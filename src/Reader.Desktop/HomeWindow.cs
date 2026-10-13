using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        Title = "良子小窗"; Width = 480; Height = 360; MinWidth = 420; MinHeight = 300;
        var panel = new StackPanel { Margin = new Thickness(30), Spacing = 14 };
        panel.Children.Add(UiThemes.Header(new TextBlock { Text = "良子小窗", FontSize = 28 }));
        panel.Children.Add(new TextBlock { Text = "阅读与加密聊天，各自打开，随时回到主页。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(UiActions.Row(UiActions.Button("阅读", library.RestoreLibrary), UiActions.Button("聊天", OpenChat)));
        panel.Children.Add(UiActions.Row(UiActions.Button("启动独立聊天服务", StartServer), UiThemes.CreatePicker(store.Pref, () => { library.SavePreferences(); ApplyTheme(); })));
        panel.Children.Add(new TextBlock { Text = "关闭主页会收进托盘。聊天窗口关闭后断开，隐藏时保持连接。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(UiActions.Button("退出良子小窗", library.RequestExit)); Content = panel;
        library.ShowHome = Restore; library.HideAll = ToggleVisibility; library.ThemeChanged = ApplyTheme;
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        ApplyTheme();
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
