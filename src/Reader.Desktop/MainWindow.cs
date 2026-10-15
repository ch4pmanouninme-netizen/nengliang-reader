using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Reader.Core;

namespace Reader.Desktop;

internal sealed partial class MainWindow : Window
{
    private readonly Store store;
    // 多选是为了批量操作；「正在阅读的书」仍由 SelectedBooks 恰好 1 本时决定。
    private readonly ListBox books = new() { MinWidth = 160, SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock description = new() { Text = "打开 TXT / EPUB，或将文件拖到窗口。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Height = 6 };
    private readonly TextBlock progressLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly WrapPanel actions = new() { Orientation = Orientation.Horizontal };
    private readonly Button cancel = new() { Content = "取消下载", IsVisible = false };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DownloadService downloads = new();
    private Hotkeys? hotkeys;
    private ReaderWindow? reader;
    private ReadingSession? session;
    private CancellationTokenSource? downloadCancellation;
    private bool busy, dirty, exiting, dialogOpen, initialized, integrationStarted;
    private int revision;
    private Task<bool>? pendingSave;
    public Action? ShowHome { get; set; }
    public Action? HideAll { get; set; }
    public Action? ThemeChanged { get; set; }
    internal HomeWindow? SmokeHome { get; set; }
    public Window? VisibleReader => reader?.IsVisible == true ? reader : null;
    public void PauseReader() => reader?.Pause();
    public void SavePreferences() { MarkDirty(); Save(); }
    public void RefreshTheme() => ApplyTheme();
    public MainWindow(Store store)
    {
        this.store = store;
        Title = "良子阅读器"; Width = 780; Height = 500; MinWidth = 600; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(24), LastChildFill = true };
        var header = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(new TextBlock { Text = "良子阅读器", FontSize = 24, FontWeight = FontWeight.SemiBold });
        header.Children.Add(new TextBlock { Text = "把故事放在手边。TXT · EPUB · 悬浮阅读", Foreground = Brushes.Gray });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        ButtonAction("主页", () => ShowHome?.Invoke());
        // 动作入口统一走 ButtonAction，包括带下拉的「书库备份」，保证一排按钮样式一致。
        ButtonAction("导入", () => _ = ImportDialog()); ButtonAction("链接下载", () => _ = DownloadDialog());
        ButtonAction("显示", ShowReader); ButtonAction("设置", () => _ = Settings());
        ButtonAction("快捷键", () => _ = EditHotkeys());
        var backup = new MenuFlyout();
        var export = new MenuItem { Header = "导出书库" }; export.Click += async (_, _) => await Archive(false);
        var importBackup = new MenuItem { Header = "导入书库" }; importBackup.Click += async (_, _) => await Archive(true);
        backup.Items.Add(export); backup.Items.Add(importBackup);
        ButtonAction("书库备份").Flyout = backup;
        cancel.Click += (_, _) => downloadCancellation?.Cancel();
        var progressRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(progress, 0); progressRow.Children.Add(progress);
        Grid.SetColumn(progressLabel, 1); progressRow.Children.Add(progressLabel);
        var bottom = new StackPanel { Spacing = 10, Margin = new Thickness(0, 16, 0, 0), Children = { actions, cancel, progressRow, status } };
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("190,20,*") };
        var shelf = new DockPanel { LastChildFill = true };
        var filterRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        filterRow.Children.Add(groupFilter);
        var manageGroups = new Button { Name = "ManageGroups", Content = "管理分组", Margin = new Thickness(8, 0, 0, 0) };
        manageGroups.Click += async (_, _) => await ManageGroups();
        Grid.SetColumn(manageGroups, 1); filterRow.Children.Add(manageGroups);
        DockPanel.SetDock(filterRow, Dock.Top);
        shelf.Children.Add(filterRow); shelf.Children.Add(books);
        grid.Children.Add(shelf); Grid.SetColumn(description, 2); grid.Children.Add(description); root.Children.Add(grid);
        ConfigureBookLibrary();
        Content = root;
        books.SelectionChanged += async (_, _) =>
        {
            // 「正在阅读的书」与「选中的书」是两回事：多选是为了批量操作。
            // 只有恰好选中一本时才切换正文，否则 Ctrl 点选会一本本地把正文换过去。
            if (busy || refreshingBooks || SelectedBooks.Length != 1) return;
            if (SelectedBooks[0] is Book book && session?.Book != book) await SelectBook(book);
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = !busy && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths != null) await ImportFiles(paths);
        });
        saveTimer.Tick += (_, _) => { if (dirty && !busy && !dialogOpen) Save(); };
        saveTimer.Start();
        Closing += (_, e) =>
        {
            if (!exiting) { e.Cancel = true; _ = CloseModule(); }
        };
        Opened += async (_, _) =>
        {
            if (initialized) return;
            initialized = true;
            ApplyTheme();
            if (!Program.SmokeTest) RefreshBooks(store.LastId);
            if (store.RecoveryNotice.Length > 0) await Dialogs.Message(this, "恢复提示", store.RecoveryNotice);
            if (Program.SmokeTest) await SmokeTest();
        };
    }

    // 返回按钮，便于给「书库备份」挂下拉菜单，而不必另起一套样式。
    private Button ButtonAction(string label, Action? action = null)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
        if (action != null) button.Click += (_, _) => action();
        actions.Children.Add(button);
        return button;
    }

    public void StartIntegration()
    {
        if (Program.SmokeTest || integrationStarted) return;
        integrationStarted = true;
        try { hotkeys = new Hotkeys(store.Root, HandleHotkey); }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    private void HandleHotkey(int id)
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) return;
        switch (id)
        {
            case 1: if (HideAll != null) HideAll(); else ToggleReader(); break;
            case 2: if (reader?.IsVisible == true) reader.Move(true); break;
            case 3: if (reader?.IsVisible == true) reader.Move(false); break;
            case 4: ToggleClickThrough(); break;
            case 5: if (reader?.IsVisible == true) reader.ToggleAuto(); break;
        }
    }

    private async Task SelectBook(Book book)
    {
        await RunBusy(async () =>
        {
            reader?.DisposeReader(); reader = null; session = null;
            string text = await Task.Run(() => store.TextFor(book));
            session = new ReadingSession(book, text); store.LastId = book.Id; Changed();
        });
    }

    public void Changed()
    {
        MarkDirty();
        if (session == null) return;
        description.Text = session.Book.Title + "\n" + session.Book.Format + " · " + session.Percent + "%\n\n" +
            session.Text.Substring(session.Position, Math.Min(250, session.Text.Length - session.Position));
        ShowReadingProgress();
    }

    // 同一根进度条兼作阅读进度与下载进度，文案必须说清当前是哪一种。
    private void ShowReadingProgress()
    {
        progress.Value = session?.Percent ?? 0;
        progressLabel.Text = session == null ? "" : $"阅读 {session.Percent}%";
    }

    private async Task ImportDialog()
    {
        if (busy || dialogOpen) return;
        dialogOpen = true;
        IReadOnlyList<IStorageFile> files;
        try { files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "打开电子书", AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("电子书") { Patterns = new[] { "*.txt", "*.epub" } } } }); }
        finally { dialogOpen = false; }
        await ImportFiles(files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray());
    }

    private async Task ImportFiles(string[] paths)
    {
        if (paths.Length == 0 || busy || dialogOpen) return;
        string? selected = null;
        await RunBusy(async () =>
        {
            List<string> failures = [];
            foreach (string path in paths)
            {
                status.Text = "正在导入 " + Path.GetFileName(path);
                try
                {
                    var imported = await Task.Run(() => Importer.Read(path));
                    var book = await Task.Run(() => store.Add(imported.Title, imported.Text, imported.Format));
                    selected = book.Id; MarkDirty();
                }
                catch (Exception ex) { failures.Add(Path.GetFileName(path) + "：" + ex.Message); }
            }
            status.Text = failures.Count == 0 ? "导入完成，阅读进度保存在本机。" : string.Join("\n", failures);
        });
        if (selected != null) RefreshBooks(selected, true);
        Save();
    }

    private async Task DownloadDialog()
    {
        if (busy || dialogOpen) return;
        dialogOpen = true;
        string? address;
        try { address = await Dialogs.Prompt(this, "链接下载", "粘贴无需登录的 TXT / EPUB 文件下载链接。普通小说网页不支持。"); }
        finally { dialogOpen = false; }
        if (string.IsNullOrWhiteSpace(address)) return;
        string? selected = null;
        await RunBusy(async () =>
        {
            using var cancellation = new CancellationTokenSource(); downloadCancellation = cancellation; cancel.IsVisible = true;
            var updates = new Progress<DownloadProgress>(p =>
            {
                status.Text = $"已下载 {p.Bytes / 1024.0:N0} KB" + (p.Total.HasValue ? $" / {p.Total / 1024.0:N0} KB" : "");
                progress.IsIndeterminate = !p.Total.HasValue;
                progressLabel.Text = p.Total > 0 ? $"下载 {100.0 * p.Bytes / p.Total.Value:0}%" : "下载中 …";
                if (p.Total > 0) progress.Value = 100.0 * p.Bytes / p.Total.Value;
            });
            try
            {
                var imported = await downloads.DownloadAsync(address, updates, cancellation.Token);
                var book = await Task.Run(() => store.Add(imported.Title, imported.Text, imported.Format));
                selected = book.Id; MarkDirty(); status.Text = "下载完成，已加入书架。";
            }
            catch (OperationCanceledException) { status.Text = "下载已取消。"; }
            // 下载结束后把进度条和文案交还阅读进度，取消下载时不会留在“下载 xx%”。
            finally { downloadCancellation = null; cancel.IsVisible = false; progress.IsIndeterminate = false; ShowReadingProgress(); }
        });
        if (selected != null) RefreshBooks(selected, true);
        Save();
    }

    private async Task RunBusy(Func<Task> operation)
    {
        if (busy) return;
        busy = true; actions.IsEnabled = books.IsEnabled = groupFilter.IsEnabled = false; reader?.Pause(); reader?.Hide();
        try { if (pendingSave != null) await pendingSave; await operation(); }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; actions.IsEnabled = books.IsEnabled = groupFilter.IsEnabled = true; }
    }

    private async Task Archive(bool import)
    {
        if (busy || dialogOpen) return;
        dialogOpen = true;
        string? path = null;
        try
        {
            if (import)
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入书库备份", AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("书库备份") { Patterns = new[] { "*.zip" } } } });
                path = files.FirstOrDefault()?.TryGetLocalPath();
            }
            else
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "导出书库", SuggestedFileName = "NengliangReader-backup.zip", DefaultExtension = "zip" });
                path = file?.TryGetLocalPath();
            }
        }
        finally { dialogOpen = false; }
        if (path == null) return;
        await RunBusy(async () =>
        {
            if (import) { int count = await Task.Run(() => LibraryArchive.Import(store, path)); status.Text = $"已导入 {count} 本书；重复书籍保留现有进度。"; }
            else { await Task.Run(() => LibraryArchive.Export(store, path)); status.Text = "书库已导出。"; }
        });
        if (import) RefreshBooks(store.LastId);
    }

    public void ShowReader()
    {
        if (session == null || busy || dialogOpen) return;
        try
        {
            reader ??= new ReaderWindow(store.Pref, session, this);
            reader.Show(); reader.EnsureVisible(); reader.ApplyPreferences(); Hide();
        }
        catch (Exception ex) { status.Text = "无法打开阅读条：" + ex.Message; }
    }

    public void ToggleReader()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) return;
        if (reader?.IsVisible == true) { reader.Pause(); reader.Hide(); Save(); }
        else if (session != null) ShowReader();
        else RestoreLibrary();
    }

    public void RestoreLibrary()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) return;
        if (reader != null)
        {
            reader.Pause();
            try { reader.SetClickThrough(false); } catch (Exception ex) { status.Text = "穿透恢复失败：" + ex.Message; }
            reader.Hide();
        }
        store.Pref.ClickThrough = false;
        Show(); WindowState = WindowState.Normal; Activate();
        if (session == null && books.SelectedItem is Book selected && !busy) _ = SelectBook(selected);
    }

    public void ToggleClickThrough()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true || reader?.IsVisible != true) return;
        try { reader.SetClickThrough(!store.Pref.ClickThrough); MarkDirty(); }
        catch (Exception ex) { RestoreLibrary(); status.Text = "无法切换穿透：" + ex.Message; }
    }

    public async Task Settings()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) return;
        RestoreLibrary(); dialogOpen = true;
        try { if (await new SettingsWindow(store.Pref).ShowDialog<bool>(this)) { reader?.ApplyPreferences(); ApplyTheme(); ThemeChanged?.Invoke(); MarkDirty(); Save(); } }
        finally { dialogOpen = false; }
    }

    private async Task EditHotkeys()
    {
        // 热键不可用时要说出来：静默返回会让「快捷键」按钮看起来只是没反应。
        if (hotkeys == null) { status.Text = "全局快捷键不可用；可通过托盘 / 菜单栏操作。"; return; }
        if (busy || dialogOpen) return;
        dialogOpen = true;
        try
        {
            // 录入要读原始按键，而全局热键是系统级注册、会抢先收到，先停掉再开窗口。
            hotkeys.Suspend();
            string[]? chosen = null;
            try
            {
                // 注册失败的功能在窗口里就地标注，这是它唯一的显示位置。
                var window = new HotkeyWindow(hotkeys.Keys, hotkeys.FailedRows);
                if (await window.ShowDialog<bool>(this)) chosen = window.Result;
            }
            finally
            {
                try { if (chosen != null) hotkeys.Configure(chosen); else hotkeys.Resume(); }
                catch (Exception ex) { status.Text = ex.Message; hotkeys.Resume(); }
            }
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { dialogOpen = false; }
    }

    private void ApplyTheme() => RequestedThemeVariant = UiThemes.Resolve(store.Pref).Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    private void MarkDirty() { dirty = true; revision++; }
    private void Save() => _ = SaveAsync();
    private Task<bool> SaveAsync()
    {
        if (busy) return Task.FromResult(false);
        if (pendingSave is { IsCompleted: false }) return pendingSave;
        return pendingSave = Persist(revision);
    }
    private async Task<bool> Persist(int savedRevision)
    {
        try { await store.SaveAsync(); dirty = revision != savedRevision; return true; }
        catch (Exception ex) { status.Text = "保存失败，当前进度仍在内存中：" + ex.Message; dirty = true; return false; }
    }

    // Mirrors the guard at the top of ExitAsync so the control channel can refuse a stop request
    // with "busy" instead of promising an exit that will not happen.
    public bool CanExit => !busy && !dialogOpen && reader?.IsPrompting != true;
    public void RequestExit() => _ = ExitAsync();
    private async Task CloseModule()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) { status.Text = "请先完成或取消当前操作。"; return; }
        reader?.Pause(); reader?.Hide();
        busy = true;
        bool saved;
        try { if (pendingSave != null) await pendingSave; saved = await Persist(revision); }
        finally { busy = false; }
        if (!saved) return;
        reader?.DisposeReader(); reader = null; session = null; description.Text = "阅读已关闭，再次打开时恢复进度。"; Hide();
    }
    private async Task ExitAsync()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) { status.Text = "请先完成或取消当前操作。"; return; }
        busy = true; actions.IsEnabled = books.IsEnabled = false; reader?.Pause(); reader?.Hide();
        bool saved;
        try
        {
            if (pendingSave != null) await pendingSave;
            do { saved = await Persist(revision); } while (saved && dirty);
        }
        finally { busy = false; actions.IsEnabled = books.IsEnabled = true; }
        if (!saved) { RestoreLibrary(); return; }
        exiting = true;
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public void DisposeServices() { saveTimer.Stop(); hotkeys?.Dispose(); downloads.Dispose(); reader?.DisposeReader(); }

    private async Task SmokeTest()
    {
        try
        {
            // Real window creation/layout path; test data lives in the caller's isolated --data directory.
            if (SmokeHome != null) await DesktopSmokeTests.Run(SmokeHome, this, store);
            var book = store.Add("界面冒烟测试", "第一章\n中文 English 😊 阅读测试。\n第二段。", "TXT");
            busy = true; RefreshBooks(book.Id); busy = false;
            await SelectBook(book);
            await RunBookLibrarySmoke();
            busy = true; RefreshBooks(book.Id, true); busy = false;
            await SelectBook(book);
            if (progressLabel.Text != $"阅读 {session!.Percent}%") throw new InvalidOperationException("进度条文案：" + progressLabel.Text);
            await Task.Delay(150);
            Capture(this, "library.png");
            ShowReader();
            if (reader?.IsVisible != true) throw new InvalidOperationException(status.Text);
            reader.Move(true); reader.Move(false); reader.ApplyPreferences();
            reader.SetClickThrough(true); reader.SetClickThrough(false);
            await Task.Delay(150);
            Capture(reader, "reader.png");
            RestoreLibrary();
            if (!await SaveAsync()) throw new IOException("Smoke test could not persist state. " + status.Text);
            File.WriteAllText(Path.Combine(store.Root, "smoke-test.ok"), "Window creation, layout, navigation and persistence passed.");
            RequestExit();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(store.Root, "smoke-test.error"), ex.ToString());
            exiting = true; (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(1);
        }
    }

    private void Capture(Window window, string filename)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(Path.Combine(store.Root, filename));
    }
}
