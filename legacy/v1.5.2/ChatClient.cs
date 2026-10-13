using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OneLineReader
{
    internal sealed class ChatJoinException : IOException { public ChatJoinException(string message) : base(message) { } }

    internal sealed class ChatTranscript : Control
    {
        private int contentHeight, offset;
        public ChatTranscript() { DoubleBuffered = true; TabStop = true; AccessibleName = "会话正文"; }
        private int ScrollLimit { get { return Math.Max(0, contentHeight - ClientSize.Height); } }
        private void MeasureContent(bool follow)
        {
            contentHeight = String.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font, new Size(Math.Max(1, ClientSize.Width - 12), Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl).Height;
            offset = follow ? ScrollLimit : Math.Min(offset, ScrollLimit); Invalidate();
        }
        protected override void OnTextChanged(EventArgs e) { bool follow = offset >= ScrollLimit - 4; base.OnTextChanged(e); MeasureContent(follow); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); MeasureContent(true); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); MeasureContent(true); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { offset = Store.Clamp(offset - e.Delta / 120 * Font.Height * 3, 0, ScrollLimit); Invalidate(); base.OnMouseWheel(e); }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Home || keyData == Keys.End || keyData == Keys.PageUp || keyData == Keys.PageDown || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Home) offset = 0;
            else if (e.KeyCode == Keys.End) offset = ScrollLimit;
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.PageUp) offset -= e.KeyCode == Keys.Up ? Font.Height : Height;
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.PageDown) offset += e.KeyCode == Keys.Down ? Font.Height : Height;
            else { base.OnKeyDown(e); return; }
            offset = Store.Clamp(offset, 0, ScrollLimit); e.Handled = true; Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, -offset, Math.Max(1, Width - 12), Math.Max(contentHeight, Height)), ForeColor,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl | TextFormatFlags.PreserveGraphicsClipping);
            if (ScrollLimit > 0)
            {
                int thumb = Math.Max(18, Height * Height / Math.Max(1, contentHeight));
                using (Brush brush = new SolidBrush(Color.FromArgb(130, 135, 140))) e.Graphics.FillRectangle(brush, Width - 4, (Height - thumb) * offset / ScrollLimit, 2, thumb);
            }
        }
    }

    internal sealed class ChatConnection : IDisposable
    {
        private readonly TcpClient client = new TcpClient();
        private readonly ChatOptions options;
        private ChatPeer peer;
        private System.Threading.Timer heartbeat;
        private int stopped;
        private int droppedOnce;
        private volatile bool connected;
        public bool Connected { get { return connected; } }
        internal bool Encrypted { get { var tls = peer == null ? null : peer.Stream as System.Net.Security.SslStream; return tls != null && tls.IsAuthenticated && tls.IsEncrypted; } }
        public event Action<ChatPacket> Received;
        public event Action<string> Dropped;
        public ChatConnection(ChatOptions options = null) { this.options = options ?? new ChatOptions(); }
        public Task ConnectAsync(string host, int port, string nick, string code, string fingerprint)
        {
            return Task.Run(() =>
            {
                try
                {
                    IAsyncResult pending = client.BeginConnect(host, port, null, null);
                    using (pending.AsyncWaitHandle)
                    {
                        if (!pending.AsyncWaitHandle.WaitOne(5000)) throw new TimeoutException("连接超时，请检查地址、端口和服务电脑。");
                        client.EndConnect(pending);
                    }
                    if (Volatile.Read(ref stopped) != 0) throw new ObjectDisposedException("聊天连接");
                    peer = new ChatPeer(client); client.ReceiveTimeout = 10000;
                    peer.SecureAsClient(fingerprint);
                    peer.Send(new ChatPacket { Kind = "join", Nick = nick, Code = code });
                    ChatPacket welcome = ChatWire.Read(peer.Stream);
                    if (welcome.Kind == "reject") throw new ChatJoinException(welcome.Text);
                    if (welcome.Kind != "welcome") throw new InvalidDataException("服务端响应不正确。");
                    client.ReceiveTimeout = 45000; connected = true;
                    if (Volatile.Read(ref stopped) != 0) throw new ObjectDisposedException("聊天连接");
                    Action<ChatPacket> received = Received; if (received != null) received(welcome);
                    heartbeat = new System.Threading.Timer(s =>
                    {
                        try { if (Connected) peer.Send(new ChatPacket { Kind = "ping" }); }
                        catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; Drop("连接已中断"); }
                    }, null, 15000, 15000);
                    if (Volatile.Read(ref stopped) != 0) heartbeat.Dispose();
                    Task.Run(() => ReceiveLoop());
                }
                catch { Dispose(); throw; }
            });
        }
        private void ReceiveLoop()
        {
            try
            {
                while (Connected)
                {
                    ChatPacket packet = ChatWire.Read(peer.Stream);
                    if (packet.Kind == "pong") continue;
                    Action<ChatPacket> received = Received; if (received != null) received(packet);
                }
            }
            catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; Drop("连接已中断，等待重连"); }
        }
        public Task SendAsync(string text)
        {
            return Task.Run(() =>
            {
                if (!Connected) throw new IOException("尚未连接到会话。");
                string body = ChatWire.Clean(text);
                if (body.Length == 0 || body.Length > options.MessageLength) throw new ArgumentException("消息需要 1～" + options.MessageLength + " 个字符。");
                try { peer.Send(new ChatPacket { Kind = "send", Text = body }); }
                catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; Drop("发送失败，连接已中断"); throw; }
            });
        }
        private void Drop(string message)
        {
            bool notify = Connected && Interlocked.Exchange(ref droppedOnce, 1) == 0; Dispose();
            Action<string> dropped = Dropped; if (notify && dropped != null) dropped(message);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            connected = false; if (heartbeat != null) heartbeat.Dispose(); client.Close(); if (peer != null) peer.Dispose();
        }
    }

    internal sealed class ChatForm : Form
    {
        private readonly Store store;
        private readonly Action save, showHome;
        private readonly ChatOptions options;
        private readonly TextBox fingerprint = new TextBox { Name = "ChatFingerprint", MaxLength = 64 };
        private readonly ConcurrentQueue<KeyValuePair<int, ChatPacket>> inbox = new ConcurrentQueue<KeyValuePair<int, ChatPacket>>();
        private readonly System.Windows.Forms.Timer receiveTimer = new System.Windows.Forms.Timer();
        private readonly object inboxGate = new object();
        internal int CachedCount { get { return lines.Count; } }
        private readonly TextBox host = new TextBox { Name = "ChatHost", MaxLength = 253 };
        private readonly NumericUpDown port = new NumericUpDown { Name = "ChatPort", Minimum = 1, Maximum = 65535 };
        private readonly TextBox nick = new TextBox { Name = "ChatNick", MaxLength = 20 };
        private readonly TextBox code = new TextBox { Name = "ChatCode", MaxLength = 64, UseSystemPasswordChar = true };
        private readonly ChatTranscript transcript = new ChatTranscript { Name = "Transcript", Font = Ui.Font(10) };
        private readonly TextBox compose = new TextBox { Name = "Compose", Multiline = true, MaxLength = 500, ScrollBars = ScrollBars.Vertical };
        private readonly Button connect, send;
        private readonly Label state;
        private readonly System.Windows.Forms.Timer retry = new System.Windows.Forms.Timer();
        private readonly Queue<string> lines = new Queue<string>();
        private ChatConnection connection;
        private string session = "";
        private long lastId;
        private bool wanted, joining, sending;
        private int generation;
        public ChatForm(Store store, Action save, Action showHome, ChatOptions options = null)
        {
            this.store = store; this.save = save; this.showHome = showHome; this.options = options ?? new ChatOptions();
            Text = "会话"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 528); AutoScaleMode = AutoScaleMode.None; Font = Ui.Font(9); DoubleBuffered = true;
            Panel header = new Panel { Bounds = new Rectangle(1, 1, 438, 34), Name = "header" }; Controls.Add(header);
            Label caption = Ui.Label("会话", 12, 5, 100, 24, 9, Ui.Ink); header.Controls.Add(caption);
            MouseEventHandler drag = (s, e) => { if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } }; header.MouseDown += drag; caption.MouseDown += drag;
            Button home = Ui.Button("主页", 310, 4, 56, 25, false); home.Name = "Home"; header.Controls.Add(home); home.Click += (s, e) => showHome();
            Button hide = Ui.Button("—", 374, 4, 26, 25, false); header.Controls.Add(hide); hide.Click += (s, e) => Hide();
            Button close = Ui.Button("×", 404, 4, 26, 25, false); header.Controls.Add(close); close.Name = "CloseChat"; close.Click += (s, e) => Close();
            Controls.Add(Ui.Label("地址", 14, 48, 40, 24, 9, Ui.Muted)); host.SetBounds(57, 48, 240, 25); Controls.Add(host); host.Text = store.Pref.ChatHost;
            Controls.Add(Ui.Label("端口", 308, 48, 40, 24, 9, Ui.Muted)); port.SetBounds(350, 48, 76, 25); Controls.Add(port); port.Value = store.Pref.ChatPort;
            Controls.Add(Ui.Label("昵称", 14, 85, 40, 24, 9, Ui.Muted)); nick.SetBounds(57, 85, 120, 25); Controls.Add(nick); nick.Text = store.Pref.ChatNick;
            Controls.Add(Ui.Label("房间码", 188, 85, 52, 24, 9, Ui.Muted)); code.SetBounds(241, 85, 101, 25); Controls.Add(code);
            connect = Ui.Button("连接", 352, 84, 74, 27, false); connect.Name = "Connect"; Controls.Add(connect); connect.Click += async (s, e) => { if (wanted) LeaveRoom(); else { wanted = true; await Join(); } };
            Controls.Add(Ui.Label("指纹", 14, 121, 40, 24, 9, Ui.Muted)); fingerprint.SetBounds(57, 121, 253, 25); Controls.Add(fingerprint);
            Button paste = Ui.Button("粘贴连接信息", 320, 120, 106, 27, false); paste.Name = "PasteConnection"; Controls.Add(paste);
            paste.Click += (s, e) => { if (!wanted) { try { ApplyConnectionInfo(Clipboard.GetText()); } catch (System.Runtime.InteropServices.ExternalException) { state.Text = "剪贴板正忙，请重试"; } } };
            state = Ui.Label("未连接 · 填写服务电脑的地址和房间码", 14, 161, 412, 24, 8, Ui.Muted); state.Name = "ChatState"; Controls.Add(state);
            transcript.SetBounds(14, 194, 412, 225); Controls.Add(transcript);
            compose.SetBounds(14, 431, 322, 54); Controls.Add(compose);
            send = Ui.Button("发送", 348, 431, 78, 54, false); send.Name = "Send"; send.Enabled = false; Controls.Add(send); send.Click += async (s, e) => await Send();
            compose.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await Send(); } };
            Controls.Add(Ui.Label("Enter 发送 · Shift+Enter 换行 · Esc 隐藏", 14, 496, 412, 20, 8, Ui.Muted));
            retry.Interval = 5000; retry.Tick += async (s, e) => { if (wanted && !joining && (connection == null || !connection.Connected)) await Join(); }; retry.Start();
            receiveTimer.Interval = 100; receiveTimer.Tick += (s, e) => DrainInbox(); receiveTimer.Start();
            ApplyTheme();
        }
        internal bool ApplyConnectionInfo(string text)
        {
            if (wanted) return false;
            Dictionary<string, string> fields = new Dictionary<string, string>();
            foreach (string line in (text ?? "").Split('\n')) { int colon = line.IndexOf('：'); if (colon > 0) fields[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim(); }
            string address, secret, pin, number; int parsed;
            if (!fields.TryGetValue("服务地址", out address) || address.Length == 0 || address.Length > host.MaxLength || !fields.TryGetValue("端口", out number)
                || !Int32.TryParse(number, out parsed) || parsed < 1 || parsed > 65535 || !fields.TryGetValue("房间码", out secret) || secret.Length < 6 || secret.Length > 64
                || !fields.TryGetValue("服务指纹", out pin) || !ChatSecurity.ValidFingerprint(pin)) { state.Text = "请粘贴完整连接信息（含地址、端口、房间码和指纹）"; return false; }
            host.Text = address; port.Value = parsed; code.Text = secret; fingerprint.Text = pin; state.Text = "连接信息已填入，填写昵称后连接"; return true;
        }
        private void Enqueue(int current, ChatPacket packet)
        {
            lock (inboxGate)
            {
                if (IsDisposed || Disposing || current != generation) return;
                if (inbox.Count >= options.ClientHistoryLimit + 32)
                {
                    // A blocked UI must not accumulate unbounded callbacks; disconnect and replay bounded room history.
                    if (connection != null) connection.Dispose();
                    KeyValuePair<int, ChatPacket> discarded; while (inbox.TryDequeue(out discarded)) { }
                    inbox.Enqueue(new KeyValuePair<int, ChatPacket>(current, new ChatPacket { Kind = "overflow" })); return;
                }
                inbox.Enqueue(new KeyValuePair<int, ChatPacket>(current, packet));
            }
        }
        private void DrainInbox()
        {
            KeyValuePair<int, ChatPacket> item;
            for (int i = 0; i < 64 && inbox.TryDequeue(out item); i++) if (item.Key == generation) Receive(item.Value);
            UpdateControls();
        }
        public void ApplyTheme() { Ui.Skin(this, store.Pref.UiDark); Invalidate(); }
        private void OnUi(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed && !Disposing) action(); })); }
            catch (InvalidOperationException) { }
        }
        private async Task Join()
        {
            if (joining || !wanted) return;
            string serverHost = host.Text.Trim(), nickname = nick.Text.Trim(), roomCode = code.Text.Trim();
            if (serverHost.Length == 0 || nickname.Length == 0 || roomCode.Length < 6 || !ChatSecurity.ValidFingerprint(fingerprint.Text.Trim())) { state.Text = "请填写地址、昵称、房间码和完整服务指纹"; wanted = false; UpdateControls(); return; }
            joining = true; int current = ++generation;
            if (connection != null) connection.Dispose();
            ChatConnection candidate = new ChatConnection(options); connection = candidate;
            candidate.Received += packet => Enqueue(current, packet);
            candidate.Dropped += message => OnUi(() => { if (generation == current && wanted) { state.Text = message; UpdateControls(); } });
            state.Text = "正在连接…"; UpdateControls();
            try
            {
                await candidate.ConnectAsync(serverHost, (int)port.Value, nickname, roomCode, fingerprint.Text.Trim());
                if (IsDisposed || generation != current || !wanted) { candidate.Dispose(); return; }
                store.Pref.ChatHost = serverHost; store.Pref.ChatPort = (int)port.Value; store.Pref.ChatNick = nickname; save();
            }
            catch (Exception ex)
            {
                if (!ChatWire.Expected(ex)) throw;
                if (!IsDisposed && generation == current) { if (ex is ChatJoinException || ex is AuthenticationException) wanted = false; state.Text = ex is AuthenticationException ? "服务身份验证失败，请重新获取服务电脑的连接信息" : ex is ChatJoinException ? ex.Message : "未连通，请检查地址和服务；5 秒后重试"; }
            }
            finally { if (!IsDisposed) { joining = false; UpdateControls(); } }
        }
        private void Receive(ChatPacket packet)
        {
            if (packet.Kind == "overflow") { state.Text = "界面处理拥堵，正在重新连接获取最近消息"; return; }
            if (packet.Kind == "welcome")
            {
                if (session != packet.Session) { lines.Clear(); transcript.Text = ""; session = packet.Session; lastId = 0; }
                state.Text = "已加密连接 · 在线 " + packet.Online + " 人"; return;
            }
            if (packet.Kind == "state") { state.Text = "已加密连接 · 在线 " + packet.Online + " 人"; Append("· " + packet.Text); return; }
            if (packet.Kind == "error") { Append("· " + packet.Text); return; }
            if (packet.ContentType != "text") { Append("· 当前版本暂不支持此消息类型"); return; }
            if (packet.Kind != "message" || packet.Session != session || packet.Id <= lastId) return;
            lastId = packet.Id; DateTime time; string stamp = DateTime.TryParse(packet.Time, out time) ? time.ToLocalTime().ToString("HH:mm:ss") : "";
            Append(packet.Nick + "  " + stamp + "\r\n" + packet.Text.Replace("\n", "\r\n"));
        }
        private void Append(string text)
        {
            lines.Enqueue(text); while (lines.Count > options.ClientHistoryLimit) lines.Dequeue();
            transcript.Text = String.Join("\r\n\r\n", lines);
        }
        private async Task Send()
        {
            if (sending || connection == null || !connection.Connected || String.IsNullOrWhiteSpace(compose.Text)) return;
            sending = true; string text = compose.Text; send.Enabled = false;
            try { await connection.SendAsync(text); if (!IsDisposed && compose.Text == text) compose.Clear(); }
            catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; if (!IsDisposed) state.Text = "发送失败，文字已保留，请连接后重试"; }
            finally { sending = false; if (!IsDisposed) UpdateControls(); }
        }
        private void LeaveRoom()
        {
            wanted = false; generation++; if (connection != null) { connection.Dispose(); connection = null; }
            ClearMessages(); compose.Clear(); state.Text = "已离开会话"; UpdateControls();
        }
        private void ClearMessages()
        {
            lock (inboxGate) { KeyValuePair<int, ChatPacket> discarded; while (inbox.TryDequeue(out discarded)) { } }
            lines.Clear(); transcript.Text = ""; session = ""; lastId = 0;
        }
        private void UpdateControls()
        {
            connect.Text = wanted ? "离开" : "连接"; host.Enabled = nick.Enabled = port.Enabled = code.Enabled = fingerprint.Enabled = !wanted;
            Controls["PasteConnection"].Enabled = !wanted;
            send.Enabled = !sending && connection != null && connection.Connected;
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys key) { if (key == Keys.Escape) { Hide(); return true; } return base.ProcessCmdKey(ref msg, key); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (Pen border = new Pen(store.Pref.UiDark ? Color.FromArgb(54, 57, 61) : Color.FromArgb(217, 222, 228))) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1); }
        protected override void OnFormClosing(FormClosingEventArgs e) { LeaveRoom(); base.OnFormClosing(e); }
        protected override void Dispose(bool disposing) { if (disposing) { wanted = false; generation++; retry.Dispose(); receiveTimer.Dispose(); if (connection != null) connection.Dispose(); ClearMessages(); compose.Clear(); code.Clear(); fingerprint.Clear(); } base.Dispose(disposing); }
    }
}

