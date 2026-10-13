using Avalonia.Controls;
using Avalonia.Interactivity;
using Reader.Chat;
using Reader.Core;
using System.Collections.Concurrent;
using System.Net;
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
        home.Restore(); home.OpenChat(); var chat = home.Chat!;
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
        home.Restore(); Capture(home, Path.Combine(store.Root, "home.png")); home.Close(); Check(!home.IsVisible, "home close hides rather than exits");
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var serviceUi = new ServerWindow(); serviceUi.Show();
        Field<ComboBox>(serviceUi, "address").SelectedItem = "127.0.0.1"; Field<TextBox>(serviceUi, "port").Text = port.ToString();
        Click(serviceUi, "toggle"); var service = Field<ChatServer>(serviceUi, "server");
        using var client = new ChatConnection();
        await client.ConnectAsync("127.0.0.1", port, "服务测试", Field<TextBox>(serviceUi, "code").Text!, service.Fingerprint);
        Check(client.Connected, "service window starts real TLS server"); Capture(serviceUi, Path.Combine(store.Root, "server.png"));
        Click(serviceUi, "toggle"); await Wait(() => !client.Connected); serviceUi.Close();
        File.WriteAllText(Path.Combine(store.Root, "shell-chat-smoke.ok"), "Home lifecycle, independent close, TLS UI send/receive, global hide/restore, reconnect, cache cleanup, server window passed.");
        library.RestoreLibrary();
    }
    private static void Capture(Window window, string path)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Avalonia.Vector(96, 96));
        bitmap.Render(window); bitmap.Save(path);
    }
}
