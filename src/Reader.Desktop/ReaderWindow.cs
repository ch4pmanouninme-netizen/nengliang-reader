using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Threading;
using Reader.Core;
using Reader.Platform.Windows;
using Reader.Platform.Mac;

namespace Reader.Desktop;

/// <summary>
/// 悬浮阅读条：分页显示正文，处理翻页、自动阅读、书签、查找/章节跳转、置顶/穿透和位置持久化。
/// </summary>
/// <remarks>
/// 窗口无边框且不进入任务栏，关闭会被拦截为隐藏。定时器、分页布局缓存和原生穿透样式都归本窗口所有，
/// 由 <see cref="DisposeReader"/> 统一释放。
/// </remarks>
internal sealed class ReaderWindow : Window
{
    private readonly Preferences preferences;
    private readonly ReadingSession session;
    private readonly MainWindow library;
    private readonly ReadingSurface surface;
    // 自动阅读定时器；间隔来自偏好。
    private readonly DispatcherTimer auto = new();
    // 定期把窗口拉回可见屏幕，防止显示器布局变化后窗口丢失在屏幕外。
    private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    // 拖动宽度时防抖重新分页，避免每一帧都重排。
    private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly TextBlock footer = new() { FontSize = 10, Margin = new Thickness(8, 0, 8, 2), Foreground = Brushes.Gray };
    private ReadingPage? page;
    // 章节索引缓存；null 表示尚未加载。
    private IReadOnlyList<Chapter>? chapters;
    // 后台加载任务；失败后清空以便下次重试。
    private Task<IReadOnlyList<Chapter>>? chaptersTask;
    // 防止重复打开章节选择时并发加载目录。
    private bool loadingChapters;
    // applying 抑制程序性位置/尺寸变化触发的持久化。
    private bool closing, applying;
    // resizing 表示正在拖拽宽度；prompting 表示模态提示打开，主窗口据此暂停热键。
    private bool resizing, prompting;
    private double initialWidth;
    private PixelPoint resizeStart;
    /// <summary>是否有阅读条自己的模态提示正在显示。</summary>
    public bool IsPrompting => prompting;

