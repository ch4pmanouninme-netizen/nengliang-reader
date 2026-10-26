using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Reader.Core;

namespace Reader.Desktop;

// 分组管理：新建（含空分组）、重命名、删除、上移/下移。所有校验失败都在窗口内提示，
// 不像对话框那样一失败就把输入框关掉、让人重新敲一遍。
/// <summary>
/// 分组管理窗口：维护分组的增删改和显示顺序，并把操作结果即时刷新到列表。
/// </summary>
/// <remarks>所有操作都直接写入 <see cref="Store"/>；窗口本身不保存未提交的分组草稿。</remarks>
internal sealed class GroupWindow : Window
{
    // 列表项：显示分组名和书数，空分组也保留在列表中。
    private sealed record Entry(string Name, int Count)
    {
        public override string ToString() => Count == 0 ? Name + "（空分组）" : $"{Name} · {Count} 本";
    }

    private readonly Store store;
    private readonly ListBox list = new() { Name = "GroupList", MinHeight = 150 };
    private readonly TextBox input = new() { Name = "GroupNameInput", MaxLength = Store.GroupNameLimit, Watermark = "分组名称", Margin = new Thickness(0, 12, 8, 12) };
    private readonly TextBlock message = new() { Name = "GroupMessage", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };
    private readonly Button rename = new() { Name = "RenameGroup", Content = "重命名" };
    private readonly Button remove = new() { Name = "DeleteGroup", Content = "删除" };
    private readonly Button up = new() { Name = "MoveGroupUp", Content = "上移" };
    private readonly Button down = new() { Name = "MoveGroupDown", Content = "下移" };

    /// <summary>创建分组管理窗口。</summary>
    /// <param name="store">书库存储；分组的增删改都直接作用于它。</param>
    public GroupWindow(Store store)
    {
        // 名称供冒烟测试按窗口名定位这个模态对话框。
        Name = "GroupDialog";
        this.store = store;
        Title = "管理分组"; Icon = AppIcon.Shared;
        Width = 460; SizeToContent = SizeToContent.Height; MinHeight = 320; MaxHeight = 650;
        CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var rows = new StackPanel { Margin = new Thickness(24), Spacing = 8 };
        rows.Children.Add(new TextBlock { Text = "分组按这里的顺序显示在下拉框里；空分组也会保留。删除分组只把它下面的书移回未分组，不会删书。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray });
        var createRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        createRow.Children.Add(input);
        var create = new Button { Name = "CreateGroup", Content = "新建" };
        Grid.SetColumn(create, 1); createRow.Children.Add(create);
        rows.Children.Add(createRow);
        rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { up, down, rename, remove } });
        rows.Children.Add(list);
        rows.Children.Add(message);
        var close = new Button { Name = "CloseGroupDialog", Content = "关闭", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Left };
        rows.Children.Add(close);
        Content = rows;

        create.Click += async (_, _) => await Apply(store.CreateGroupAsync(input.Text), "", "已新建分组。");
        rename.Click += async (_, _) =>
        {
            string from = Selected;
            if (from.Length == 0) return;
            string to = (input.Text ?? "").Trim();
            await Apply(store.RenameGroupAsync(from, to), to, "已重命名。");
        };
        remove.Click += async (_, _) =>
        {
            string name = Selected;
            if (name.Length == 0) return;
            int count = store.GetBooks(name).Length;
            await Apply(store.RemoveGroupAsync(name), null,
                count == 0 ? $"已删除空分组“{name}”。" : $"已删除分组“{name}”，{count} 本书移回未分组。");
        };
        up.Click += async (_, _) => await Move(-1);
        down.Click += async (_, _) => await Move(1);
        close.Click += (_, _) => Close();
        list.SelectionChanged += (_, _) =>
        {
            // 选中哪个分组，名称框就填哪个，方便直接改名；没有选中就清空。
            if (Selected.Length > 0) input.Text = Selected;
            UpdateEnabled();
        };
        Refresh(null);
    }

    // 当前选中的分组名；未选中时为空串，各操作据此直接返回。
    private string Selected => (list.SelectedItem as Entry)?.Name ?? "";

    // step 为 -1 上移、1 下移；越界时 Store 会返回提示而不是抛异常。
    private async Task Move(int step)
    {
        string name = Selected;
        if (name.Length == 0) return;
        await Apply(store.MoveGroupAsync(name, step), name, "已移动。");
    }

    // 失败时保留输入内容，让用户就地改；成功则按 keep 重新定位选中项。
    private async Task Apply(Task<string?> operation, string? keep, string success)
    {
        string? failure;
        try { failure = await operation; }
        catch (Exception ex) { failure = ex.Message; }
        message.Foreground = failure == null ? Brushes.Gray : Brushes.OrangeRed;
        message.Text = failure ?? success;
        if (failure == null) Refresh(keep);
        UpdateEnabled();
    }

    private void Refresh(string? keep)
    {
        string? selection = keep ?? (list.SelectedItem as Entry)?.Name;
        // 一次遍历统计每组的书数；不要再对每个分组调用 GetBooks（过滤 + 排序 + 分配）。
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var book in store.Books)
            if (book.Group.Length > 0) counts[book.Group] = counts.TryGetValue(book.Group, out int count) ? count + 1 : 1;
        Entry[] entries = store.GroupNames.Select(name => new Entry(name, counts.TryGetValue(name, out int count) ? count : 0)).ToArray();
        list.ItemsSource = entries;
        list.SelectedItem = entries.FirstOrDefault(entry => string.Equals(entry.Name, selection, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault();
    }

    // 重命名/删除需要选中项；上移/下移还要检查是否已在列表边界。
    private void UpdateEnabled()
    {
        int index = list.SelectedIndex;
        bool has = index >= 0;
        rename.IsEnabled = remove.IsEnabled = has;
        up.IsEnabled = has && index > 0;
        down.IsEnabled = has && index >= 0 && index < list.ItemCount - 1;
    }
}
