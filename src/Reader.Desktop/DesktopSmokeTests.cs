using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
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
    private static async Task<Window> Confirmation(Window owner)
    {
        await Wait(() => owner.OwnedWindows.Any(w => w.Name == "ConfirmationDialog" && w.IsVisible));
        return owner.OwnedWindows.Single(w => w.Name == "ConfirmationDialog" && w.IsVisible);
    }
    private static Button DialogButton(Window dialog, string name) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
    private static void Answer(Window dialog, string name) => DialogButton(dialog, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static int HistoryCount(ChatServer server) => (int)typeof(ChatServer).GetProperty("HistoryCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
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
        // Recording a combo only works if the system stops routing it to the app's own hotkeys first,
        // so the real registration is exercised here rather than assumed.
        var held = new Hotkeys(store.Root, _ => { });
        Check(held.FailedCount == 0, "hotkeys register on this platform");
        var blocked = new Hotkeys(store.Root, _ => { });
        Check(blocked.FailedCount == 5, "the same combos cannot be taken twice while held");
        blocked.Dispose();
        held.Suspend();
        var afterSuspend = new Hotkeys(store.Root, _ => { });
        Check(afterSuspend.FailedCount == 0, "suspend releases the system hotkeys so a capture can read them");
        afterSuspend.Dispose();
        held.Resume();
        Check(held.FailedCount == 0, "resume takes the hotkeys back");
        held.Dispose();
        var hotkeyUi = new HotkeyWindow(["H", "Right", "Left", "M", "Space"]);
        var keys = Field<TextBox[]>(hotkeyUi, "boxes");
        var saveHotkeys = Field<Button>(hotkeyUi, "save");
        var hkErrors = Field<TextBlock[]>(hotkeyUi, "errors");
        // Keys are raised on the window, matching real routing: the boxes are never in the route
        // unless focus happens to sit on them, which is what made per-box handlers dead on open.
        void Press(int row, Key keyName, KeyModifiers modifiers)
        {
            hotkeyUi.Select(row);
            hotkeyUi.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = keyName, KeyModifiers = modifiers });
        }
        Check(saveHotkeys.IsEnabled, "hotkey dialog accepts the defaults");
        hotkeyUi.Select(1);
        Check(hkErrors[1].Text!.Contains("按") && hkErrors[0].Text == "", "the selected row shows where the next key lands");
        Press(0, Key.F9, KeyModifiers.Control | KeyModifiers.Alt);
        Check(keys[0].Text == "F9" && saveHotkeys.IsEnabled, "holding the modifier combo records the pressed key");
        Press(0, Key.D1, KeyModifiers.Control | KeyModifiers.Alt);
        Check(keys[0].Text == "F9", "unsupported keys are ignored instead of replacing the value");
        Press(0, Key.F9, KeyModifiers.None);
        Check(keys[0].Text == "F9" && hkErrors[0].Text!.Contains("需同时按住"), "capture without the modifier combo is refused with a hint");
        // Row 1 already holds "Right", so recording Right into row 0 has to flag both rows.
        Press(0, Key.Right, KeyModifiers.Control | KeyModifiers.Alt);
        Check(keys[0].Text == "Right" && !saveHotkeys.IsEnabled, "a key already used by another row is refused");
        Check(hkErrors[0].Text == "与其他行重复" && hkErrors[1].Text == "与其他行重复", "both rows of a duplicate are flagged");
        Press(0, Key.F9, KeyModifiers.Control | KeyModifiers.Alt);
        Check(saveHotkeys.IsEnabled && hkErrors.Skip(1).All(error => error.Text == ""), "resolving the duplicate clears the other rows");
        hotkeyUi.Show();
        await Task.Delay(200);
        Capture(hotkeyUi, Path.Combine(store.Root, "hotkeys.png"));
        // 每行的清除图标解绑该功能，空位必须仍然可保存；重置只恢复这一行的默认键。
        var clearIcons = keys.Select(box => box.InnerRightContent).OfType<Button>().ToArray();
        Check(clearIcons.Length == 5 && clearIcons[4].Name == "Clear4", "every row carries its own clear icon inside the box");
        clearIcons[4].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(keys[4].Text == "" && saveHotkeys.IsEnabled, "clearing one row unbinds it without blocking the save");
        hotkeyUi.Select(4); hotkeyUi.Reset(4);
        Check(keys[4].Text == "Space" && keys[3].Text == "M", "resetting a row restores only that row's default");
        hotkeyUi.Select(4); hotkeyUi.Clear(4);
        saveHotkeys.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(hotkeyUi.Result is ["F9", "Right", "Left", "M", ""], "an unbound row is saved as an empty slot");
        hotkeyUi.Close();
        // 未绑定的空位能从文件里读回、不注册，其余按键照常注册。
        File.WriteAllText(Path.Combine(store.Root, "hotkeys.json"), "[\"F9\",\"Right\",\"Left\",\"M\",\"\"]");
        var unbound = new Hotkeys(store.Root, _ => { });
        Check(unbound.Keys[4].Length == 0 && unbound.FailedCount == 0, "an unbound slot loads and registers nothing while the rest stay bound");
        Check(unbound.Combo(4) == "未绑定", "an unbound slot reads as unbound in the library caption");
        unbound.Dispose();
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var serviceUi = new ServerWindow(); serviceUi.Show();
        Field<ComboBox>(serviceUi, "address").SelectedItem = "127.0.0.1"; Field<TextBox>(serviceUi, "port").Text = port.ToString();
        Click(serviceUi, "toggle"); var service = Field<ChatServer>(serviceUi, "server");
        using var client = new ChatConnection(); var servicePackets = new ConcurrentQueue<ChatPacket>(); client.Received += servicePackets.Enqueue;
        await client.ConnectAsync("127.0.0.1", port, "服务测试", Field<TextBox>(serviceUi, "code").Text!, service.Fingerprint);
        Check(client.Connected, "service window starts real TLS server"); Capture(serviceUi, Path.Combine(store.Root, "server.png"));
        await client.SendAsync("取消停止后保留这条消息"); await Wait(() => servicePackets.Any(p => p.Text == "取消停止后保留这条消息"));
        int history = HistoryCount(service);
        Click(serviceUi, "toggle"); var confirmation = await Confirmation(serviceUi);
        string stopMessage = ((TextBlock)((StackPanel)confirmation.Content!).Children[0]).Text!;
        Check(stopMessage.Contains("(｡•́︿•̀｡)") && stopMessage.Contains("清空聊天历史"), "stop button keeps the existing emoticon warning");
        Check(((TextBlock)DialogButton(confirmation, "Confirm").Content!).Text!.Contains("(╯°□°)╯")
            && DialogButton(confirmation, "Cancel").IsDefault && DialogButton(confirmation, "Cancel").IsCancel, "stop warning keeps emoticon buttons and defaults to cancel");
        Check(client.Connected && ReferenceEquals(service, Field<ChatServer>(serviceUi, "server")) && HistoryCount(service) == history, "opening stop confirmation keeps connections and history");
        await Task.Delay(100); confirmation.UpdateLayout();
        Capture(confirmation, Path.Combine(store.Root, "server-stop-confirmation.png"));
        Click(serviceUi, "toggle"); serviceUi.Close();
        Check(serviceUi.OwnedWindows.Count(w => w.Name == "ConfirmationDialog" && w.IsVisible) == 1, "repeated stop and close requests share one confirmation");
        Answer(confirmation, "Cancel"); await Wait(() => !confirmation.IsVisible);
        Check(serviceUi.IsVisible && client.Connected && ReferenceEquals(service, Field<ChatServer>(serviceUi, "server")) && HistoryCount(service) == history, "cancel keeps the service, window, client and history");
        Click(serviceUi, "toggle"); confirmation = await Confirmation(serviceUi); Answer(confirmation, "Confirm");
        await Wait(() => Field<ChatServer?>(serviceUi, "server") == null && !client.Connected);
        Check(serviceUi.IsVisible && HistoryCount(service) == 0, "confirmed stop clears history without closing the service window");
        Check(Equals(Field<Button>(serviceUi, "toggle").Content, "启动服务") && !Field<Button>(serviceUi, "copy").IsEnabled
            && Field<ComboBox>(serviceUi, "address").IsEnabled && Field<TextBox>(serviceUi, "port").IsEnabled && Field<TextBox>(serviceUi, "code").IsEnabled, "confirmed stop restores idle controls");
        Click(serviceUi, "toggle"); service = Field<ChatServer>(serviceUi, "server");
        using var restartedClient = new ChatConnection();
        await restartedClient.ConnectAsync("127.0.0.1", port, "重启测试", Field<TextBox>(serviceUi, "code").Text!, service.Fingerprint);
        serviceUi.Close(); confirmation = await Confirmation(serviceUi);
        Check(((TextBlock)((StackPanel)confirmation.Content!).Children[0]).Text == stopMessage, "window close and stop button use the same warning after restart");
        Answer(confirmation, "Cancel"); await Wait(() => !confirmation.IsVisible);
        Check(serviceUi.IsVisible && restartedClient.Connected, "cancelled window close keeps the restarted service connected");
        serviceUi.Close(); confirmation = await Confirmation(serviceUi); Answer(confirmation, "Confirm");
        await Wait(() => !serviceUi.IsVisible && !restartedClient.Connected);
        Check(HistoryCount(service) == 0, "confirmed window close still shuts down the service and clears history");
        var idleUi = new ServerWindow(); idleUi.Show(); idleUi.Close();
        Check(!idleUi.IsVisible && !idleUi.OwnedWindows.Any(w => w.IsVisible), "idle server window closes without a confirmation");
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
            var agent = Field<AgentBridge>(lanUi, "bridge");
            Click(lanUi, "toggle"); confirmation = await Confirmation(lanUi); Answer(confirmation, "Cancel");
            await Wait(() => !confirmation.IsVisible);
            Check(ReferenceEquals(agent, Field<AgentBridge>(lanUi, "bridge")) && lanService.Online == 1, "cancelled stop leaves the assistant connected");
            Click(lanUi, "toggle"); confirmation = await Confirmation(lanUi); Answer(confirmation, "Confirm");
            await Wait(() => Field<ChatServer?>(lanUi, "server") == null && Field<AgentBridge?>(lanUi, "bridge") == null && lanService.Online == 0);
            lanUi.Close();
        }
        File.WriteAllText(Path.Combine(store.Root, "server-stop-confirmation-smoke.ok"), "Stop button and window close share the emoticon warning; duplicate requests, cancel, confirm, preserved history, idle controls and restart passed."
            + (lan == null ? " Assistant check skipped: no LAN address." : " Assistant cancellation/stop passed."));
        File.WriteAllText(Path.Combine(store.Root, "shell-chat-smoke.ok"), "Home lifecycle, independent close, TLS UI send/receive, global hide/restore, reconnect, cache cleanup, server window, assistant on a LAN bind passed.");
        library.RestoreLibrary();
    }
    private static void Capture(Window window, string path)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Avalonia.Vector(96, 96));
        bitmap.Render(window); bitmap.Save(path);
    }
}
