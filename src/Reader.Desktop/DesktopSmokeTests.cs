using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Reader.Chat;
using Reader.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;

namespace Reader.Desktop;

internal static class DesktopSmokeTests
{
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Click(object target, string name) => Field<Button>(target, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Desktop smoke: " + message); }
    private static async Task Wait(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(12);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
        Check(condition(), "timed out waiting for UI/network state");
    }
    public static async Task Run(HomeWindow home, MainWindow library, Store store)
    {
        home.Restore();
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
        Button ChoiceButton(Window dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(item => item.Name == name);
        home.PickChat();
        await Wait(() => lifetime.Windows.Any(window => window.Name == "ChatModeDialog"));
        var modeDialog = lifetime.Windows.Single(window => window.Name == "ChatModeDialog");
        ChoiceButton(modeDialog, "JoinRoom").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => home.Chat != null && home.Chat.IsVisible);
        Check(modeDialog.IsVisible == false && lifetime.Windows.All(window => window.Name != "ChatModeDialog"), "join choice closes and opens the chat client");
        var chat = home.Chat!;
        Check(home.IsVisible && chat.IsVisible, "opening chat preserves visible home");
        chat.Close(); Check(home.IsVisible && home.Chat == null, "closing chat preserves home");
        home.Hide(); home.OpenChat(); chat = home.Chat!;
        Check(!home.IsVisible, "opening chat preserves hidden home");
        using var certificate = ChatSecurity.CreateCertificate();
        using var server = new ChatServer("smoke-room", certificate: certificate); server.Start(IPAddress.Loopback, 0);
        Field<TextBox>(chat, "host").Text = "127.0.0.1"; Field<TextBox>(chat, "port").Text = server.Port.ToString();
        Field<TextBox>(chat, "nick").Text = "窗口测试"; Field<TextBox>(chat, "code").Text = "smoke-room"; Field<TextBox>(chat, "pin").Text = server.Fingerprint;
        Click(chat, "connect");
        try { await Wait(() => Field<Button>(chat, "send").IsEnabled); }
        catch (Exception ex) { throw new InvalidOperationException(Field<TextBlock>(chat, "state").Text, ex); }
        using var peer = new ChatConnection(); var packets = new ConcurrentQueue<ChatPacket>(); peer.Received += packets.Enqueue;
        await peer.ConnectAsync("127.0.0.1", server.Port, "对端", "smoke-room", server.Fingerprint);
        await peer.SendAsync("跨平台聊天 · 中文 😊"); await Wait(() => Field<TextBox>(chat, "transcript").Text?.Contains("跨平台聊天") == true);
        Field<TextBox>(chat, "compose").Text = "窗口发送"; Click(chat, "send"); await Wait(() => packets.Any(p => p.Text == "窗口发送"));
        Capture(chat, Path.Combine(store.Root, "chat.png"));
        home.Restore(); home.ToggleVisibility(); Check(!home.IsVisible && !chat.IsVisible && !library.IsVisible, "global hide covers all modules");
        Check(Field<ChatConnection>(chat, "connection").Connected, "hide retains connection");
        home.ToggleVisibility(); Check(chat.IsVisible && home.IsVisible, "global restore restores visible modules");
        home.Hide(); library.Close(); await Wait(() => !library.IsVisible);
        Check(chat.IsVisible && Field<ChatConnection>(chat, "connection").Connected && !home.IsVisible, "closing reader leaves chat and home alone");
        server.Dispose(); await Wait(() => !Field<Button>(chat, "send").IsEnabled);
        using var restart = new ChatServer("smoke-room", certificate: certificate); restart.Start(IPAddress.Loopback, server.Port);
        await Wait(() => Field<Button>(chat, "send").IsEnabled);
        await Wait(() => !(Field<TextBox>(chat, "transcript").Text ?? "").Contains("跨平台聊天"));
        Check(restart.Online == 1, "chat auto reconnects with fresh history");
        chat.Close(); await Wait(() => restart.Online == 0);
        Check(Field<TextBox>(chat, "transcript").Text is null or "" && Field<TextBox>(chat, "code").Text is null or "", "closing clears cache and secret");
        Check(!home.IsVisible, "closing chat preserves hidden home");
        home.Restore(); Capture(home, Path.Combine(store.Root, "home.png"));
        // First close with no exit choice: closing asks once, choosing tray remembers and hides.
        home.Close(); await Wait(() => lifetime.Windows.Any(window => window.Name == "ExitModeDialog"));
        var exitDialog = lifetime.Windows.Single(window => window.Name == "ExitModeDialog");
        Check(store.Pref.ExitMode == Preferences.ExitUnset, "first close asks because no exit choice was saved");
        exitDialog.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "ExitToTray").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => !home.IsVisible && lifetime.Windows.All(window => window.Name != "ExitModeDialog"));
        Check(store.Pref.ExitMode == Preferences.ExitToTray, "choosing tray remembers the choice");
        // The settings window owns the same choice, so it can change what the next close does.
        home.Restore(); _ = home.OpenSettings();
        await Wait(() => lifetime.Windows.Any(window => window.Name == "HomeSettingsDialog"));
        var settingsDialog = lifetime.Windows.Single(window => window.Name == "HomeSettingsDialog");
        Check(settingsDialog.GetVisualDescendants().OfType<Button>().Any(item => item.Content?.ToString() == "切换主题"), "settings window offers theme switching");
        settingsDialog.GetVisualDescendants().OfType<RadioButton>().Single(item => item.Name == "ExitProgram").IsChecked = true;
        settingsDialog.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "SaveHomeSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => store.Pref.ExitMode == Preferences.ExitProgram && lifetime.Windows.All(window => window.Name != "HomeSettingsDialog"));
        Check(home.IsVisible, "settings window closes after saving the quit choice");
        home.Restore(); home.PickChat();
        await Wait(() => lifetime.Windows.Any(window => window.Name == "ChatModeDialog"));
        modeDialog = lifetime.Windows.Single(window => window.Name == "ChatModeDialog");
        // Creating would spawn a real service process; the dialog only needs to offer the choice.
        Check(ChoiceButton(modeDialog, "CreateRoom").Content?.ToString() == "创建房间", "dialog offers hosting a room");
        ChoiceButton(modeDialog, "CancelChatMode").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => modeDialog.IsVisible == false);
        Check(lifetime.Windows.All(window => window.Name != "ChatModeDialog") && home.Chat == null, "cancel closes the choice without opening either chat mode");
        // Hotkey editing is a real window in the shipping build; smoke mode skips the native registration,
        // so the dialog is driven directly. Validity must gate the save button row by row.
        var hotkeyUi = new HotkeyWindow(["H", "Right", "Left", "M", "Space"]);
        var keys = Field<TextBox[]>(hotkeyUi, "boxes");
        var saveHotkeys = Field<Button>(hotkeyUi, "save");
        Check(saveHotkeys.IsEnabled, "hotkey dialog accepts the defaults");
        keys[0].Text = "Right"; Check(!saveHotkeys.IsEnabled, "hotkey dialog blocks a duplicate key");
        Check(Field<TextBlock[]>(hotkeyUi, "errors")[0].Text == "与其他行重复", "duplicate key is reported on its own row");
        keys[0].Text = "F9"; Check(saveHotkeys.IsEnabled, "hotkey dialog accepts a different valid key");
        keys[0].Text = "Nope"; Check(!saveHotkeys.IsEnabled && Field<TextBlock[]>(hotkeyUi, "errors")[0].Text == "无效按键", "hotkey dialog blocks an unsupported key name");
        keys[0].Text = "f9"; Check(saveHotkeys.IsEnabled, "hotkey dialog accepts a lower-case key name");
        saveHotkeys.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(hotkeyUi.Result is ["F9", "Right", "Left", "M", "Space"], "saved hotkeys are normalised to canonical names");
        hotkeyUi.Close();
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var serviceUi = new ServerWindow(); serviceUi.Show();
        Field<ComboBox>(serviceUi, "address").SelectedItem = "127.0.0.1"; Field<TextBox>(serviceUi, "port").Text = port.ToString();
        Click(serviceUi, "toggle"); var service = Field<ChatServer>(serviceUi, "server");
        using var client = new ChatConnection();
        await client.ConnectAsync("127.0.0.1", port, "服务测试", Field<TextBox>(serviceUi, "code").Text!, service.Fingerprint);
        Check(client.Connected, "service window starts real TLS server"); Capture(serviceUi, Path.Combine(store.Root, "server.png"));
        Click(serviceUi, "toggle"); await Wait(() => !client.Connected); serviceUi.Close();
        // The assistant joins through whichever address the window binds, so a LAN deployment keeps working.
        // Binding to a non-loopback address is what makes that wiring observable: a hardcoded loopback dial
        // would leave the room empty forever while the status line still claimed the assistant had joined.
        IPAddress? lan = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."));
        if (lan == null) Console.WriteLine("Desktop smoke: no LAN address, skipping the assistant bind check");
        else
        {
            reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int agentPort = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            var lanUi = new ServerWindow(); lanUi.Show();
            Field<ComboBox>(lanUi, "address").SelectedItem = lan.ToString(); Field<TextBox>(lanUi, "port").Text = agentPort.ToString();
            Field<CheckBox>(lanUi, "agentEnabled").IsChecked = true;
            Field<TextBox>(lanUi, "agentEndpoint").Text = "http://127.0.0.1:1/v1/chat";
            Field<TextBox>(lanUi, "agentKey").Text = "smoke-key"; Field<TextBox>(lanUi, "agentModel").Text = "smoke-model";
            Field<TextBox>(lanUi, "agentNick").Text = "小助手";
            Click(lanUi, "toggle"); var lanService = Field<ChatServer>(lanUi, "server");
            try { await Wait(() => lanService.Online == 1); }
            catch (Exception ex) { throw new InvalidOperationException(Field<TextBlock>(lanUi, "status").Text, ex); }
            // The old defect was invisible precisely because the window reported success either way.
            await Wait(() => (Field<TextBlock>(lanUi, "status").Text ?? "").Contains("助手：已加入房间"));
            Capture(lanUi, Path.Combine(store.Root, "server-lan.png"));
            Click(lanUi, "toggle"); lanUi.Close();
        }
        File.WriteAllText(Path.Combine(store.Root, "shell-chat-smoke.ok"), "Home lifecycle, independent close, TLS UI send/receive, global hide/restore, reconnect, cache cleanup, server window, assistant on a LAN bind passed.");
        library.RestoreLibrary();
    }
    private static void Capture(Window window, string path)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Avalonia.Vector(96, 96));
        bitmap.Render(window); bitmap.Save(path);
    }
}
