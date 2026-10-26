using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Reader.Desktop;
using Reader.Core;
using Reader.Chat;
using System.Reflection;
using System.Net;
using System.Collections.Concurrent;

// 聊天窗口 UI 契约：窄窗口下消息自动换行并可滚动、折叠连接设置后消息区变高，
// 以及真实 TLS 连接下 Enter 发送、Shift+Enter 换行和防重复发送。
class Test
{
    static int failures;
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void Check(bool ok, string message) { Console.WriteLine((ok ? "PASS " : "FAIL ") + message); if (!ok) failures++; }
    [STAThread] static int Main()
    {
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        // 在 Avalonia 调度线程上建窗口并跑主循环；Run 结束后取消循环。
        using var stop = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () => { try { await Run(); } catch (Exception ex) { Console.WriteLine(ex); failures++; } finally { stop.Cancel(); } });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return failures == 0 ? 0 : 1;
    }
    // 最多等 12 秒（600 × 20ms），避免 UI 布局或 TLS 连接慢时误报失败。
    static async Task Until(Func<bool> ready)
    {
        for (int i = 0; i < 600 && !ready(); i++) await Task.Delay(20);
    }
    static async Task Run()
    {
        // ChatWindow 是内部类型，用反射构造；Store 指向临时书库，结束后删除。
        var type = typeof(App).Assembly.GetType("Reader.Desktop.ChatWindow")!;
        var data = Path.Combine(Path.GetTempPath(), "reader-chat-ui-" + Guid.NewGuid().ToString("N"));
        var window = (Window)Activator.CreateInstance(type, new Store(data), (Action)(() => {}), (Action)(() => {}))!;
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
        try
        {
            var transcript = Field<TextBox>(window, "transcript");
            transcript.Text = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"消息 {i}: 这是一段需要在窄窗口中完整自动换行显示的聊天内容。"));
            // 从宽到窄再回到宽，验证布局在每次尺寸变化后都重新收敛。
            foreach (var size in new[] { new Size(640,680), new Size(320,420), new Size(240,300), new Size(640,680) })
            {
                window.Width = size.Width; window.Height = size.Height;
                await Task.Delay(80); window.UpdateLayout();
                Check(transcript.Bounds.Width <= Math.Max(240, window.ClientSize.Width), $"transcript fits width {size}: actual {transcript.Bounds}");
                Check(transcript.Bounds.Height <= window.ClientSize.Height, $"history has bounded scrolling height {size}");
                Check(transcript.Text.Contains("消息 1:") && transcript.Text.Contains("消息 40:"), "resize preserves complete history");
                var historyScroll = transcript.GetVisualDescendants().OfType<ScrollViewer>().Single();
                Check(historyScroll.Extent.Height > historyScroll.Viewport.Height && historyScroll.Viewport.Height > 0, "long history scrolls inside message area");
                Check(historyScroll.Extent.Width <= historyScroll.Viewport.Width + 1, "message text wraps without horizontal clipping");
            }
            // 折叠连接设置后，消息区应拿回垂直空间。
            window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = false;
            window.Width = 320; window.Height = 420; await Task.Delay(100); window.UpdateLayout();
            Check(transcript.Bounds.Height > 80, "collapsing connection settings gives space back to messages");
            // 用真实 ChatServer 和临时证书，避免 mock 掉 TLS/协议层。
            using var cert = ChatSecurity.CreateCertificate();
            using var server = new ChatServer("ui-test", certificate: cert); server.Start(IPAddress.Loopback, 0);
            foreach (var item in new[] { ("host", "127.0.0.1"), ("port", server.Port.ToString()), ("nick", "测试"), ("code", "ui-test"), ("pin", server.Fingerprint) }) Field<TextBox>(window,item.Item1).Text=item.Item2;
            Field<Button>(window,"connect").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Field<Button>(window,"send").IsEnabled);
            Check(Field<Button>(window,"send").IsEnabled, "connected to real TLS server");
            using var peer = new ChatConnection(); var packets = new ConcurrentQueue<ChatPacket>(); peer.Received += packets.Enqueue;
            await peer.ConnectAsync("127.0.0.1",server.Port,"对端","ui-test",server.Fingerprint);
            // Enter 走 KeyDown 事件，验证只发送当前草稿且发送成功后清空。
            var compose = Field<TextBox>(window,"compose"); compose.Text="Enter发送验证";
            compose.RaiseEvent(new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent, Key=Key.Enter, KeyModifiers=KeyModifiers.None });
            await Until(() => packets.Any(p => p.Text == "Enter发送验证"));
            Check(packets.Any(p => p.Text == "Enter发送验证"), "Enter sends exactly the draft through TLS");
            await Until(() => string.IsNullOrEmpty(compose.Text));
            Check(string.IsNullOrEmpty(compose.Text), "successful Enter send clears draft");
            // Shift+Enter 只插入换行，不触发发送。
            compose.Text="保留换行"; compose.CaretIndex=compose.Text.Length;
            compose.RaiseEvent(new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent, Key=Key.Enter, KeyModifiers=KeyModifiers.Shift });
            await Task.Delay(100);
            Check(compose.Text.Contains('\n') && !packets.Any(p=>p.Text.Contains("保留换行")), "Shift+Enter inserts newline without sending");
            Check(packets.Count(p=>p.Text == "Enter发送验证") == 1, "Enter does not send duplicates");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(data, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