    /// <summary>创建悬浮阅读条。</summary>
    /// <param name="preferences">阅读外观与行为偏好；窗口会就地更新宽高和位置。</param>
    /// <param name="session">当前阅读会话，提供正文和分页状态。</param>
    /// <param name="library">书库主窗口，用于回调保存、恢复书架和打开设置。</param>
    public ReaderWindow(Preferences preferences, ReadingSession session, MainWindow library)
    {
        this.preferences = preferences; this.session = session; this.library = library;
        // 无系统装饰、不占任务栏、显示时不抢焦点：阅读条是悬浮工具窗口。
        Title = "良子阅读器 · 悬浮阅读"; SystemDecorations = SystemDecorations.None; ShowInTaskbar = false;
        ShowActivated = false; CanResize = true; MinWidth = 240; MaxWidth = 1800; Width = preferences.Width;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        surface = new ReadingSurface();
        var panel = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer); panel.Children.Add(surface); Content = panel;
        ContextMenu = CreateMenu();
        ContextMenu.Opened += (_, _) => Pause();
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // 双击返回书架；靠近右边缘拖动改宽，其余区域拖动窗口。
                if (e.ClickCount == 2) library.RestoreLibrary();
                else if (e.GetPosition(this).X >= Bounds.Width - 10)
                {
                    resizing = true; initialWidth = Width; resizeStart = this.PointToScreen(e.GetPosition(this)); e.Pointer.Capture(this);
                }
                else BeginMoveDrag(e);
            }
        };
        PointerMoved += (_, e) =>
        {
            // 宽度按物理像素增量换算为逻辑宽度，并限制在窗口允许范围内。
            if (resizing) Width = Math.Clamp(initialWidth + (this.PointToScreen(e.GetPosition(this)).X - resizeStart.X) / DesktopScaling, MinWidth, MaxWidth);
        };
        PointerReleased += (_, e) => { if (resizing) { resizing = false; e.Pointer.Capture(null); } };
        PointerCaptureLost += (_, _) => resizing = false;
        // 滚轮按方向翻页，而不是滚动像素；标记已处理避免父级再滚动。
        PointerWheelChanged += (_, e) => { Move(e.Delta.Y < 0); e.Handled = true; };
        // Ctrl/Cmd+B/J/F/G 分别对应书签、回到书签、查找、选择章节；其余按键负责翻页和自动阅读。
        KeyDown += async (_, e) =>
        {
            bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (command && e.Key == Key.B) Bookmark();
            else if (command && e.Key == Key.J) GoBookmark();
            else if (command && e.Key == Key.F) await Find();
            else if (command && e.Key == Key.G) await ChooseChapter();
            else if (e.Key is Key.Right or Key.Down or Key.PageDown) Move(true);
            else if (e.Key is Key.Left or Key.Up or Key.PageUp) Move(false);
            else if (e.Key == Key.Space) ToggleAuto();
            else if (e.Key == Key.Escape) library.ToggleReader();
            else return;
            e.Handled = true;
        };
        SizeChanged += (_, _) =>
        {
            if (applying) return;
            preferences.Width = (int)Width;
            // 拖拽右边缘会连续触发 SizeChanged；停手后再重新分页，避免每帧重排。
            resizeTimer.Stop(); resizeTimer.Start();
        };
        resizeTimer.Tick += (_, _) =>
        {
            resizeTimer.Stop();
            if (closing) return;
            session.ClearHistory(); RefreshPage(); library.Changed();
        };
        PositionChanged += (_, _) =>
        {
            if (applying) return;
            preferences.X = Position.X; preferences.Y = Position.Y; preferences.HasPosition = true;
            // 窗口位置不影响阅读进度文案，只标脏，交给现有 2 秒保存定时器落盘。
            library.MarkDirtyOnly();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            // 只在可见时检查窗口是否还在屏幕内，隐藏期间不做无谓计算。
            if (IsVisible) { positionTimer.Start(); EnsureVisible(); }
            else positionTimer.Stop();
        };
        // 关闭按钮不销毁窗口，只交给书库切换显示；DisposeReader 才真正关闭。
        Closing += (_, e) => { if (!closing) { e.Cancel = true; library.ToggleReader(); } };
        // 自动阅读在窗口隐藏或右键菜单打开时暂停，避免后台翻页。
        auto.Tick += (_, _) => { if (IsVisible && ContextMenu?.IsOpen != true) Move(true); else Pause(); };
        positionTimer.Tick += (_, _) => { if (IsVisible) EnsureVisible(); };
        applying = true;
        // 恢复上次位置；首次打开则放在主屏右下角附近。
        if (preferences.HasPosition) Position = new PixelPoint(preferences.X, preferences.Y);
        else if (Screens.Primary is { } screen) Position = new PixelPoint(screen.WorkingArea.X + 60, screen.WorkingArea.Bottom - 100);
        applying = false;
        ApplyPreferences();
    }

    // 右键菜单承载不常用操作；打开时会暂停自动阅读。
    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();
        // 每个菜单项都转发到已有命令，菜单本身不保存状态。
        void Add(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("下一页", () => Move(true)); Add("上一页", () => Move(false)); Add("自动阅读 / 暂停", ToggleAuto);
        Add("保存书签", Bookmark); Add("回到书签", GoBookmark); Add("查找文字", () => _ = Find()); Add("选择章节", () => _ = ChooseChapter());
        Add("阅读设置", () => _ = library.Settings()); Add("切换鼠标穿透", library.ToggleClickThrough);
        Add("返回书架", library.RestoreLibrary); Add("主页", () => library.ShowHome?.Invoke()); Add("隐藏", library.ToggleReader);
        return menu;
    }

    /// <summary>把当前偏好应用到阅读条：主题、字体、行列数、透明度和穿透状态。</summary>
    /// <remarks>在主线程调用；会清空分页历史并重新排版，因此阅读位置会按当前段落重新对齐。</remarks>
    public void ApplyPreferences()
    {
        // applying 防止设置过程中尺寸/位置变化被当作用户操作写回偏好。
        applying = true;
        try
        {
            Pause(); session.ClearHistory(); Topmost = preferences.TopMost; Opacity = preferences.Opacity / 100.0;
            // 旧版阅读主题使用固定索引；官方主题优先按稳定 Id 匹配。
            Color[] colors = [Color.Parse("#f8f9f6"), Color.Parse("#303238"), Color.Parse("#f7efda"), Color.Parse("#e1e5e6"), Color.Parse("#f5f7fa"), Color.Parse("#2b2b2b"), Colors.Transparent, Color.Parse("#252526"), Color.Parse("#282c34"), Color.Parse("#253247"), Color.Parse("#121314"), Color.Parse("#1E1F22")];
            var official = UiThemes.Official.FirstOrDefault(x => x.Id == preferences.ReadingThemeId);
            Background = new SolidColorBrush(official == null ? colors[Math.Clamp(preferences.Theme, 0, colors.Length - 1)] : Color.Parse(official.Background));
            surface.Typeface = new Typeface(preferences.FontName);
            // 偏好中的字号以 pt 保存，Avalonia 使用 DIP，按 96/72 换算。
            surface.FontSize = preferences.FontSize * 96 / 72;
            bool dark = official?.Dark ?? (preferences.Theme is 1 or 5 or >= 7);
            // 深色标记同时决定正文和页脚颜色；官方主题可以直接覆盖正文色。
            surface.Ink = new SolidColorBrush(Color.Parse(official?.EditorForeground ?? (preferences.Theme switch { 8 => "#ABB2BF", 10 => "#BBBEBF", 11 => "#BCBEC4", _ => dark ? "#D4D4D4" : "#696969" })));
            footer.Foreground = new SolidColorBrush(Color.Parse(official?.EditorForeground ?? (dark ? "#AAB2C0" : "#696969")));
            footer.IsVisible = !preferences.Stealth;
            // 高度由行高、行数和可选页脚推导；极简模式不占页脚高度。
            Height = surface.LineHeight * preferences.LineCount + 12 + (preferences.Stealth ? 0 : 18);
            auto.Interval = TimeSpan.FromSeconds(preferences.Seconds);
            RefreshPage();
            // 原生句柄尚未创建时跳过穿透；Show 之后会再次调用 ApplyPreferences。
            if (TryGetPlatformHandle() != null) SetClickThrough(preferences.ClickThrough);
        }
        finally { applying = false; }
    }

    // 用真实排版测量一行是否能放进当前宽度（留出左右各 8 像素内边距）。
    private bool Fits(string value)
    {
        using var text = surface.CreateLayout(value);
        return text.WidthIncludingTrailingWhitespace <= Math.Max(1, Width - 16);
    }
    // 从会话重新分页，刷新页脚提示，并把整页文本写入无障碍名称。
    private void RefreshPage()
    {
        page = session.Layout(Fits, preferences.LineCount); surface.Lines = page.Lines; surface.InvalidateVisual();
        footer.Text = $"{session.Percent}% · {(auto.IsEnabled ? "自动阅读中" : "右键菜单 · 双击书架 · 右边缘调宽")} " ;
        Avalonia.Automation.AutomationProperties.SetName(surface, string.Join("\n", page.Lines));
    }
    /// <summary>向前或向后翻一页；到末尾时停止自动阅读。</summary>
    /// <param name="next">true 下一页，false 上一页。</param>
    public void Move(bool next)
    {
        if (page == null) return;
        // 下一页走到全书末尾时返回 false，此时自动阅读应停止；上一页走排版回退。
        if (next) { if (!session.Next(page.End)) Pause(); }
        else session.Previous(Fits, preferences.LineCount);
        RefreshPage(); library.Changed();
    }
    /// <summary>开始或暂停自动阅读；已读完的书不会启动。</summary>
    public void ToggleAuto() { if (auto.IsEnabled) Pause(); else if (!session.Book.Completed) auto.Start(); RefreshPage(); }
    /// <summary>暂停自动阅读。</summary>
    public void Pause() => auto.Stop();
    // 书签记录当前分页起点，保存后立即刷新页面提示。
    private void Bookmark() { session.Book.Bookmark = session.Position; library.Changed(); footer.Text = "书签已保存"; }
    // 没有书签（-1）时保持当前位置。
    private void GoBookmark() { if (session.Book.Bookmark >= 0) { session.Seek(session.Book.Bookmark); RefreshPage(); library.Changed(); } }

    // 阅读条自己的输入提示：暂停自动阅读，并临时取消置顶，避免对话框被阅读条盖住。
    private async Task<string?> Prompt(string title, string description, string initial = "")
    {
        Pause(); bool top = Topmost; Topmost = false; prompting = true;
        try { return await Dialogs.Prompt(this, title, description, initial); }
        finally { Topmost = top; prompting = false; }
    }
    // 从当前位置向后查找，找不到再从头找一次，避免用户重复按。
    private async Task Find()
    {
        string? text = await Prompt("查找", "输入正文中的文字，从当前位置向后查找。");
        if (string.IsNullOrWhiteSpace(text)) return;
        int index = session.Text.IndexOf(text, Math.Min(session.Position + 1, session.Text.Length), StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = session.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (index >= 0) { session.Seek(index); RefreshPage(); library.Changed(); }
        else await Message("查找", "没有找到这段文字。");
    }
    // 按百分比跳转；章节识别失败时作为兜底入口。
    private async Task Jump(string description = "输入 0～100 的阅读百分比。")
    {
        string? text = await Prompt("跳转", description, session.Percent.ToString());
        if (text == null) return;
        if (!double.TryParse(text.Trim().TrimEnd('%'), out double number) || !double.IsFinite(number) || number < 0 || number > 100)
        { await Message("跳转", "请输入 0～100 的有效数字。"); return; }
        session.Seek((int)(number / 100 * (session.Text.Length - 1))); RefreshPage(); library.Changed();
    }

    // 后台构建章节索引；结果会缓存，失败时由调用方清空任务以便重试。
    private Task<IReadOnlyList<Chapter>> LoadChaptersAsync()
    {
        // 读取 sidecar 和标题启发式都可能扫全文；放到后台线程，避免首次打开目录时卡住阅读条。
        return Task.Run(() =>
        {
            // EPUB 导入时保存的结构化目录优先；旧书没有 sidecar 或索引不完整时，再用标题启发式。
            var stored = library.ChaptersFor(session.Book)?
                .Where(chapter => chapter.Position >= 0 && chapter.Position < session.Text.Length).ToList();
            if (stored != null && stored.Count >= 2) return (IReadOnlyList<Chapter>)stored;
            return ChapterIndex.Find(session.Text);
        });
    }

    // 打开章节选择窗口：首次使用时在后台加载目录，之后复用缓存。
    private async Task ChooseChapter()
    {
        if (chapters == null)
        {
            // 加载期间重复触发（例如连按快捷键）直接忽略。
            if (loadingChapters) return;
            loadingChapters = true;
            Pause();
            footer.Text = "正在读取目录…";
            try { chapters = await (chaptersTask ??= LoadChaptersAsync()); }
            catch (Exception ex)
            {
                // 清空任务，下次打开时重新尝试；底部提示恢复到当前进度。
                chaptersTask = null;
                await Message("选择章节", "读取目录失败：" + ex.Message);
                RefreshPage();
                return;
            }
            finally { loadingChapters = false; }
            // 加载期间窗口可能已被关闭，避免继续操作已释放的界面。
            if (closing) return;
            RefreshPage();
        }
        // 识别不到章节标题时保留百分比跳转作为兜底，避免整本书完全失去跳转能力。
        if (chapters.Count < 2) { await Jump("未识别到章节标题，请输入 0～100 的阅读百分比。"); return; }
        Pause(); bool top = Topmost; Topmost = false; prompting = true;
        try
        {
            // 章节窗口是模态的；期间取消置顶，否则可能被阅读条本身遮挡。
            Chapter? chosen = await new ChapterDialog(chapters, session.Position).ShowDialog<Chapter?>(this);
            if (chosen == null) return;
            session.Seek(chosen.Position); RefreshPage(); library.Changed();
        }
        finally { Topmost = top; prompting = false; }
    }
    /// <summary>开启或关闭阅读条鼠标穿透。</summary>
    /// <param name="enabled">true 表示点击穿透到下层窗口。</param>
    /// <remarks>仅 Windows/macOS 实现；窗口句柄尚未创建时直接返回。</remarks>
    public void SetClickThrough(bool enabled)
    {
        var handle = TryGetPlatformHandle();
        if (handle == null) return;
        // Windows 额外需要当前透明度，避免穿透后残留可见的实心窗口。
        if (OperatingSystem.IsWindows()) WindowsIntegration.SetClickThrough(handle.Handle, enabled, (byte)Math.Round(255 * preferences.Opacity / 100.0));
        else if (OperatingSystem.IsMacOS()) MacIntegration.SetClickThrough(handle.Handle, enabled);
        preferences.ClickThrough = enabled;
    }
    // 阅读条内的消息框同样临时取消置顶并标记 prompting。
    private async Task Message(string title, string text)
    {
        prompting = true; bool top = Topmost; Topmost = false;
        try { await Dialogs.Message(this, title, text); }
        finally { prompting = false; Topmost = top; }
    }
    /// <summary>把窗口位置限制在当前屏幕的工作区内，防止换显示器后窗口不可见。</summary>
    public void EnsureVisible()
    {
        double scale = DesktopScaling;
        var bounds = new PixelRect(Position, new PixelSize((int)(Width * scale), (int)(Height * scale)));
        var screen = Screens.All.FirstOrDefault(screen => screen.WorkingArea.Intersects(bounds)) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        // 优先完整显示；窗口比屏幕还大时至少把左上角留在工作区内。
        var next = new PixelPoint(Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - bounds.Width)),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - bounds.Height)));
        if (next != Position) Position = next;
    }
    /// <summary>彻底关闭阅读条：停止定时器、释放排版缓存并关闭窗口。</summary>
    /// <remarks>与隐藏不同，关闭后需要重新创建窗口实例。</remarks>
    public void DisposeReader() { closing = true; auto.Stop(); resizeTimer.Stop(); positionTimer.Stop(); surface.ReleaseLayouts(); Close(); }
}

