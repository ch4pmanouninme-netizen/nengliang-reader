using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Reader.Chat;
using Reader.Core;
using System.Security.Authentication;

namespace Reader.Desktop;

/// <summary>
/// 加密聊天窗口：负责连接设置、加入/离开房间、消息收发与断线重连，并把连接偏好写回设置。
/// </summary>
/// <remarks>
/// 自带的 DispatcherTimer 在 UI 线程轮询收件箱；关闭窗口会停止定时器、断开连接并清除房间码与指纹等敏感输入。
/// </remarks>
internal sealed class ChatWindow : Window
{
    private readonly TextBox host = new() { Watermark = "服务地址", MaxLength = 253 };
    private readonly TextBox port = new() { Text = "38417", Watermark = "端口", MaxLength = 5 };
    private readonly TextBox nick = new() { Watermark = "昵称", MaxLength = 20 };
    private readonly TextBox code = new() { Watermark = "房间码", MaxLength = 64, PasswordChar = '●' };
    private readonly TextBox pin = new() { Watermark = "SHA-256 服务指纹", MaxLength = 64 };
    private readonly TextBox compose = new() { Watermark = "Enter 发送，Shift+Enter 换行", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 500, MinHeight = 70, MaxHeight = 120 };
    private readonly TextBlock state = new() { Text = "未连接", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox transcript = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly Button connect = new() { Content = "连接" }, send = new() { Content = "发送" }, paste = new() { Content = "粘贴连接信息" };
    // UI 线程轮询收件箱；网络回调只入队，不直接改控件。
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ChatInbox inbox = new();
    // 环形消息缓存，只保留最近 150 条。
    private readonly Queue<string> lines = new();
    private readonly Store store;
    private readonly Action save;
    private ChatConnection? connection;
    // wanted 表示用户希望保持连接；joining/sending/closed 用于防重入和关闭后抑制。
    private bool wanted, joining, sending, closed;
    // 批量到达的消息只重建一次文本。
    private bool transcriptDirty;
    // 每次加入/离开递增，用来丢弃旧连接的迟到回包。
    private int generation;
    private DateTime retryAt;
    private string session = "";
    private long lastId;

    /// <summary>创建聊天窗口。</summary>
    /// <param name="store">书库存储，用于读写聊天偏好。</param>
    /// <param name="save">请求保存偏好的回调；由书库窗口统一调度落盘。</param>
    /// <param name="home">返回主页的回调。</param>
    public ChatWindow(Store store, Action save, Action home)
    {
        this.store = store; this.save = save;
        Title = "良子小窗 · 聊天"; Icon = AppIcon.Shared;
        Width = 640; Height = 680; MinWidth = 0; MinHeight = 0; CanResize = true;
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
        // A scroll viewer measures unconstrained axes with infinity. Give the grid
        // a finite viewport so history wraps and scrolls inside its own TextBox.
        // Only very small windows scroll the whole form to keep controls reachable.
        void FitViewport()
        {
            // 根网格必须有有限尺寸，否则 ScrollViewer 的无限测量会让历史文本不换行。
            double width = Math.Max(240, viewport.Bounds.Width - 16);
            double height = Math.Max(top.DesiredSize.Height + bottom.DesiredSize.Height + 80, viewport.Bounds.Height - 16);
            if (root.Width != width) root.Width = width;
            if (root.Height != height) root.Height = height;
        }
        viewport.SizeChanged += (_, _) => FitViewport();
        root.LayoutUpdated += (_, _) => FitViewport();
        Content = viewport;
        // 同一个按钮在“连接/离开”之间切换；wanted 同时驱动自动重连。
        connect.Click += async (_, _) => { if (wanted) Leave(); else { wanted = true; await Join(); } };
        send.Click += async (_, _) => await Send();
        paste.Click += async (_, _) =>
        {
            // 剪贴板内容来自外部，解析失败时只更新状态栏，不影响已有连接。
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
        // Intercept Enter before the multiline TextBox consumes it as a newline.
        compose.AddHandler(InputElement.KeyDownEvent, async (_, e) => { if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; await Send(); } }, RoutingStrategies.Tunnel);
        // Esc 只隐藏窗口，不离开房间；隐藏期间连接和定时器继续保持。
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        timer.Tick += async (_, _) =>
        {
            // 网络回调只入队，这里统一在 UI 线程处理并刷新界面。
            foreach (var packet in inbox.Drain()) Receive(packet);
            // 一次 Drain 里可能重连回放上百条；等全部处理完再重建一次文本。
            FlushTranscript();
            UpdateControls();
            // 连接断开且用户仍希望在线时，按 5 秒间隔重试。
            if (wanted && !joining && connection?.Connected != true && DateTime.UtcNow >= retryAt) await Join();
        };
        // 关闭必须断开连接并清除房间码/指纹，避免敏感信息留在内存中。
        Closed += (_, _) => { closed = true; timer.Stop(); Leave(); code.Clear(); pin.Clear(); };
        timer.Start(); UpdateControls();
    }
    // 建立一次新的连接尝试；失败后由定时器按 retryAt 重试。
    private async Task Join()
    {
        if (closed || joining || !wanted) return;
        ConnectionInfo info;
        string nickname = (nick.Text ?? "").Trim();
        try
        {
            // 先在本地校验地址、房间码和昵称，避免明显无效的配置进入网络层。
            info = new ConnectionInfo((host.Text ?? "").Trim(), int.TryParse(port.Text, out int p) ? p : 0, (code.Text ?? "").Trim(), (pin.Text ?? "").Trim()); info.Validate();
            if (nickname.Length is < 1 or > 20 || nickname.Any(char.IsControl)) throw new ArgumentException("昵称需要 1～20 个字符。");
        }
        catch (Exception ex) { wanted = false; state.Text = ex.Message; UpdateControls(); return; }
        // 每次连接持有自己的代号；旧连接迟到的回调会被丢弃。
        joining = true; int current = ++generation; int inboxGeneration = inbox.Reset(); connection?.Dispose();
        var candidate = new ChatConnection(); connection = candidate;
        // 网络线程只把包放进收件箱；队列满时由 inbox 拒绝入队并释放连接。
        candidate.Received += packet => { if (!inbox.Enqueue(inboxGeneration, packet)) candidate.Dispose(); };
        candidate.Dropped += message => inbox.Enqueue(inboxGeneration, new ChatPacket { Kind = "disconnected", Text = message });
        state.Text = "正在验证服务身份并连接…"; UpdateControls();
        try
        {
            await candidate.ConnectAsync(info.Host, info.Port, nickname, info.Code, info.Fingerprint);
            if (closed || current != generation || !wanted) { candidate.Dispose(); return; }
            // 只有连接成功才保存偏好，避免把错误的地址或房间码写进设置。
            store.Pref.ChatHost = info.Host; store.Pref.ChatPort = info.Port; store.Pref.ChatNick = nickname; save();
        }
        catch (Exception ex)
        {
            if (closed || current != generation) return;
            // 房间不存在或身份验证失败时停止重试；其它错误保留 wanted 以便自动重连。
            if (ex is ChatJoinException or AuthenticationException) wanted = false;
            state.Text = ex is AuthenticationException ? "服务身份验证失败，请重新获取连接信息。" : ex.Message + (wanted ? " · 5 秒后重试" : "");
        }
        finally { joining = false; retryAt = DateTime.UtcNow.AddSeconds(5); if (!closed) UpdateControls(); }
    }
    // 在 UI 线程处理一条已排队的消息包。
    private void Receive(ChatPacket packet)
    {
        switch (packet.Kind)
        {
            case "welcome":
                // 会话变化说明重新加入或换了房间，清空旧历史并按新会话重新累计消息 Id。
                if (session != packet.Session) { lines.Clear(); transcript.Clear(); transcriptDirty = false; session = packet.Session; lastId = 0; }
                state.Text = $"已加密连接 · 在线 {packet.Online} 人"; return;
            case "state": state.Text = $"已加密连接 · 在线 {packet.Online} 人"; Append("· " + packet.Text); return;
            case "error": Append("· " + packet.Text); return;
            case "overflow": state.Text = "消息处理拥堵，5 秒后重新获取最近消息。"; retryAt = DateTime.UtcNow.AddSeconds(5); return;
            case "disconnected": state.Text = packet.Text; retryAt = DateTime.UtcNow.AddSeconds(5); return;
        }
        // 只接受当前会话的文本消息；按 Id 去重，重连回放时不会重复显示。
        if (packet.ContentType != "text" || packet.Kind != "message" || packet.Session != session || packet.Id <= lastId) return;
        lastId = packet.Id;
        string time = DateTime.TryParse(packet.Time, out var timestamp) ? timestamp.ToLocalTime().ToString("HH:mm:ss") : "";
        Append($"{packet.Nick}  {time}\n{packet.Text}");
    }
    // 只保留最近 150 条，长时间聊天不会无限占用内存。
    private void Append(string text)
    {
        lines.Enqueue(text); while (lines.Count > 150) lines.Dequeue();
        transcriptDirty = true;
    }

    // 只有内容变化时才重写只读文本框，并把光标放到末尾跟随新消息。
    private void FlushTranscript()
    {
        if (!transcriptDirty) return;
        transcriptDirty = false;
        string text = string.Join("\n\n", lines);
        transcript.Text = text; transcript.CaretIndex = text.Length;
    }
    // 发送输入框内容；sending 防重入，失败时保留草稿。
    private async Task Send()
    {
        if (closed || sending || connection?.Connected != true || string.IsNullOrWhiteSpace(compose.Text)) return;
        var candidate = connection; int current = generation; string text = compose.Text;
        sending = true; UpdateControls();
        // 发送期间用户可能改了草稿或切换了连接，只有仍匹配时才清空。
        try { await candidate.SendAsync(text); if (!closed && current == generation && compose.Text == text) compose.Clear(); }
        catch (Exception ex) { if (!closed && current == generation) state.Text = "发送失败，草稿已保留：" + ex.Message; }
        finally { sending = false; if (!closed) UpdateControls(); }
    }
    // 离开会话：停止重连、丢弃连接和缓存，并清空消息与草稿。
    private void Leave()
    {
        wanted = false; generation++; inbox.Reset(); connection?.Dispose(); connection = null;
        lines.Clear(); transcript.Clear(); transcriptDirty = false; compose.Clear(); session = ""; lastId = 0; state.Text = "已离开会话"; UpdateControls();
    }
    // 连接期间锁定连接参数，避免中途改配置导致界面状态与实际连接不一致。
    private void UpdateControls()
    {
        connect.Content = wanted ? "离开" : "连接";
        host.IsEnabled = port.IsEnabled = nick.IsEnabled = code.IsEnabled = pin.IsEnabled = paste.IsEnabled = !wanted;
        send.IsEnabled = !sending && connection?.Connected == true;
    }
}

/// <summary>
/// 聊天窗口与服务窗口共用的轻量布局辅助：标签、按钮和两列行。
/// </summary>
internal static class UiActions
{
    /// <summary>创建“小号标签 + 控件”的纵向组合。</summary>
    /// <param name="label">控件上方的说明文字。</param>
    /// <param name="control">被标注的控件。</param>
    /// <returns>纵向排列的面板。</returns>
    public static StackPanel Labeled(string label, Control control) => new() { Spacing = 3, Children = { new TextBlock { Text = label, FontSize = 12 }, control } };
    /// <summary>创建点击后执行动作的按钮。</summary>
    /// <param name="title">按钮文字。</param>
    /// <param name="action">点击回调。</param>
    /// <returns>已绑定事件的按钮。</returns>
    public static Button Button(string title, Action action)
    {
        var button = new Button { Content = title }; button.Click += (_, _) => action(); return button;
    }
    /// <summary>创建左右两列、中间留 12 像素间隔的行。</summary>
    /// <param name="left">左列控件。</param>
    /// <param name="right">右列控件。</param>
    /// <returns>两列网格。</returns>
    public static Grid Row(Control left, Control right)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") }; row.Children.Add(left); Grid.SetColumn(right, 2); row.Children.Add(right); return row;
    }
}
