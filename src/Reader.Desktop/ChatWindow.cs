using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Reader.Chat;
using Reader.Core;
using System.Security.Authentication;

namespace Reader.Desktop;

internal sealed class ChatWindow : Window
{
    private readonly TextBox host = new() { Watermark = "服务地址", MaxLength = 253 };
    private readonly TextBox port = new() { Text = "38417", Watermark = "端口", MaxLength = 5 };
    private readonly TextBox nick = new() { Watermark = "昵称", MaxLength = 20 };
    private readonly TextBox code = new() { Watermark = "房间码", MaxLength = 64, PasswordChar = '●' };
    private readonly TextBox pin = new() { Watermark = "SHA-256 服务指纹", MaxLength = 64 };
    private readonly TextBox compose = new() { Watermark = "Enter 发送，Shift+Enter 换行", AcceptsReturn = true, MaxLength = 500, MinHeight = 70 };
    private readonly TextBlock state = new() { Text = "未连接", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox transcript = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly Button connect = new() { Content = "连接" }, send = new() { Content = "发送" }, paste = new() { Content = "粘贴连接信息" };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ChatInbox inbox = new();
    private readonly Queue<string> lines = new();
    private readonly Store store;
    private readonly Action save;
    private ChatConnection? connection;
    private bool wanted, joining, sending, closed;
    private int generation;
    private DateTime retryAt;
    private string session = "";
    private long lastId;

    public ChatWindow(Store store, Action save, Action home)
    {
        this.store = store; this.save = save;
        Title = "良子小窗 · 聊天"; Width = 640; Height = 680; MinWidth = 0; MinHeight = 0; CanResize = true;
        host.Text = store.Pref.ChatHost; nick.Text = store.Pref.ChatNick; port.Text = store.Pref.ChatPort.ToString();
        var top = new StackPanel { Spacing = 8 };
        var navigation = new WrapPanel { Orientation = Orientation.Horizontal };
        navigation.Children.Add(UiActions.Button("主页", home)); navigation.Children.Add(UiActions.Button("隐藏", Hide)); navigation.Children.Add(paste);
        top.Children.Add(UiThemes.Header(navigation));
        var connectionFields = new StackPanel { Spacing = 8 };
        connectionFields.Children.Add(UiActions.Row(UiActions.Labeled("服务地址", host), UiActions.Labeled("端口", port)));
        connectionFields.Children.Add(UiActions.Row(UiActions.Labeled("昵称", nick), UiActions.Labeled("房间码", code)));
        connectionFields.Children.Add(UiActions.Labeled("SHA-256 服务指纹", pin));
        top.Children.Add(new Expander { Header = "连接设置", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, Content = connectionFields });
        top.Children.Add(connect); top.Children.Add(state);
        var bottom = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { compose, send } };
        var root = new Grid { Margin = new Thickness(8), MinWidth = 240, RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        transcript.MinHeight = 80;
        root.Children.Add(top); Grid.SetRow(transcript, 1); root.Children.Add(transcript); Grid.SetRow(bottom, 2); root.Children.Add(bottom);
        // Keep all controls reachable when the window is smaller than its content.
        var viewport = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = root
        };
        viewport.SizeChanged += (_, _) => root.MinHeight = Math.Max(0, viewport.Bounds.Height - 16);
        Content = viewport;
        connect.Click += async (_, _) => { if (wanted) Leave(); else { wanted = true; await Join(); } };
        send.Click += async (_, _) => await Send();
        paste.Click += async (_, _) =>
        {
            try
            {
                var clipboard = Clipboard;
                if (clipboard == null) throw new InvalidOperationException("剪贴板不可用。");
                string? text = await clipboard.TryGetTextAsync();
                if (closed || wanted) return;
                var info = ConnectionInfo.Parse(text ?? ""); host.Text = info.Host; port.Text = info.Port.ToString(); code.Text = info.Code; pin.Text = info.Fingerprint;
                state.Text = "连接信息已填入，填写昵称后连接。";
            }
            catch (Exception ex) { state.Text = ex.Message; }
        };
        compose.KeyDown += async (_, e) => { if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; await Send(); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        timer.Tick += async (_, _) =>
        {
            foreach (var packet in inbox.Drain()) Receive(packet);
            UpdateControls();
            if (wanted && !joining && connection?.Connected != true && DateTime.UtcNow >= retryAt) await Join();
        };
        Closed += (_, _) => { closed = true; timer.Stop(); Leave(); code.Clear(); pin.Clear(); };
        timer.Start(); UpdateControls();
    }
    private async Task Join()
    {
        if (closed || joining || !wanted) return;
        ConnectionInfo info;
        string nickname = (nick.Text ?? "").Trim();
        try
        {
            info = new ConnectionInfo((host.Text ?? "").Trim(), int.TryParse(port.Text, out int p) ? p : 0, (code.Text ?? "").Trim(), (pin.Text ?? "").Trim()); info.Validate();
            if (nickname.Length is < 1 or > 20 || nickname.Any(char.IsControl)) throw new ArgumentException("昵称需要 1～20 个字符。");
        }
        catch (Exception ex) { wanted = false; state.Text = ex.Message; UpdateControls(); return; }
        joining = true; int current = ++generation; int inboxGeneration = inbox.Reset(); connection?.Dispose();
        var candidate = new ChatConnection(); connection = candidate;
        candidate.Received += packet => { if (!inbox.Enqueue(inboxGeneration, packet)) candidate.Dispose(); };
        candidate.Dropped += message => inbox.Enqueue(inboxGeneration, new ChatPacket { Kind = "disconnected", Text = message });
        state.Text = "正在验证服务身份并连接…"; UpdateControls();
        try
        {
            await candidate.ConnectAsync(info.Host, info.Port, nickname, info.Code, info.Fingerprint);
            if (closed || current != generation || !wanted) { candidate.Dispose(); return; }
            store.Pref.ChatHost = info.Host; store.Pref.ChatPort = info.Port; store.Pref.ChatNick = nickname; save();
        }
        catch (Exception ex)
        {
            if (closed || current != generation) return;
            if (ex is ChatJoinException or AuthenticationException) wanted = false;
            state.Text = ex is AuthenticationException ? "服务身份验证失败，请重新获取连接信息。" : ex.Message + (wanted ? " · 5 秒后重试" : "");
        }
        finally { joining = false; retryAt = DateTime.UtcNow.AddSeconds(5); if (!closed) UpdateControls(); }
    }
    private void Receive(ChatPacket packet)
    {
        switch (packet.Kind)
        {
            case "welcome":
                if (session != packet.Session) { lines.Clear(); transcript.Clear(); session = packet.Session; lastId = 0; }
                state.Text = $"已加密连接 · 在线 {packet.Online} 人"; return;
            case "state": state.Text = $"已加密连接 · 在线 {packet.Online} 人"; Append("· " + packet.Text); return;
            case "error": Append("· " + packet.Text); return;
            case "overflow": state.Text = "消息处理拥堵，5 秒后重新获取最近消息。"; retryAt = DateTime.UtcNow.AddSeconds(5); return;
            case "disconnected": state.Text = packet.Text; retryAt = DateTime.UtcNow.AddSeconds(5); return;
        }
        if (packet.ContentType != "text" || packet.Kind != "message" || packet.Session != session || packet.Id <= lastId) return;
        lastId = packet.Id;
        string time = DateTime.TryParse(packet.Time, out var timestamp) ? timestamp.ToLocalTime().ToString("HH:mm:ss") : "";
        Append($"{packet.Nick}  {time}\n{packet.Text}");
    }
    private void Append(string text)
    {
        lines.Enqueue(text); while (lines.Count > 150) lines.Dequeue();
        transcript.Text = string.Join("\n\n", lines); transcript.CaretIndex = transcript.Text.Length;
    }
    private async Task Send()
    {
        if (closed || sending || connection?.Connected != true || string.IsNullOrWhiteSpace(compose.Text)) return;
        var candidate = connection; int current = generation; string text = compose.Text;
        sending = true; UpdateControls();
        try { await candidate.SendAsync(text); if (!closed && current == generation && compose.Text == text) compose.Clear(); }
        catch (Exception ex) { if (!closed && current == generation) state.Text = "发送失败，草稿已保留：" + ex.Message; }
        finally { sending = false; if (!closed) UpdateControls(); }
    }
    private void Leave()
    {
        wanted = false; generation++; inbox.Reset(); connection?.Dispose(); connection = null;
        lines.Clear(); transcript.Clear(); compose.Clear(); session = ""; lastId = 0; state.Text = "已离开会话"; UpdateControls();
    }
    private void UpdateControls()
    {
        connect.Content = wanted ? "离开" : "连接";
        host.IsEnabled = port.IsEnabled = nick.IsEnabled = code.IsEnabled = pin.IsEnabled = paste.IsEnabled = !wanted;
        send.IsEnabled = !sending && connection?.Connected == true;
    }
}

internal static class UiActions
{
    public static StackPanel Labeled(string label, Control control) => new() { Spacing = 3, Children = { new TextBlock { Text = label, FontSize = 12 }, control } };
    public static Button Button(string title, Action action)
    {
        var button = new Button { Content = title }; button.Click += (_, _) => action(); return button;
    }
    public static Grid Row(Control left, Control right)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") }; row.Children.Add(left); Grid.SetColumn(right, 2); row.Children.Add(right); return row;
    }
}