/// <summary>
/// 自绘正文面板：按行缓存 <see cref="TextLayout"/>，并用统一行高绘制分页结果。
/// </summary>
/// <remarks>TextLayout 持有原生资源；内容、字体、字号或颜色变化时清空缓存，关闭阅读条时调用 ReleaseLayouts。</remarks>
internal sealed class ReadingSurface : Control
{
    private Typeface typeface = new("sans-serif");
    private double fontSize = 16;
    private IBrush ink = Brushes.DimGray;
    private IReadOnlyList<string> lines = [];
    private TextLayout[]? layouts;

    /// <summary>正文使用的字体。</summary>
    public Typeface Typeface { get => typeface; set { typeface = value; ClearLayouts(); } }
    /// <summary>正文使用的字号（DIP）。</summary>
    public double FontSize { get => fontSize; set { fontSize = value; ClearLayouts(); } }
    /// <summary>正文画刷。</summary>
    public IBrush Ink { get => ink; set { ink = value; ClearLayouts(); } }
    /// <summary>当前页的文本行；赋值时清空排版缓存。</summary>
    public IReadOnlyList<string> Lines { get => lines; set { lines = value; ClearLayouts(); } }
    /// <summary>固定的行高（字号的 1.55 倍，向上取整）。</summary>
    public double LineHeight => Math.Ceiling(FontSize * 1.55);
    /// <summary>按当前字体/字号/颜色创建一段文本排版；调用方负责释放。</summary>
    /// <param name="text">要测量的文本。</param>
    /// <returns>新创建的 TextLayout。</returns>
    public TextLayout CreateLayout(string text) => new(text, Typeface, FontSize, Ink);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // 重绘只复用缓存；内容/字体/字号/颜色变化时由属性 setter 清空。
        if (layouts == null || layouts.Length != Lines.Count)
        {
            ClearLayouts();
            layouts = new TextLayout[Lines.Count];
            for (int row = 0; row < Lines.Count; row++) layouts[row] = CreateLayout(Lines[row]);
        }
        for (int row = 0; row < layouts.Length; row++)
            layouts[row].Draw(context, new Point(8, 6 + row * LineHeight));
    }

    /// <summary>释放当前缓存的排版对象；窗口关闭前调用。</summary>
    public void ReleaseLayouts() => ClearLayouts();

    // TextLayout 是原生资源，重建前必须逐个释放，否则分页频繁时会泄漏。
    private void ClearLayouts()
    {
        if (layouts != null) foreach (TextLayout layout in layouts) layout.Dispose();
        layouts = null;
    }
}
