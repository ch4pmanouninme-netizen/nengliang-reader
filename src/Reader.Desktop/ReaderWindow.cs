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

internal sealed class ReaderWindow : Window
{
    private readonly Preferences preferences;
    private readonly ReadingSession session;
    private readonly MainWindow library;
    private readonly ReadingSurface surface;
    private readonly DispatcherTimer auto = new();
    private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBlock footer = new() { FontSize = 10, Margin = new Thickness(8, 0, 8, 2), Foreground = Brushes.Gray };
    private ReadingPage? page;
    private bool closing, applying;
    private bool resizing, prompting;
    private double initialWidth;
    private PixelPoint resizeStart;
    public bool IsPrompting => prompting;

    public ReaderWindow(Preferences preferences, ReadingSession session, MainWindow library)
    {
        this.preferences = preferences; this.session = session; this.library = library;
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
            if (resizing) Width = Math.Clamp(initialWidth + (this.PointToScreen(e.GetPosition(this)).X - resizeStart.X) / DesktopScaling, MinWidth, MaxWidth);
        };
        PointerReleased += (_, e) => { if (resizing) { resizing = false; e.Pointer.Capture(null); } };
        PointerCaptureLost += (_, _) => resizing = false;
        PointerWheelChanged += (_, e) => { Move(e.Delta.Y < 0); e.Handled = true; };
        KeyDown += async (_, e) =>
        {
            bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (command && e.Key == Key.B) Bookmark();
            else if (command && e.Key == Key.J) GoBookmark();
            else if (command && e.Key == Key.F) await Find();
            else if (command && e.Key == Key.G) await Jump();
            else if (e.Key is Key.Right or Key.Down or Key.PageDown) Move(true);
            else if (e.Key is Key.Left or Key.Up or Key.PageUp) Move(false);
            else if (e.Key == Key.Space) ToggleAuto();
            else if (e.Key == Key.Escape) library.ToggleReader();
            else return;
            e.Handled = true;
        };
        SizeChanged += (_, _) => { if (!applying) { preferences.Width = (int)Width; session.ClearHistory(); RefreshPage(); library.Changed(); } };
        PositionChanged += (_, _) => { if (!applying) { preferences.X = Position.X; preferences.Y = Position.Y; preferences.HasPosition = true; library.Changed(); } };
        Closing += (_, e) => { if (!closing) { e.Cancel = true; library.ToggleReader(); } };
        auto.Tick += (_, _) => { if (IsVisible && ContextMenu?.IsOpen != true) Move(true); else Pause(); };
        positionTimer.Tick += (_, _) => { if (IsVisible) EnsureVisible(); };
        positionTimer.Start();
        applying = true;
        if (preferences.HasPosition) Position = new PixelPoint(preferences.X, preferences.Y);
        else if (Screens.Primary is { } screen) Position = new PixelPoint(screen.WorkingArea.X + 60, screen.WorkingArea.Bottom - 100);
        applying = false;
        ApplyPreferences();
    }

    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();
        void Add(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("下一页", () => Move(true)); Add("上一页", () => Move(false)); Add("自动阅读 / 暂停", ToggleAuto);
        Add("保存书签", Bookmark); Add("回到书签", GoBookmark); Add("查找文字", () => _ = Find()); Add("跳转进度", () => _ = Jump());
        Add("阅读设置", () => _ = library.Settings()); Add("切换鼠标穿透", library.ToggleClickThrough);
        Add("返回书架", library.RestoreLibrary); Add("主页", () => library.ShowHome?.Invoke()); Add("隐藏", library.ToggleReader);
        return menu;
    }

    public void ApplyPreferences()
    {
        applying = true;
        try
        {
            Pause(); session.ClearHistory(); Topmost = preferences.TopMost; Opacity = preferences.Opacity / 100.0;
            Color[] colors = [Color.Parse("#f8f9f6"), Color.Parse("#303238"), Color.Parse("#f7efda"), Color.Parse("#e1e5e6"), Color.Parse("#f5f7fa"), Color.Parse("#2b2b2b"), Colors.Transparent, Color.Parse("#252526"), Color.Parse("#282c34"), Color.Parse("#253247"), Color.Parse("#121314"), Color.Parse("#1E1F22")];
            var official = UiThemes.Official.FirstOrDefault(x => x.Id == preferences.ReadingThemeId);
            Background = new SolidColorBrush(official == null ? colors[Math.Clamp(preferences.Theme, 0, colors.Length - 1)] : Color.Parse(official.Background));
            surface.Typeface = new Typeface(preferences.FontName);
            surface.FontSize = preferences.FontSize * 96 / 72;
            bool dark = official?.Dark ?? (preferences.Theme is 1 or 5 or >= 7);
            surface.Ink = new SolidColorBrush(Color.Parse(official?.EditorForeground ?? (preferences.Theme switch { 8 => "#ABB2BF", 10 => "#BBBEBF", 11 => "#BCBEC4", _ => dark ? "#D4D4D4" : "#696969" })));
            footer.Foreground = new SolidColorBrush(Color.Parse(official?.EditorForeground ?? (dark ? "#AAB2C0" : "#696969")));
            footer.IsVisible = !preferences.Stealth;
            Height = surface.LineHeight * preferences.LineCount + 12 + (preferences.Stealth ? 0 : 18);
            auto.Interval = TimeSpan.FromSeconds(preferences.Seconds);
            RefreshPage();
            if (TryGetPlatformHandle() != null) SetClickThrough(preferences.ClickThrough);
        }
        finally { applying = false; }
    }

    private bool Fits(string value)
    {
        using var text = surface.CreateLayout(value);
        return text.WidthIncludingTrailingWhitespace <= Math.Max(1, Width - 16);
    }
    private void RefreshPage()
    {
        page = session.Layout(Fits, preferences.LineCount); surface.Lines = page.Lines; surface.InvalidateVisual();
        footer.Text = $"{session.Percent}% · {(auto.IsEnabled ? "自动阅读中" : "右键菜单 · 双击书架 · 右边缘调宽")} " ;
        Avalonia.Automation.AutomationProperties.SetName(surface, string.Join("\n", page.Lines));
    }
    public void Move(bool next)
    {
        if (page == null) return;
        if (next) { if (!session.Next(page.End)) Pause(); }
        else session.Previous(Fits, preferences.LineCount);
        RefreshPage(); library.Changed();
    }
    public void ToggleAuto() { if (auto.IsEnabled) Pause(); else if (!session.Book.Completed) auto.Start(); RefreshPage(); }
    public void Pause() => auto.Stop();
    private void Bookmark() { session.Book.Bookmark = session.Position; library.Changed(); footer.Text = "书签已保存"; }
    private void GoBookmark() { if (session.Book.Bookmark >= 0) { session.Seek(session.Book.Bookmark); RefreshPage(); library.Changed(); } }

    private async Task<string?> Prompt(string title, string description, string initial = "")
    {
        Pause(); bool top = Topmost; Topmost = false; prompting = true;
        try { return await Dialogs.Prompt(this, title, description, initial); }
        finally { Topmost = top; prompting = false; }
    }
    private async Task Find()
    {
        string? text = await Prompt("查找", "输入正文中的文字，从当前位置向后查找。");
        if (string.IsNullOrWhiteSpace(text)) return;
        int index = session.Text.IndexOf(text, Math.Min(session.Position + 1, session.Text.Length), StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = session.Text.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (index >= 0) { session.Seek(index); RefreshPage(); library.Changed(); }
        else await Message("查找", "没有找到这段文字。");
    }
    private async Task Jump()
    {
        string? text = await Prompt("跳转", "输入 0～100 的阅读百分比。", session.Percent.ToString());
        if (text == null) return;
        if (!double.TryParse(text.Trim().TrimEnd('%'), out double number) || !double.IsFinite(number) || number < 0 || number > 100)
        { await Message("跳转", "请输入 0～100 的有效数字。"); return; }
        session.Seek((int)(number / 100 * (session.Text.Length - 1))); RefreshPage(); library.Changed();
    }
    public void SetClickThrough(bool enabled)
    {
        var handle = TryGetPlatformHandle();
        if (handle == null) return;
        if (OperatingSystem.IsWindows()) WindowsIntegration.SetClickThrough(handle.Handle, enabled, (byte)Math.Round(255 * preferences.Opacity / 100.0));
        else if (OperatingSystem.IsMacOS()) MacIntegration.SetClickThrough(handle.Handle, enabled);
        preferences.ClickThrough = enabled;
    }
    private async Task Message(string title, string text)
    {
        prompting = true; bool top = Topmost; Topmost = false;
        try { await Dialogs.Message(this, title, text); }
        finally { prompting = false; Topmost = top; }
    }
    public void EnsureVisible()
    {
        double scale = DesktopScaling;
        var bounds = new PixelRect(Position, new PixelSize((int)(Width * scale), (int)(Height * scale)));
        var screen = Screens.All.FirstOrDefault(screen => screen.WorkingArea.Intersects(bounds)) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        var next = new PixelPoint(Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - bounds.Width)),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - bounds.Height)));
        if (next != Position) Position = next;
    }
    public void DisposeReader() { closing = true; auto.Stop(); positionTimer.Stop(); Close(); }
}

internal sealed class ReadingSurface : Control
{
    public Typeface Typeface { get; set; } = new("sans-serif");
    public double FontSize { get; set; } = 16;
    public IBrush Ink { get; set; } = Brushes.DimGray;
    public IReadOnlyList<string> Lines { get; set; } = [];
    public double LineHeight => Math.Ceiling(FontSize * 1.55);
    public TextLayout CreateLayout(string text) => new(text, Typeface, FontSize, Ink);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        for (int row = 0; row < Lines.Count; row++)
        {
            using var layout = CreateLayout(Lines[row]);
            layout.Draw(context, new Point(8, 6 + row * LineHeight));
        }
    }
}
