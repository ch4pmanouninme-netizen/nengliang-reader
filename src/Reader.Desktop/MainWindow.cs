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

/// <summary>
/// 书库主窗口：管理书目、导入/下载、阅读会话、设置与全局快捷键，并负责阅读进度持久化。
/// </summary>
/// <remarks>
/// 这个类按 partial 拆分：书库视图在 BookLibraryView.cs，书库冒烟测试在 BookLibrarySmokeTests.cs。
/// 窗口与界面状态由 UI 线程访问；保存定时器和异步保存也由 UI 线程的调度上下文驱动。
/// 控制通道只读取 <see cref="CanExit"/> 做快速判断，真正的退出仍会调度回 UI 线程执行。
/// </remarks>
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
    // 2 秒防抖：阅读位置、窗口位置等变化频繁，攒一批再落盘。
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DownloadService downloads = new();
    private Hotkeys? hotkeys;
    private ReaderWindow? reader;
    private ReadingSession? session;
    private CancellationTokenSource? downloadCancellation;
    // busy/dialogOpen 阻止重入；dirty/revision 驱动防抖保存；exiting 放行真正的关闭。
    private bool busy, dirty, exiting, dialogOpen, initialized, integrationStarted;
    // 每次标脏递增；保存完成时用它判断保存期间是否又有新改动。
    private int revision;
    // 同一时间最多一个保存任务，后续调用复用未完成的任务。
    private Task<bool>? pendingSave;
    /// <summary>显示主页的回调，由 <see cref="HomeWindow"/> 注入。</summary>
    public Action? ShowHome { get; set; }
    /// <summary>全局隐藏/恢复所有模块窗口的回调，由主页注入。</summary>
    public Action? HideAll { get; set; }
    /// <summary>主题变化后通知主页刷新的回调。</summary>
    public Action? ThemeChanged { get; set; }
    /// <summary>冒烟测试专用：保存主页实例，正常启动时为 null。</summary>
    internal HomeWindow? SmokeHome { get; set; }
    /// <summary>当前可见的阅读条；没有打开或已隐藏时为 null。</summary>
    public Window? VisibleReader => reader?.IsVisible == true ? reader : null;
    // 阅读条优先使用随书保存的章节索引；TXT 在导入时也会生成 sidecar。
    /// <summary>获取某本书的章节索引；没有可用目录时返回 null。</summary>
    /// <param name="book">要查询的书目。</param>
    /// <returns>随书保存的章节目录；不存在、为空或损坏时为 null，阅读端会回退到标题识别。</returns>
    internal IReadOnlyList<Chapter> ChaptersFor(Book book) => store.ChaptersFor(book);
    /// <summary>暂停阅读条的自动阅读。</summary>
    public void PauseReader() => reader?.Pause();
    /// <summary>标记偏好已变更并立即请求一次异步保存。</summary>
    public void SavePreferences() { MarkDirty(); Save(); }
    /// <summary>仅刷新书库窗口自身的主题变体；应用级主题由 <see cref="UiThemes.Apply"/> 处理。</summary>
    public void RefreshTheme() => ApplyTheme();
    /// <summary>创建书库主窗口并构建全部界面。</summary>
    /// <param name="store">书库存储；窗口直接读写它。</param>
    /// <remarks>窗口构造只搭界面，首次 Opened 时才加载书目并处理恢复提示。</remarks>
    public MainWindow(Store store)
    {
        this.store = store;
        Title = "良子阅读器"; Icon = AppIcon.Shared;
        Width = 780; Height = 500; MinWidth = 600; MinHeight = 380;
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
        // 进度条同时服务阅读、导入和下载，结束后由 ShowReadingProgress 恢复阅读文案。
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
        // 忙碌时拒绝拖放；是否支持的文件格式交给导入流程判断。
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = !busy && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths != null) await ImportFiles(paths);
        });
        // 定时器只在有改动且没有进行中的操作时落盘，避免与导入/删除竞争。
        saveTimer.Tick += (_, _) => { if (dirty && !busy && !dialogOpen) Save(); };
        saveTimer.Start();
        // 关闭书库窗口默认只收进托盘；真正退出由 ExitAsync 设置 exiting 后放行。
        Closing += (_, e) =>
        {
            if (!exiting) { e.Cancel = true; _ = CloseModule(); }
        };
        Opened += async (_, _) =>
        {
            if (initialized) return;
            initialized = true;
            ApplyTheme();
            // 冒烟测试自己准备数据并控制刷新顺序，正常启动才按上次书目恢复。
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

    /// <summary>启动平台集成（全局快捷键）；冒烟测试下跳过，由测试自行管理。</summary>
    /// <remarks>注册失败只记录到状态栏，不阻止应用继续运行。</remarks>
    public void StartIntegration()
    {
        if (Program.SmokeTest || integrationStarted) return;
        integrationStarted = true;
        try { hotkeys = new Hotkeys(store.Root, HandleHotkey); }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    // 全局热键回调已由 Hotkeys 调度到 UI 线程。
    private void HandleHotkey(int id)
    {
        // 忙碌、对话框或阅读条提示框期间不响应，避免误隐藏正文或打断当前操作。
        if (busy || dialogOpen || reader?.IsPrompting == true) return;
        // id 与 Hotkeys.Functions 顺序一致：1 隐藏/恢复全部窗口，2/3 翻页，4 穿透，5 自动阅读。
        switch (id)
        {
            case 1: if (HideAll != null) HideAll(); else ToggleReader(); break;
            case 2: if (reader?.IsVisible == true) reader.Move(true); break;
            case 3: if (reader?.IsVisible == true) reader.Move(false); break;
            case 4: ToggleClickThrough(); break;
            case 5: if (reader?.IsVisible == true) reader.ToggleAuto(); break;
        }
    }

    // 切换当前阅读书：释放旧阅读条，后台读取正文，再建立新的阅读会话。
    private async Task SelectBook(Book book)
    {
        await RunBusy(async () =>
        {
            // 旧阅读条必须先释放，避免两个会话同时持有同一本书的缓存。
            reader?.DisposeReader(); reader = null; session = null;
            string text = await Task.Run(() => store.TextFor(book));
            session = new ReadingSession(book, text); store.LastId = book.Id; Changed();
        });
    }

    /// <summary>阅读会话推进后刷新摘录、进度条并标记待保存。</summary>
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

    // 打开系统文件选择器；选择期间阻止其它书库操作。
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

    // 拖放与文件选择器共用的导入入口；空列表或忙碌时直接返回。
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
                // 导入与阅读/下载共用同一根进度条：Importer 每解析完一章回报一次，
                // 结束后在 finally 里交还给阅读进度，避免把「导入 x/y」留在界面上。
                var importProgress = new Progress<ImportProgress>(p =>
                {
                    if (p.Total <= 0) { progress.IsIndeterminate = true; progressLabel.Text = "导入中 …"; return; }
                    progress.IsIndeterminate = false;
                    progress.Value = 100.0 * p.Completed / p.Total;
                    progressLabel.Text = $"导入 {p.Completed}/{p.Total}";
                });
                try
                {
                    var imported = await Task.Run(() => Importer.Read(path, importProgress));
                    var book = await Task.Run(() => store.Add(imported.Title, imported.Text, imported.Format, imported.Chapters));
                    selected = book.Id; MarkDirty();
                }
                catch (Exception ex) { failures.Add(Path.GetFileName(path) + "：" + ex.Message); }
                finally { progress.IsIndeterminate = false; ShowReadingProgress(); }
            }
            status.Text = failures.Count == 0 ? "导入完成，阅读进度保存在本机。" : string.Join("\n", failures);
        });
        // 导入成功后立即刷新到新书；保存异步执行，不阻塞界面。
        if (selected != null) RefreshBooks(selected, true);
        Save();
    }

    // 询问下载地址并在后台下载/解析；取消按钮通过 downloadCancellation 生效。
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
            // 下载令牌交给取消按钮；结束后在 finally 中清空，防止残留到下一次下载。
            using var cancellation = new CancellationTokenSource(); downloadCancellation = cancellation; cancel.IsVisible = true;
            var updates = new Progress<DownloadProgress>(p =>
            {
                status.Text = $"已下载 {p.Bytes / 1024.0:N0} KB" + (p.Total.HasValue ? $" / {p.Total / 1024.0:N0} KB" : "");
                progress.IsIndeterminate = !p.Total.HasValue;
                progressLabel.Text = p.Total > 0 ? $"下载 {100.0 * p.Bytes / p.Total.Value:0}%" : "下载中 …";
                if (p.Total > 0) progress.Value = 100.0 * p.Bytes / p.Total.Value;
            });
            // 下载完成后如果解析的是 EPUB，进度条改由章节进度驱动。
            var importUpdates = new Progress<ImportProgress>(p =>
            {
                status.Text = "正在解析下载的书籍…";
                if (p.Total <= 0) { progress.IsIndeterminate = true; progressLabel.Text = "解析中 …"; return; }
                progress.IsIndeterminate = false;
                progress.Value = 100.0 * p.Completed / p.Total;
                progressLabel.Text = $"解析 {p.Completed}/{p.Total}";
            });
            try
            {
                var imported = await downloads.DownloadAsync(address, updates, cancellation.Token, importUpdates);
                var book = await Task.Run(() => store.Add(imported.Title, imported.Text, imported.Format, imported.Chapters));
                selected = book.Id; MarkDirty(); status.Text = "下载完成，已加入书架。";
            }
            catch (OperationCanceledException) { status.Text = "下载已取消。"; }
            // 下载结束后把进度条和文案交还阅读进度，取消下载时不会留在“下载 xx%”。
            finally { downloadCancellation = null; cancel.IsVisible = false; progress.IsIndeterminate = false; ShowReadingProgress(); }
        });
        if (selected != null) RefreshBooks(selected, true);
        Save();
    }

    // 统一的忙碌包装：禁用操作控件、暂停并隐藏阅读条、等待已挂起的保存。
    private async Task RunBusy(Func<Task> operation)
    {
        if (busy) return;
        busy = true; actions.IsEnabled = books.IsEnabled = groupFilter.IsEnabled = false; reader?.Pause(); reader?.Hide();
        try { if (pendingSave != null) await pendingSave; await operation(); }
        // 操作异常只显示在状态栏，窗口保持可用。
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; actions.IsEnabled = books.IsEnabled = groupFilter.IsEnabled = true; }
    }

    // 导出或导入整库备份；文件选择器与后台 IO 都包在 dialogOpen/RunBusy 中。
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
        // 用户取消文件选择时不执行任何 IO。
        if (path == null) return;
        await RunBusy(async () =>
        {
            if (import) { int count = await Task.Run(() => LibraryArchive.Import(store, path)); status.Text = $"已导入 {count} 本书；重复书籍保留现有进度。"; }
            else { await Task.Run(() => LibraryArchive.Export(store, path)); status.Text = "书库已导出。"; }
        });
        if (import) RefreshBooks(store.LastId);
    }

    /// <summary>显示或创建阅读条，并隐藏书库窗口。</summary>
    /// <remarks>没有阅读会话、忙碌或对话框打开时不执行。</remarks>
    public void ShowReader()
    {
        if (session == null || busy || dialogOpen) return;
        try
        {
            // 阅读条按需创建，之后复用同一实例以保留窗口位置和设置。
            reader ??= new ReaderWindow(store.Pref, session, this);
            reader.Show(); reader.EnsureVisible(); reader.ApplyPreferences(); Hide();
        }
        catch (Exception ex) { status.Text = "无法打开阅读条：" + ex.Message; }
    }

    /// <summary>切换阅读条显示/隐藏；隐藏时暂停自动阅读并保存进度。</summary>
    public void ToggleReader()
    {
        // 阅读条正在显示提示框时不切换，避免抢走模态焦点。
        if (busy || dialogOpen || reader?.IsPrompting == true) return;
        if (reader?.IsVisible == true) { reader.Pause(); reader.Hide(); Save(); }
        else if (session != null) ShowReader();
        // 还没有阅读会话时，这个动作退回书库窗口。
        else RestoreLibrary();
    }

    /// <summary>恢复并激活书库窗口，同时退出鼠标穿透和隐藏阅读条。</summary>
    public void RestoreLibrary()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) return;
        if (reader != null)
        {
            reader.Pause();
            // 穿透是原生窗口样式，恢复失败也要继续显示书库并提示原因。
            try { reader.SetClickThrough(false); } catch (Exception ex) { status.Text = "穿透恢复失败：" + ex.Message; }
            reader.Hide();
        }
        store.Pref.ClickThrough = false;
        Show(); WindowState = WindowState.Normal; Activate();
        // 没有活动会话时，用当前选中书补建阅读会话。
        if (session == null && books.SelectedItem is Book selected && !busy) _ = SelectBook(selected);
    }

    /// <summary>切换阅读条鼠标穿透；失败时恢复书库窗口并提示。</summary>
    public void ToggleClickThrough()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true || reader?.IsVisible != true) return;
        try { reader.SetClickThrough(!store.Pref.ClickThrough); MarkDirty(); }
        // 平台调用失败时退出穿透并恢复书库，避免阅读条变成无法关闭的透明窗口。
        catch (Exception ex) { RestoreLibrary(); status.Text = "无法切换穿透：" + ex.Message; }
    }

    /// <summary>打开阅读设置；保存后应用偏好、刷新主题并落盘。</summary>
    /// <remarks>设置窗口是模态的，打开期间 <see cref="dialogOpen"/> 阻止其它入口。</remarks>
    public async Task Settings()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) return;
        // 先恢复书库并关闭阅读条，避免设置窗口被置顶/穿透的阅读条遮挡。
        RestoreLibrary(); dialogOpen = true;
        try { if (await new SettingsWindow(store.Pref).ShowDialog<bool>(this)) { reader?.ApplyPreferences(); ApplyTheme(); ThemeChanged?.Invoke(); MarkDirty(); Save(); } }
        finally { dialogOpen = false; }
    }

    // 打开快捷键设置；设置窗口期间先注销全局热键，关闭后按结果重新注册。
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

    // 书库窗口只跟随明暗变体；具体配色由 UiThemes.Apply 写入应用资源。
    private void ApplyTheme() => RequestedThemeVariant = UiThemes.Resolve(store.Pref).Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    // 所有需要落盘的改动都经过这里；revision 用于判断保存期间是否又有新改动。
    private void MarkDirty() { dirty = true; revision++; }
    // 窗口位置等纯偏好变化不需要重算阅读描述，只标记待保存。
    internal void MarkDirtyOnly() => MarkDirty();
    // 触发异步保存，调用方不等待；失败会保持 dirty 供定时器重试。
    private void Save() => _ = SaveAsync();
    // 忙碌时推迟保存；已有未完成任务时复用，避免并发写盘。
    private Task<bool> SaveAsync()
    {
        if (busy) return Task.FromResult(false);
        if (pendingSave is { IsCompleted: false }) return pendingSave;
        return pendingSave = Persist(revision);
    }
    private async Task<bool> Persist(int savedRevision)
    {
        // 保存成功后比较 revision：期间有新改动则保持 dirty，让定时器再存一次。
        try { await store.SaveAsync(); dirty = revision != savedRevision; return true; }
        // 写盘失败不能假装保存成功；保留 dirty 并提示进度仍在内存中。
        catch (Exception ex) { status.Text = "保存失败，当前进度仍在内存中：" + ex.Message; dirty = true; return false; }
    }

    // Mirrors the guard at the top of ExitAsync so the control channel can refuse a stop request
    // with "busy" instead of promising an exit that will not happen.
    /// <summary>当前是否允许退出：没有忙碌操作、对话框或阅读条提示。</summary>
    public bool CanExit => !busy && !dialogOpen && reader?.IsPrompting != true;
    /// <summary>请求退出应用；实际保存与关闭异步执行。</summary>
    public void RequestExit() => _ = ExitAsync();
    // 关闭书库模块：保存后只隐藏窗口，不结束进程。
    private async Task CloseModule()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) { status.Text = "请先完成或取消当前操作。"; return; }
        reader?.Pause(); reader?.Hide();
        busy = true;
        bool saved;
        // 等待挂起保存，再同步持久化当前 revision，确保关闭前不丢进度。
        try { if (pendingSave != null) await pendingSave; saved = await Persist(revision); }
        finally { busy = false; }
        // 保存失败时保持窗口打开，避免把未落盘的进度藏起来。
        if (!saved) return;
        reader?.DisposeReader(); reader = null; session = null; description.Text = "阅读已关闭，再次打开时恢复进度。"; Hide();
    }
    // 真正退出：等待保存完成，并循环持久化直到没有新的改动。
    private async Task ExitAsync()
    {
        if (busy || dialogOpen || reader?.IsPrompting == true) { status.Text = "请先完成或取消当前操作。"; return; }
        busy = true; actions.IsEnabled = books.IsEnabled = false; reader?.Pause(); reader?.Hide();
        bool saved;
        try
        {
            if (pendingSave != null) await pendingSave;
            // 保存过程中可能又有进度更新，循环直到一次保存期间没有新 revision。
            do { saved = await Persist(revision); } while (saved && dirty);
        }
        finally { busy = false; actions.IsEnabled = books.IsEnabled = true; }
        // 保存失败则恢复书库窗口，不退出进程。
        if (!saved) { RestoreLibrary(); return; }
        // 设置 exiting 后 Closing 不再拦截，Shutdown 才会真正结束进程。
        exiting = true;
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>应用退出前释放书库模块持有的定时器、热键、下载服务和阅读条。</summary>
    public void DisposeServices() { saveTimer.Stop(); hotkeys?.Dispose(); downloads.Dispose(); reader?.DisposeReader(); }

    // 仅在 --smoke-test 下由 Opened 调用；失败会写错误文件并以非零码退出。
    private async Task SmokeTest()
    {
        try
        {
            // Real window creation/layout path; test data lives in the caller's isolated --data directory.
            if (SmokeHome != null) await DesktopSmokeTests.Run(SmokeHome, this, store);
            // 在隔离数据目录里建一本测试书，后续界面检查都围绕它进行。
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
            // 成功标记文件供外部脚本判定；随后走正常退出流程释放资源。
            File.WriteAllText(Path.Combine(store.Root, "smoke-test.ok"), "Window creation, layout, navigation and persistence passed.");
            RequestExit();
        }
        catch (Exception ex)
        {
            // 失败时记录完整异常，并直接以非零码退出，避免留下半初始化的窗口。
            File.WriteAllText(Path.Combine(store.Root, "smoke-test.error"), ex.ToString());
            exiting = true; (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(1);
        }
    }

    // 把窗口当前像素保存到数据目录，供人工核对布局。
    private void Capture(Window window, string filename)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(Path.Combine(store.Root, filename));
    }
}
