using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Reader.Core;
using System.Diagnostics;

namespace Reader.Desktop;

internal sealed class HomeWindow : Window
{
    private readonly MainWindow library;
    private readonly Store store;
    private ChatWindow? chat;
    private readonly List<Window> hidden = new();
    private bool exiting;
    internal ChatWindow? Chat => chat;
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
        library.ShowHome = Restore; library.HideAll = ToggleVisibility; library.ThemeChanged = ApplyTheme;
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
    internal async Task OpenAbout() => await new AboutWindow().ShowDialog(this);

    internal async Task OpenSettings()
    {
        if (await new HomeSettingsWindow(store.Pref).ShowDialog<bool>(this))
        {
            library.SavePreferences();
            ApplyTheme();
        }
    }
    // 关闭主页：按用户选择收进托盘或退出程序；尚未选择时询问一次并记住。
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
            if (answer == null) return;
            store.Pref.ExitMode = answer.Value;
            library.SavePreferences();
        }
        if (store.Pref.ExitMode == Preferences.ExitProgram) library.RequestExit();
        else Hide();
    }
    public void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    internal void OpenChat()
    {
        if (chat == null)
        {
            var window = new ChatWindow(store, library.SavePreferences, Restore); chat = window;
            window.Closed += (_, _) => { hidden.Remove(window); if (chat == window) chat = null; };
        }
        chat.Show(); chat.WindowState = WindowState.Normal; chat.Activate();
    }
    // One entry button, two chat modes: hosting a room or joining one.
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
        if (createRoom == true) StartServer(); else if (createRoom == false) OpenChat();
    }
    private async void StartServer()
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("--server");
            if (Program.DataDirectory != null) { start.ArgumentList.Add("--data"); start.ArgumentList.Add(Program.DataDirectory); }
            using var process = Process.Start(start);
        }
        catch (Exception ex) { await Dialogs.Message(this, "服务启动失败", ex.Message); }
    }
    internal void ToggleVisibility()
    {
        var visible = new List<Window>();
        if (IsVisible) visible.Add(this); if (library.IsVisible) visible.Add(library); if (chat?.IsVisible == true) visible.Add(chat);
        if (library.VisibleReader is { } reader) visible.Add(reader);
        if (visible.Count > 0) { hidden.Clear(); hidden.AddRange(visible); library.PauseReader(); foreach (var window in hidden) { window.Closed -= RemoveHidden; window.Closed += RemoveHidden; window.Hide(); } }
        else if (hidden.Count > 0) { foreach (var window in hidden.ToArray()) window.Show(); hidden.Clear(); }
        else Restore();
    }
    private void RemoveHidden(object? sender, EventArgs e) { if (sender is Window window) hidden.Remove(window); }
    public void ApplyTheme()
    {
        UiThemes.Apply(store.Pref);
        library.RefreshTheme();
    }
    public void DisposeModules() { exiting = true; hidden.Clear(); chat?.Close(); library.DisposeServices(); }
}
