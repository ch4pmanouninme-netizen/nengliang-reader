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

        menu = await RowMenu(target); menu.Close();
        Click(Item(menu, "GroupBook").Items.OfType<MenuItem>().Single(item => item.Name == "NewBookGroup"));
        var appLifetime = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
        await Wait(() => appLifetime.Windows.Any(window => window.Title == "新建分组"));
        var groupDialog = appLifetime.Windows.Single(window => window.Title == "新建分组");
        groupDialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "测试分组";
        groupDialog.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == "确定").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => !busy && !dialogOpen && target.Group == "测试分组");
        Check(new Store(store.Root).Books.Single(book => book.Id == target.Id).Group == "测试分组", "new-group menu persists organization");
        groupFilter.SelectedItem = groupFilter.Items.OfType<GroupChoice>().Single(choice => choice.Group == "测试分组");
        await Wait(() => !busy && session?.Book == target);
        Check(books.Items.Cast<Book>().SequenceEqual(new[] { target }), "group selector filters the visible shelf");
        menu = await RowMenu(target); menu.Close();
        Click(Item(menu, "GroupBook").Items.OfType<MenuItem>().Single(item => item.Tag is string group && group == ""));
        await Wait(() => !busy && target.Group == "");
        Check(groupFilter.Items.OfType<GroupChoice>().All(choice => choice.Group != "测试分组"), "moving the last book out removes an unused group");

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
