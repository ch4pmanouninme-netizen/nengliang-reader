using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Reader.Core;

namespace Reader.Desktop;

internal sealed partial class MainWindow
{
    private readonly ComboBox groupFilter = new() { Name = "BookGroupFilter", HorizontalAlignment = HorizontalAlignment.Stretch };
    private bool refreshingBooks;
    private sealed record GroupChoice(string Label, string? Group)
    {
        public override string ToString() => Label;
    }

    private void ConfigureBookLibrary()
    {
        books.Name = "BookLibrary";
        books.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(books).Properties.IsRightButtonPressed || e.Source is not Control source || source.DataContext is not Book book) return;
            // A right-click opens the target row's menu without starting asynchronous reading selection first.
            e.Handled = true;
            if (busy || dialogOpen || !store.Books.Contains(book)) return;
            var row = (books.ContainerFromItem(book) as Control)?.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.ContextMenu != null);
            row?.ContextMenu?.Open(row);
        }, RoutingStrategies.Tunnel);
        books.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) }
        });
        books.ItemTemplate = new FuncDataTemplate<Book>((book, _) =>
        {
            if (book == null) return new TextBlock();
            return new StackPanel { Spacing = 3, Margin = new Thickness(0, 2), Background = Brushes.Transparent, ContextMenu = CreateBookMenu(book), Children = {
                new TextBlock { Text = (book.IsPinned ? "置顶 · " : "") + book.Title, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = book.Group.Length == 0 ? "未分组" : book.Group, FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis }
            } };
        });
        groupFilter.SelectionChanged += (_, _) =>
        {
            if (!refreshingBooks && !busy && !dialogOpen) RefreshBooks((books.SelectedItem as Book)?.Id);
        };
    }

    private void RefreshBooks(string? selected, bool allGroups = false)
    {
        refreshingBooks = true;
        Book? next;
        try
        {
            string? group = allGroups ? null : (groupFilter.SelectedItem as GroupChoice)?.Group;
            var choices = new List<GroupChoice> { new("全部书籍", null), new("未分组", "") };
            choices.AddRange(store.GroupNames.Select(name => new GroupChoice(name, name)));
            groupFilter.ItemsSource = choices;
            var choice = choices.FirstOrDefault(item => string.Equals(item.Group, group, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
            groupFilter.SelectedItem = choice;
            Book[] visible = store.GetBooks(choice.Group);
            books.ItemsSource = null; books.ItemsSource = visible;
            next = visible.FirstOrDefault(book => book.Id == selected) ?? visible.FirstOrDefault();
            books.SelectedItem = next;
            if (next == null)
            {
                reader?.DisposeReader(); reader = null; session = null;
                description.Text = store.Books.Count == 0 ? "书库为空，打开 TXT / EPUB 添加书籍。" : "当前分组没有书籍。"; progress.Value = 0;
            }
        }
        finally { refreshingBooks = false; }
        if (!busy && next != null && session?.Book != next) _ = SelectBook(next);
    }

    private ContextMenu CreateBookMenu(Book book)
    {
        var menu = new ContextMenu();
        void Rebuild()
        {
            menu.Items.Clear();
            var remove = new MenuItem { Header = "删除", Name = "DeleteBook" };
            remove.Click += async (_, _) => await DeleteBook(book);
            var pin = new MenuItem { Header = book.IsPinned ? "取消置顶" : "置顶", Name = "PinBook" };
            pin.Click += async (_, _) => await ChangeBook(book, () => store.SetPinnedAsync(book, !book.IsPinned), "置顶状态已保存。");
            var groups = new MenuItem { Header = "分组", Name = "GroupBook" };
            var create = new MenuItem { Header = "新建分组…", Name = "NewBookGroup" };
            create.Click += async (_, _) => await NewBookGroup(book); groups.Items.Add(create); groups.Items.Add(new Separator());
            void AddGroup(string label, string name)
            {
                var item = new MenuItem { Header = (string.Equals(book.Group, name, StringComparison.OrdinalIgnoreCase) ? "✓ " : "") + label, Tag = name };
                item.Click += async (_, _) => await ChangeBook(book, () => store.SetGroupAsync(book, name), name.Length == 0 ? "已移回未分组。" : "已移至分组：" + name);
                groups.Items.Add(item);
            }
            AddGroup("未分组", ""); foreach (string name in store.GroupNames) AddGroup(name, name);
            menu.Items.Add(remove); menu.Items.Add(pin); menu.Items.Add(groups);
        }
        Rebuild();
        menu.Opening += (_, e) => { if (busy || dialogOpen || !store.Books.Contains(book)) e.Cancel = true; else Rebuild(); };
        return menu;
    }

    private async Task ChangeBook(Book book, Func<Task> update, string message)
    {
        if (busy || dialogOpen || !store.Books.Contains(book)) return;
        string? selected = (books.SelectedItem as Book)?.Id;
        bool changed = false;
        await RunBusy(async () => { await update(); changed = true; status.Text = message; });
        if (changed) RefreshBooks(selected);
    }

    private async Task NewBookGroup(Book book)
    {
        if (busy || dialogOpen || !store.Books.Contains(book)) return;
        dialogOpen = true; string? name;
        try { name = await Dialogs.Prompt(this, "新建分组", $"为《{book.Title}》设置分组，最多 {Store.GroupNameLimit} 个字符。", book.Group); }
        finally { dialogOpen = false; }
        if (string.IsNullOrWhiteSpace(name)) return;
        await ChangeBook(book, () => store.SetGroupAsync(book, name), "分组已保存。");
    }

    private async Task DeleteBook(Book book)
    {
        if (busy || dialogOpen || !store.Books.Contains(book)) return;
        dialogOpen = true; bool confirmed;
        try { confirmed = await Dialogs.Confirm(this, "删除书籍", $"删除《{book.Title}》及它在本机书库中的副本、进度和书签？\n\n原始导入文件不会删除。此操作不可撤销。"); }
        finally { dialogOpen = false; }
        if (!confirmed) return;
        string? selected = (books.SelectedItem as Book)?.Id;
        bool removed = false;
        await RunBusy(async () =>
        {
            bool cleaned = await store.RemoveBookAsync(book); removed = true;
            if (session?.Book == book) { reader?.DisposeReader(); reader = null; session = null; }
            status.Text = cleaned ? "已从书库删除：" + book.Title : "书籍已删除，有临时文件尚未清理，下次启动会重试。";
        });
        if (removed) RefreshBooks(selected == book.Id ? store.LastId : selected);
    }
}
