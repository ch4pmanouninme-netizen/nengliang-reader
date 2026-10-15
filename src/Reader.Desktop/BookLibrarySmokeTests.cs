using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using Reader.Core;

namespace Reader.Desktop;

internal sealed partial class MainWindow
{
    private async Task RunBookLibrarySmoke()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Book library UI: " + message); checks++; }
        async Task Wait(Func<bool> condition)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
            Check(condition(), "timed out waiting for book operation");
        }
        MenuItem Item(ContextMenu menu, string name) => menu.Items.OfType<MenuItem>().Single(item => item.Name == name);
        void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        async Task<ContextMenu> RowMenu(Book target)
        {
            books.ScrollIntoView(target); await Task.Delay(100);
            var container = books.ContainerFromItem(target) as Control;
            Check(container != null, "book has a realized list row");
            var row = container!.GetVisualDescendants().OfType<Control>().First(control => control.ContextMenu != null);
            Check(row.Bounds.Width >= container!.Bounds.Width - 30, "right-click target covers the book row width");
            var menu = row.ContextMenu!;
            object? selected = books.SelectedItem;
            var pointer = new Pointer(1, PointerType.Mouse, true);
            var point = row.TranslatePoint(new Avalonia.Point(8, 8), this) ?? new Avalonia.Point(0, 0);
            row.RaiseEvent(new PointerPressedEventArgs(row, pointer, this, point, 0,
                new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None, 1));
            await Task.Delay(60);
            Check(books.SelectedItem == selected && !busy, "right mouse event preserves reading selection and does not block its menu");
            Check(menu.IsOpen && Item(menu, "DeleteBook").Header?.ToString() == "删除", "right-click menu opens on the actual book row");
            return menu;
        }
        async Task AnswerDelete(bool confirm)
        {
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
            await Wait(() => lifetime.Windows.Any(window => window.Name == "ConfirmationDialog"));
            var dialog = lifetime.Windows.Single(window => window.Name == "ConfirmationDialog");
            var button = dialog.GetVisualDescendants().OfType<Button>().Single(item => item.Name == (confirm ? "Confirm" : "Cancel"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        Book active = session!.Book;
        active.Position = 2; active.Bookmark = 4; MarkDirty();
        var target = store.Add("书库操作测试", "右键操作只影响对应的书。\n用于置顶分组和删除测试。", "TXT");
        busy = true; RefreshBooks(active.Id, true); busy = false;
        var menu = await RowMenu(target); menu.Close(); Click(Item(menu, "PinBook"));
        await Wait(() => !busy && target.IsPinned);
        Check(store.GetBooks().First() == target && session?.Book == active && active.Position == 2, "pinning a different row preserves the active book and reading progress");
        menu = await RowMenu(target); Check(Item(menu, "PinBook").Header?.ToString() == "取消置顶", "pinned row offers unpin"); menu.Close(); Click(Item(menu, "PinBook"));
        await Wait(() => !busy && !target.IsPinned);

        // 分组只在「管理分组」窗口里新建，右键只负责把书移进去。
        var appLifetime = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
        await store.CreateGroupAsync("测试分组");
        menu = await RowMenu(target); menu.Close();
        Check(Item(menu, "GroupBook").Header?.ToString() == "移动到分组", "the submenu is a move action, not a group editor");
        Check(!Item(menu, "GroupBook").Items.OfType<MenuItem>().Any(item => item.Name == "NewBookGroup"), "the move submenu no longer carries new-group");
        Click(Item(menu, "GroupBook").Items.OfType<MenuItem>().Single(item => item.Tag is string group && group == "测试分组"));
        await Wait(() => !busy && target.Group == "测试分组");
        Check(new Store(store.Root).Books.Single(book => book.Id == target.Id).Group == "测试分组", "moving a book into a group persists");
        groupFilter.SelectedItem = groupFilter.Items.OfType<GroupChoice>().Single(choice => choice.Group == "测试分组");
        await Wait(() => !busy && session?.Book == target);
        Check(books.Items.Cast<Book>().SequenceEqual(new[] { target }), "group selector filters the visible shelf");
        menu = await RowMenu(target); menu.Close();
        Click(Item(menu, "GroupBook").Items.OfType<MenuItem>().Single(item => item.Tag is string group && group == ""));
        await Wait(() => !busy && target.Group == "");
        // 分组现在是独立实体：把最后一本书移走，它作为空分组留下来。
        Check(groupFilter.Items.OfType<GroupChoice>().Any(choice => choice.Group == "测试分组"), "an emptied group survives as an empty group");
        Check(store.GetBooks("测试分组").Length == 0, "the emptied group holds no books");

        // 多选是为了批量操作，但不能把正在阅读的书换掉。
        var batchA = store.Add("批量甲", "批量操作的甲。", "TXT");
        var batchB = store.Add("批量乙", "批量操作的乙。", "TXT");
        busy = true; RefreshBooks(active.Id, true); busy = false; await SelectBook(active);
        books.SelectedItems!.Add(batchA); books.SelectedItems.Add(batchB);
        await Task.Delay(150);
        Check(books.SelectedItems.Count == 3 && session?.Book == active, "selecting several books leaves the reading session on the current book");
        await store.CreateGroupAsync("批量组");
        menu = await RowMenu(batchA); menu.Close();
        Check(menu.Items.OfType<MenuItem>().Any(item => item.Name == "BatchScope"), "the row menu announces that the action covers the whole selection");
        Click(Item(menu, "GroupBook").Items.OfType<MenuItem>().Single(item => item.Tag is string group && group == "批量组"));
        await Wait(() => !busy && batchA.Group == "批量组" && batchB.Group == "批量组" && active.Group == "批量组");
        Check(active.Group == "批量组", "batch grouping covers every selected book");

        // 分组管理窗口：重名拒绝、空分组、重命名、排序、删除后书回未分组。
        var managed = store.Add("受管理书", "分组管理窗口的测试书。", "TXT");
        await store.SetGroupAsync(managed, "受管理组");
        busy = true; RefreshBooks(active.Id, true); busy = false;
        _ = ManageGroups();
        await Wait(() => appLifetime.Windows.Any(window => window.Name == "GroupDialog"));
        var groupUi = appLifetime.Windows.Single(window => window.Name == "GroupDialog");
        Button GroupButton(string name) => groupUi.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
        TextBox nameInput = groupUi.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "GroupNameInput");
        ListBox groupList = groupUi.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "GroupList");
        TextBlock groupMessage = groupUi.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "GroupMessage");
        // store 先变、窗口后刷，直接等 store 会在刷新之前返回，随后的选择就被迟到的 Refresh 复位。
        // ItemsSource 每次刷新都换新实例，拿它当“这次操作已经落到界面上”的确定信号。
        async Task Operate(string button)
        {
            object? before = groupList.ItemsSource;
            GroupButton(button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !ReferenceEquals(groupList.ItemsSource, before));
        }

        nameInput.Text = "受管理组";
        GroupButton("CreateGroup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => groupMessage.Text?.Contains("已存在") == true);
        Check(store.GroupNames.Count(name => name == "受管理组") == 1, "the window refuses a duplicate group name and says why");

        nameInput.Text = "空管理组";
        await Operate("CreateGroup");
        Check(store.GetBooks("空管理组").Length == 0, "the window creates an empty group");

        groupList.SelectedIndex = Array.IndexOf(store.GroupNames, "受管理组");
        await Task.Delay(80);
        nameInput.Text = "改名后的组";
        await Operate("RenameGroup");
        Check(store.GroupNames.Contains("改名后的组") && managed.Group == "改名后的组", "renaming in the window carries its books along");

        string[] beforeMove = store.GroupNames;
        int at = Array.IndexOf(beforeMove, "改名后的组");
        Check(at > 0, "the renamed group is not already first");
        await Operate("MoveGroupUp");
        Check(store.GroupNames[at - 1] == "改名后的组" && store.GroupNames[at] == beforeMove[at - 1], "the window swaps a group with its neighbour");

        await Operate("DeleteGroup");
        Check(!store.GroupNames.Contains("改名后的组") && managed.Group == "" && store.Books.Contains(managed), "deleting a group in the window moves its books back to ungrouped without deleting them");

        GroupButton("CloseGroupDialog").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => !dialogOpen && appLifetime.Windows.All(window => window.Name != "GroupDialog"));
        Check(groupFilter.Items.OfType<GroupChoice>().Any(choice => choice.Group == "空管理组"), "the shelf filter picks up groups created in the window");

        busy = true; RefreshBooks(active.Id, true); busy = false; await SelectBook(active);
        menu = await RowMenu(target); menu.Close(); Click(Item(menu, "DeleteBook")); await AnswerDelete(false);
        await Wait(() => !dialogOpen);
        Check(store.Books.Contains(target) && File.Exists(Path.Combine(store.Root, "books", target.Id + ".txt")), "cancelled deletion retains book and cached text");
        menu = await RowMenu(target); menu.Close(); Click(Item(menu, "DeleteBook")); await AnswerDelete(true);
        await Wait(() => !busy && !dialogOpen && !store.Books.Contains(target));
        Check(session?.Book == active && active.Bookmark == 4, "deleting a different row leaves active reading and bookmark intact");

        var current = store.Add("当前阅读删除测试", "删除正在阅读的文本时关闭旧阅读条。", "TXT");
        busy = true; RefreshBooks(current.Id, true); busy = false; await SelectBook(current); ShowReader();
        ReaderWindow? previousReader = reader; RestoreLibrary(); await Task.Delay(80);
        menu = await RowMenu(current); menu.Close(); Click(Item(menu, "DeleteBook")); await AnswerDelete(true);
        await Wait(() => !busy && !store.Books.Contains(current));
        Check(reader == null && session?.Book != current && previousReader?.IsVisible != true, "deleting current book disposes its reader and moves to a surviving book");

        await store.SetPinnedAsync(active, true); await store.SetGroupAsync(active, "小说");
        busy = true; RefreshBooks(active.Id, true); busy = false; await SelectBook(active);
        await Task.Delay(100); Capture(this, "book-library.png");
        menu = await RowMenu(active);
        if (menu.GetVisualRoot() is Control popup)
        {
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)popup.Bounds.Width, (int)popup.Bounds.Height), new Avalonia.Vector(96, 96));
            bitmap.Render(popup); bitmap.Save(Path.Combine(store.Root, "book-context-menu.png"));
        }
        menu.Close();
        Check(new Store(store.Root).Books.All(book => book.Id != target.Id && book.Id != current.Id), "deleted rows remain absent after reloading saved state");
        File.WriteAllText(Path.Combine(store.Root, "book-library-smoke.ok"), $"{checks} checks: actual row menus, pin/unpin, new groups, filtering, ungrouping, cancel/confirm deletion, active reader cleanup, persistence.");
    }
}
