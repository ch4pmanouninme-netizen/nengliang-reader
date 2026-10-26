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

// 书库列表视图（MainWindow 的 partial 部分）：分组筛选、右键菜单、批量置顶/移动/删除与书目刷新。
internal sealed partial class MainWindow
{
    // 书库左上角的分组筛选；ItemsSource 在每次 RefreshBooks 时重建。
    private readonly ComboBox groupFilter = new() { Name = "BookGroupFilter", HorizontalAlignment = HorizontalAlignment.Stretch };
    // 刷新列表期间屏蔽 SelectionChanged，避免把程序性选中当成用户切换书籍。
    private bool refreshingBooks;
    // 下拉框条目：Label 用于显示，Group 为 null 表示全部书籍、空串表示未分组。
    private sealed record GroupChoice(string Label, string? Group)
    {
        public override string ToString() => Label;
    }

    // 配置书目列表的右键处理、行模板和分组筛选交互。
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
            if (row?.ContextMenu is not { } menu) return;
            // ContextMenu.Open(control) 不会触发 Opening，菜单里留的是模板生成时的内容；
            // 多选之后模板不会重跑，所以必须在这里按当前选中集合重建，否则批量项永远不出现。
            FillBookMenu(menu, book);
            menu.Open(row);
        }, RoutingStrategies.Tunnel);
        // 让行内容撑满列表宽度，右键命中区域才会覆盖整行而不是只有文字。
        books.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) }
        });
        // 每行模板自带右键菜单；真正打开前会按当前选中集合重新填充。
        books.ItemTemplate = new FuncDataTemplate<Book>((book, _) =>
        {
            if (book == null) return new TextBlock();
            var row = new StackPanel { Spacing = 3, Margin = new Thickness(0, 2), Background = Brushes.Transparent, ContextMenu = CreateBookMenu(book) };
            row.Children.Add(new TextBlock { Text = (book.IsPinned ? "★ " : "") + book.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            // 已经按某个分组筛选时，每行再显示一遍分组名是冗余的。
            if ((groupFilter.SelectedItem as GroupChoice)?.Group == null)
                row.Children.Add(new TextBlock { Text = book.Group.Length == 0 ? "未分组" : book.Group, FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis });
            return row;
        });
        groupFilter.SelectionChanged += (_, _) =>
        {
            // 程序性刷新或忙碌期间不要再次触发刷新，否则会递归重建列表。
            if (!refreshingBooks && !busy && !dialogOpen) RefreshBooks((books.SelectedItem as Book)?.Id);
        };
    }

    // 重建分组下拉框和书目列表，并尽量保住当前选中项；allGroups 为 true 时忽略分组筛选。
    private void RefreshBooks(string? selected, bool allGroups = false)
    {
        refreshingBooks = true;
        Book? next;
        try
        {
            string? group = allGroups ? null : (groupFilter.SelectedItem as GroupChoice)?.Group;
            // 分组条目每次重建，确保「管理分组」窗口里的增删改能立即反映到筛选框。
            var choices = new List<GroupChoice> { new("全部书籍", null), new("未分组", "") };
            choices.AddRange(store.GroupNames.Select(name => new GroupChoice(name, name)));
            groupFilter.ItemsSource = choices;
            var choice = choices.FirstOrDefault(item => string.Equals(item.Group, group, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
            groupFilter.SelectedItem = choice;
            Book[] visible = store.GetBooks(choice.Group);
            // 置空再赋值以强制 ListBox 丢弃旧容器，避免多选状态和行模板残留。
            books.ItemsSource = null; books.ItemsSource = visible;
            next = visible.FirstOrDefault(book => book.Id == selected) ?? visible.FirstOrDefault();
            books.SelectedItem = next;
            if (next == null)
            {
                // 当前分组没有书时关闭阅读会话，并让进度条回到空状态。
                reader?.DisposeReader(); reader = null; session = null;
                description.Text = store.Books.Count == 0 ? "书库为空，打开 TXT / EPUB 添加书籍。" : "当前分组没有书籍。";
                ShowReadingProgress();
            }
        }
        finally { refreshingBooks = false; }
        // 列表稳定后再异步加载正文，避免加载过程反过来改动刚设置的选中项。
        if (!busy && next != null && session?.Book != next) _ = SelectBook(next);
    }

    // 当前多选的书目快照；右键批量操作以它为准。
    private Book[] SelectedBooks => books.SelectedItems?.OfType<Book>().ToArray() ?? [];

    // 右键点到的行在选中集合里且不止一本时，动作作用于整批；否则只作用于该行，且不改动选中项。
    private Book[] TargetsFor(Book book)
    {
        Book[] selected = SelectedBooks;
        return selected.Length > 1 && selected.Contains(book) ? selected : [book];
    }

    // 模板生成时先填一份菜单；真正打开前还会按最新选中集合重填。
    private ContextMenu CreateBookMenu(Book book)
    {
        var menu = new ContextMenu();
        FillBookMenu(menu, book);
        return menu;
    }

    // 菜单内容依赖“当前选中集合”，所以在真正打开之前重填一次（见上面的右键处理）。
    private void FillBookMenu(ContextMenu menu, Book book)
    {
        menu.Items.Clear();
        Book[] targets = TargetsFor(book);
        // 批量提示行只负责说明作用范围，不响应点击。
        if (targets.Length > 1)
            menu.Items.Add(new MenuItem { Header = $"以下操作作用于选中的 {targets.Length} 本书", Name = "BatchScope", IsEnabled = false });
        var remove = new MenuItem { Header = "删除", Name = "DeleteBook" };
        remove.Click += async (_, _) => await DeleteBooks(targets);
        bool allPinned = targets.All(target => target.IsPinned);
        var pin = new MenuItem { Header = allPinned ? "取消置顶" : "置顶", Name = "PinBook" };
        pin.Click += async (_, _) => await ChangeBooks(targets, () => store.SetPinnedAsync(targets, !allPinned), "置顶状态已保存。");
        // 右键只负责“把书移到哪个组”；新建分组统一走「管理分组」窗口，不在这里重复一份。
        var groups = new MenuItem { Header = "移动到分组", Name = "GroupBook" };
        void AddGroup(string label, string name)
        {
            bool current = targets.All(target => string.Equals(target.Group, name, StringComparison.OrdinalIgnoreCase));
            var item = new MenuItem { Header = (current ? "✓ " : "") + label, Tag = name };
            item.Click += async (_, _) => await ChangeBooks(targets, () => store.SetGroupAsync(targets, name), name.Length == 0 ? "已移回未分组。" : "已移至分组：" + name);
            groups.Items.Add(item);
        }
        AddGroup("未分组", ""); foreach (string name in store.GroupNames) AddGroup(name, name);
        menu.Items.Add(remove); menu.Items.Add(pin); menu.Items.Add(groups);
    }

    private async Task ManageGroups()
    {
        if (busy || dialogOpen) return;
        string? selected = (books.SelectedItem as Book)?.Id;
        // dialogOpen 覆盖整个模态窗口的生命周期，阻止其它入口在它关闭前操作书库。
        dialogOpen = true;
        try { await new GroupWindow(store).ShowDialog(this); }
        finally { dialogOpen = false; }
        // 分组可能被增删改排序，回来要重建筛选下拉框并保住当前选中项。
        RefreshBooks(selected);
    }

    // 批量置顶/移动的公共入口：先校验目标仍在书库，再在忙碌状态下执行和刷新。
    private async Task ChangeBooks(Book[] targets, Func<Task> update, string message)
    {
        if (busy || dialogOpen || targets.Any(target => !store.Books.Contains(target))) return;
        string? selected = (books.SelectedItem as Book)?.Id;
        bool changed = false;
        await RunBusy(async () => { await update(); changed = true; status.Text = message; });
        if (changed) RefreshBooks(selected);
    }

    private async Task DeleteBooks(Book[] targets)
    {
        if (busy || dialogOpen || targets.Any(target => !store.Books.Contains(target))) return;
        // 确认框也占用 dialogOpen，避免删除过程中又被右键菜单或快捷键重入。
        dialogOpen = true; bool confirmed;
        string message = targets.Length == 1
            ? $"删除《{targets[0].Title}》及它在本机书库中的副本、进度和书签？\n\n原始导入文件不会删除。此操作不可撤销。"
            : $"删除选中的 {targets.Length} 本书及它们在本机书库中的副本、进度和书签？\n\n原始导入文件不会删除。此操作不可撤销。";
        try { confirmed = await Dialogs.Confirm(this, "删除书籍", message); }
        finally { dialogOpen = false; }
        if (!confirmed) return;
        string? selected = (books.SelectedItem as Book)?.Id;
        bool removed = false;
        await RunBusy(async () =>
        {
            // 逐本删除：RemoveBookAsync 有整套针对中断与回滚的处理，复用它比另写批量版安全。
            int cleaned = 0;
            foreach (Book target in targets)
            {
                if (await store.RemoveBookAsync(target)) cleaned++;
                // 删除的是正在阅读的书时同步释放阅读窗口和会话。
                if (session?.Book == target) { reader?.DisposeReader(); reader = null; session = null; }
            }
            removed = true;
            status.Text = cleaned == targets.Length
                ? (targets.Length == 1 ? "已从书库删除：" + targets[0].Title : $"已从书库删除 {cleaned} 本书。")
                : $"只删除了 {cleaned} / {targets.Length} 本，其余的有临时文件未清理，下次启动会重试。";
        });
        if (removed) RefreshBooks(targets.Any(target => target.Id == selected) ? store.LastId : selected);
    }
}
