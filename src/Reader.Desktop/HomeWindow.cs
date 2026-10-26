using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Reader.Core;
using System.Diagnostics;

namespace Reader.Desktop;

/// <summary>
/// 主页窗口：提供阅读与聊天入口、主题/退出设置，并协调各模块窗口的显示、隐藏与关闭。
/// </summary>
/// <remarks>
/// 主页隐藏到托盘时聊天窗口保持连接；真正退出统一交给 <see cref="MainWindow.RequestExit"/>。
/// </remarks>
internal sealed class HomeWindow : Window
{
    private readonly MainWindow library;
    private readonly Store store;
    // 延迟创建、关闭后置空；隐藏主页不会关闭聊天，因此连接得以保留。
    private ChatWindow? chat;
    // 全局隐藏时记录被隐藏的窗口，恢复时按原样重新显示。
    private readonly List<Window> hidden = new();
    // 标记真正退出，避免 Closing 拦截再次取消。
    private bool exiting;
    /// <summary>当前聊天窗口；尚未创建或已关闭时为 null。</summary>
    internal ChatWindow? Chat => chat;
    /// <summary>创建主页窗口。</summary>
    /// <param name="store">书库存储，用于读取设置偏好。</param>
    /// <param name="library">书库主窗口；主页把显示/隐藏/主题回调注入其中。</param>
    public HomeWindow(Store store, MainWindow library)
    {
        this.store = store; this.library = library;
        Title = "良子小窗"; Icon = AppIcon.Shared;
        Width = 480; MinWidth = 420; SizeToContent = SizeToContent.Height; CanResize = false;
        var panel = new StackPanel { Margin = new Thickness(30), Spacing = 14 };
        panel.Children.Add(UiThemes.Header(BuildHeader()));
        panel.Children.Add(new TextBlock { Text = "阅读与加密聊天，各自打开，随时回到主页。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(UiActions.Row(UiActions.Button("阅读", library.RestoreLibrary), UiActions.Button("聊天", PickChat)));
        panel.Children.Add(new TextBlock { Text = "关闭主页会收进托盘或退出，可在设置中选择。聊天窗口关闭后断开，隐藏时保持连接。", TextWrapping = TextWrapping.Wrap });
        Content = panel;
        // 主页把窗口协调回调注入书库，避免书库直接依赖主页的具体实现。
        library.ShowHome = Restore; library.HideAll = ToggleVisibility; library.ThemeChanged = ApplyTheme;
        // 关闭主页先按用户选择收进托盘或退出；只有退出流程才放行 Closing。
        Closing += async (_, e) => { if (!exiting && IsVisible) { e.Cancel = true; await HandleClose(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) _ = HandleClose(); };
        ApplyTheme();
    }
    private StackPanel BuildHeader()
    {
        // 标题本身就是「关于」的入口：做成无边框按钮，外观仍是标题，但可点、有手型和键盘焦点。
        var captionText = new TextBlock { Name = "AboutTitle", Text = "良子小窗", FontSize = 28 };
        var caption = new Button
        {
            Name = "AboutLink", Content = captionText, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        
        caption.Click += async (_, _) => await OpenAbout();
        // 标题长得不像按钮，悬停给个下划线，否则没人知道它能点。
        
        caption.PointerEntered += (_, _) => captionText.TextDecorations = TextDecorations.Underline;
        caption.PointerExited += (_, _) => captionText.TextDecorations = null;
        var settings = new Button { Name = "HomeSettings", Content = "设置", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        settings.Click += async (_, _) => await OpenSettings();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(caption, 0); Grid.SetColumn(settings, 1);
        header.Children.Add(caption); header.Children.Add(settings);
        return new StackPanel { Children = { header } };
    }
    /// <summary>以模态方式打开「关于」窗口。</summary>
    internal async Task OpenAbout() => await new AboutWindow().ShowDialog(this);

    /// <summary>打开主页设置；保存后立即持久化偏好并刷新主题。</summary>
    internal async Task OpenSettings()
    {
        if (await new HomeSettingsWindow(store.Pref).ShowDialog<bool>(this))
        {
            // 对话框只在保存时返回 true；主题偏好已在对话框内改好，这里统一应用并落盘。
            library.SavePreferences();
            ApplyTheme();
        }
    }
    // 关闭主页：按用户选择收进托盘或退出程序；尚未选择时询问一次并记住。
    /// <summary>处理主页关闭：首次询问退出方式并记住，之后按已保存的偏好执行。</summary>
    internal async Task HandleClose()
    {
        if (store.Pref.ExitMode == Preferences.ExitUnset)
        {
            var tray = new Button { Name = "ExitToTray", Content = "最小化到托盘", IsDefault = true };
            var quit = new Button { Name = "ExitProgram", Content = "完全退出程序" };
            var cancel = new Button { Name = "CancelExit", Content = "取消", IsCancel = true };
            var ask = new Window
            {
                Name = "ExitModeDialog", Title = "关闭良子小窗", Width = 380, SizeToContent = SizeToContent.Height,
                MinHeight = 160, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
            };
            int? answer = null;
            tray.Click += (_, _) => { answer = Preferences.ExitToTray; ask.Close(); };
            quit.Click += (_, _) => { answer = Preferences.ExitProgram; ask.Close(); };
            cancel.Click += (_, _) => ask.Close();
            ask.Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children = {
                new TextBlock { Text = "关闭主页时要最小化到托盘，还是完全退出程序？选择会被记住，之后可在设置中修改。", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Vertical, Spacing = 10, Children = { tray, quit } },
                cancel } };
            await ask.ShowDialog<bool?>(this);
            // 取消（null）保持主页原状；按钮只写局部变量，持久化统一放在下面。
            if (answer == null) return;
            store.Pref.ExitMode = answer.Value;
            library.SavePreferences();
        }
        // 退出交给书库的统一保存/关闭流程；收进托盘只需隐藏。
        if (store.Pref.ExitMode == Preferences.ExitProgram) library.RequestExit();
        else Hide();
    }
    /// <summary>显示并激活主页窗口（从托盘或隐藏状态恢复）。</summary>
    public void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    /// <summary>打开聊天窗口；已有实例时复用，避免丢失连接状态。</summary>
    internal void OpenChat()
    {
        if (chat == null)
        {
            var window = new ChatWindow(store, library.SavePreferences, Restore); chat = window;
            // 聊天窗口关闭后释放引用；全局隐藏列表里也同步移除，防止恢复时显示已关闭窗口。
            window.Closed += (_, _) => { hidden.Remove(window); if (chat == window) chat = null; };
        }
        chat.Show(); chat.WindowState = WindowState.Normal; chat.Activate();
    }
    // One entry button, two chat modes: hosting a room or joining one.
    /// <summary>弹出聊天模式选择：创建房间或加入房间。</summary>
    /// <remarks>选择结果在模态框关闭后处理；创建房间会启动独立的 --server 进程。</remarks>
    internal async void PickChat()
    {
        bool? createRoom = null;
        var choice = new Window
        {
            Name = "ChatModeDialog", Title = "良子小窗 · 聊天", Width = 320, SizeToContent = SizeToContent.Height,
            MinHeight = 160, MaxHeight = 400, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var create = new Button { Name = "CreateRoom", Content = "创建房间" };
        var join = new Button { Name = "JoinRoom", Content = "加入房间" };
        var cancel = new Button { Name = "CancelChatMode", Content = "取消", IsCancel = true };
        create.Click += (_, _) => { createRoom = true; choice.Close(); };
        join.Click += (_, _) => { createRoom = false; choice.Close(); };
        cancel.Click += (_, _) => choice.Close();
        choice.Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children = {
            new TextBlock { Text = "要创建一个聊天房间，还是加入别人的房间？", TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Vertical, Spacing = 10, Children = { create, join } },
            cancel } };
        await choice.ShowDialog<bool?>(this);
        // 选择框关闭后再打开目标模式，避免两个模态窗口叠在一起。
        if (createRoom == true) StartServer(); else if (createRoom == false) OpenChat();
    }
    // 以独立进程启动聊天服务，服务崩溃不会拖垮主页；dotnet 宿主需要显式传入程序集路径。
    private async void StartServer()
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--server");
            // 传递当前数据目录，保证服务进程与主页使用同一份设置和聊天配置。
            if (Program.DataDirectory != null) { start.ArgumentList.Add("--data"); start.ArgumentList.Add(Program.DataDirectory); }
            using var process = Process.Start(start);
        }
        catch (Exception ex) { await Dialogs.Message(this, "服务启动失败", ex.Message); }
    }
    /// <summary>
    /// 全局隐藏/恢复所有模块窗口：有可见窗口时隐藏它们，否则恢复上次隐藏的集合。
    /// </summary>
    /// <remarks>隐藏前会暂停自动阅读；聊天窗口隐藏时保持连接。</remarks>
    internal void ToggleVisibility()
    {
        var visible = new List<Window>();
        if (IsVisible) visible.Add(this); if (library.IsVisible) visible.Add(library); if (chat?.IsVisible == true) visible.Add(chat);
        if (library.VisibleReader is { } reader) visible.Add(reader);
        // 隐藏前暂停自动阅读，避免后台继续翻页；记录集合供再次按下快捷键时恢复。
        if (visible.Count > 0) { hidden.Clear(); hidden.AddRange(visible); library.PauseReader(); foreach (var window in hidden) { window.Closed -= RemoveHidden; window.Closed += RemoveHidden; window.Hide(); } }
        else if (hidden.Count > 0) { foreach (var window in hidden.ToArray()) window.Show(); hidden.Clear(); }
        else Restore();
    }
    // 已隐藏的窗口被用户单独关闭时，不再参与后续恢复。
    private void RemoveHidden(object? sender, EventArgs e) { if (sender is Window window) hidden.Remove(window); }
    /// <summary>应用当前偏好中的界面主题，并通知书库刷新主题相关显示。</summary>
    public void ApplyTheme()
    {
        UiThemes.Apply(store.Pref);
        library.RefreshTheme();
    }
    /// <summary>应用退出前调用：关闭聊天、停止书库后台服务并释放资源。</summary>
    public void DisposeModules() { exiting = true; hidden.Clear(); chat?.Close(); library.DisposeServices(); }
}
