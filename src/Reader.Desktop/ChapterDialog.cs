using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Reader.Core;

namespace Reader.Desktop;

/// <summary>
/// 章节选择窗口。列表可能包含上千章，因此提供标题筛选，并依赖 ListBox 的虚拟化；
/// 打开时自动选中当前阅读位置之前最近的章节。
/// </summary>
internal sealed class ChapterDialog : Window
{
    private readonly IReadOnlyList<Chapter> all;
    private readonly ListBox list = new();
    private readonly TextBox filter = new() { Watermark = "筛选章节标题，例如“第 12 章”或章节名", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock count = new() { Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button jump = new() { Content = "跳转", IsDefault = true };

    public ChapterDialog(IReadOnlyList<Chapter> chapters, int position)
    {
        all = chapters;
        Title = "选择章节"; Icon = AppIcon.Shared;
        Width = 520; Height = 620; MinWidth = 380; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        list.ItemTemplate = new FuncDataTemplate<Chapter>((chapter, _) => chapter == null ? new TextBlock() : new TextBlock
        {
            Text = chapter.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 1),
        });
        // 双击或回车直接跳转；取消保持当前位置。
        list.DoubleTapped += (_, _) => Accept();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
        jump.Click += (_, _) => Accept();
        var cancel = new Button { Content = "取消", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        filter.TextChanged += (_, _) => ApplyFilter();

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetColumn(count, 0); bottom.Children.Add(count);
        jump.Margin = new Thickness(0, 0, 12, 0); Grid.SetColumn(jump, 1); bottom.Children.Add(jump);
        Grid.SetColumn(cancel, 2); bottom.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(filter, Dock.Top); root.Children.Add(filter);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(list);
        Content = root;

        // 当前阅读位置落在哪一章：取位置不超过当前位置的最后一个标题。
        Chapter? current = null;
        foreach (Chapter chapter in all)
        {
            if (chapter.Position > position) break;
            current = chapter;
        }
        ApplyFilter();
        Opened += (_, _) =>
        {
            if (current != null)
            {
                list.SelectedItem = current;
                // 等列表完成一次布局后再滚动，否则虚拟化容器还没生成，ScrollIntoView 不生效。
                Dispatcher.UIThread.Post(() => list.ScrollIntoView(current), DispatcherPriority.Background);
            }
            filter.Focus();
        };
    }

    private void ApplyFilter()
    {
        string query = (filter.Text ?? "").Trim();
        var items = query.Length == 0
            ? all
            : all.Where(chapter => chapter.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        list.ItemsSource = items;
        count.Text = query.Length == 0 ? $"共 {all.Count} 章" : $"匹配 {items.Count} / {all.Count} 章";
        // 筛选后默认选中第一条，方便直接回车跳转。
        if (query.Length > 0 && items.Count > 0) list.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (list.SelectedItem is Chapter chapter) Close(chapter);
    }
}
