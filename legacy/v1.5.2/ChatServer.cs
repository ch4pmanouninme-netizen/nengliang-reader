using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OneLineReader
{
    internal sealed class ServerGuest : IDisposable
    {
        public readonly ChatPeer Peer;
        private readonly BlockingCollection<ChatPacket> outbound;
        private int closed;
        public ServerGuest(TcpClient client, int capacity)
        {
            outbound = new BlockingCollection<ChatPacket>(capacity);
            Peer = new ChatPeer(client);
            Task.Run(() =>
            {
                try { foreach (ChatPacket packet in outbound.GetConsumingEnumerable()) Peer.Send(packet); }
                catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
                finally { Dispose(); ChatPacket discarded; while (outbound.TryTake(out discarded)) { } outbound.Dispose(); }
            });
        }
        public void Queue(ChatPacket packet)
        {
            if (Volatile.Read(ref closed) != 0) return;
            try { if (!outbound.TryAdd(packet)) Dispose(); }
            catch (InvalidOperationException) { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            outbound.CompleteAdding(); Peer.Dispose();
        }
    }

    internal sealed class ChatServer : IDisposable
    {
        private readonly object gate = new object();
        private readonly List<ServerGuest> guests = new List<ServerGuest>();
        private readonly HashSet<ServerGuest> pending = new HashSet<ServerGuest>();
        private readonly Queue<ChatPacket> history = new Queue<ChatPacket>();
        private readonly Dictionary<string, Queue<DateTime>> attempts = new Dictionary<string, Queue<DateTime>>();
        private readonly byte[] codeHash;
        private readonly ChatOptions options;
        private readonly X509Certificate2 certificate;
        private readonly bool ownsCertificate;
        public string Fingerprint { get { return ChatSecurity.Fingerprint(certificate); } }
        internal int HistoryCount { get { lock (gate) return history.Count; } }
        private readonly string session = Guid.NewGuid().ToString("N");
        private TcpListener listener;
        private volatile bool running;
        private int online;
        private long sequence;
        private int disposed;
        public int Online { get { return Volatile.Read(ref online); } }
        public int Port { get; private set; }
        public ChatServer(string code, ChatOptions options = null, X509Certificate2 certificate = null)
        {
            code = (code ?? "").Trim();
            if (code.Length < 6 || code.Length > 64 || code.Any(Char.IsControl)) throw new ArgumentException("房间码需要 6～64 个字符。");
            this.options = options ?? new ChatOptions();
            this.certificate = certificate ?? ChatSecurity.CreateCertificate(); ownsCertificate = certificate == null;
            using (SHA256 hash = SHA256.Create()) codeHash = hash.ComputeHash(Encoding.UTF8.GetBytes(code));
        }
        public void Start(IPAddress address, int port)
        {
            if (Volatile.Read(ref disposed) != 0 || listener != null) throw new InvalidOperationException("请新建服务后启动。");
            listener = new TcpListener(address, port); listener.Start(32); Port = ((IPEndPoint)listener.LocalEndpoint).Port; running = true;
            Task.Run(() =>
            {
                try
                {
                    while (running)
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        lock (gate)
                        {
                            if (!running || pending.Count >= options.PendingLimit) { client.Close(); continue; }
                            string ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                            DateTime now = DateTime.UtcNow;
                            foreach (string old in attempts.Where(p => p.Value.Count == 0 || (now - p.Value.Last()).TotalMinutes > 1).Select(p => p.Key).ToArray()) attempts.Remove(old);
                            Queue<DateTime> recent;
                            if (!attempts.TryGetValue(ip, out recent)) { if (attempts.Count >= 256) { client.Close(); continue; } recent = new Queue<DateTime>(); attempts[ip] = recent; }
                            while (recent.Count > 0 && (now - recent.Peek()).TotalSeconds > 60) recent.Dequeue();
                            if (recent.Count >= 12) { client.Close(); continue; } recent.Enqueue(now);
                            ServerGuest guest = new ServerGuest(client, options.OutboundLimit); pending.Add(guest); Task.Run(() => Serve(guest));
                        }
                    }
                }
                catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
            });
        }
        private bool CorrectCode(string code)
        {
            byte[] supplied; using (SHA256 hash = SHA256.Create()) supplied = hash.ComputeHash(Encoding.UTF8.GetBytes((code ?? "").Trim()));
            int different = 0; for (int i = 0; i < supplied.Length; i++) different |= supplied[i] ^ codeHash[i]; return different == 0;
        }
        private void Serve(ServerGuest guest)
        {
            bool joined = false;
            try
            {
                guest.Peer.Client.ReceiveTimeout = 10000;
                guest.Peer.SecureAsServer(certificate);
                ChatPacket join = ChatWire.Read(guest.Peer.Stream);
                if (join.Kind != "join" || !CorrectCode(join.Code)) { guest.Peer.Send(new ChatPacket { Kind = "reject", Text = "房间码不正确。" }); return; }
                string nick = ChatWire.Clean(join.Nick);
                if (nick.Length == 0 || nick.Length > 20 || nick.Contains("\n")) { guest.Peer.Send(new ChatPacket { Kind = "reject", Text = "昵称需要 1～20 个字符。" }); return; }
                lock (gate)
                {
                    if (!running) return;
                    if (guests.Count >= options.ParticipantLimit) { guest.Peer.Send(new ChatPacket { Kind = "reject", Text = "房间已满，最多 " + options.ParticipantLimit + " 人。" }); return; }
                    if (guests.Any(g => String.Equals(g.Peer.Nick, nick, StringComparison.OrdinalIgnoreCase))) { guest.Peer.Send(new ChatPacket { Kind = "reject", Text = "这个昵称正在使用，请换一个。" }); return; }
                    guest.Peer.Nick = nick; guests.Add(guest); joined = true; Volatile.Write(ref online, guests.Count);
                    guest.Queue(new ChatPacket { Kind = "welcome", Session = session, Online = guests.Count });
                    foreach (ChatPacket item in history) guest.Queue(item);
                    Broadcast(new ChatPacket { Kind = "state", Online = guests.Count, Text = nick + " 加入了会话" });
                }
                guest.Peer.Client.ReceiveTimeout = 45000;
                Queue<DateTime> sent = new Queue<DateTime>();
                while (running)
                {
                    ChatPacket packet = ChatWire.Read(guest.Peer.Stream);
                    if (packet.Kind == "ping") { guest.Queue(new ChatPacket { Kind = "pong" }); continue; }
                    if (packet.ContentType != "text") { guest.Queue(new ChatPacket { Kind = "error", Text = "暂不支持这种消息类型，请升级客户端。" }); continue; }
                    if (packet.Kind != "send") throw new System.IO.InvalidDataException("无效的客户端消息。");
                    string body = ChatWire.Clean(packet.Text);
                    if (body.Length == 0 || body.Length > options.MessageLength) { guest.Queue(new ChatPacket { Kind = "error", Text = "消息需要 1～" + options.MessageLength + " 个字符。" }); continue; }
                    DateTime now = DateTime.UtcNow;
                    while (sent.Count > 0 && (now - sent.Peek()).TotalSeconds >= 10) sent.Dequeue();
                    if (sent.Count >= options.SendBurst) { guest.Queue(new ChatPacket { Kind = "error", Text = "发送太快，请稍等几秒。" }); continue; } sent.Enqueue(now);
                    lock (gate)
                    {
                        ChatPacket message = new ChatPacket { Kind = "message", Id = ++sequence, Session = session, Nick = nick, Text = body, Time = now.ToString("o") };
                        history.Enqueue(message); while (history.Count > options.HistoryLimit) history.Dequeue(); Broadcast(message);
                    }
                }
            }
            catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
            finally
            {
                guest.Dispose();
                lock (gate)
                {
                    pending.Remove(guest);
                    if (joined) { guests.Remove(guest); Volatile.Write(ref online, guests.Count); if (running) Broadcast(new ChatPacket { Kind = "state", Online = guests.Count, Text = guest.Peer.Nick + " 离开了会话" }); }
                }
            }
        }
        private void Broadcast(ChatPacket packet) { foreach (ServerGuest guest in guests) guest.Queue(packet); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            running = false; if (listener != null) listener.Stop();
            lock (gate)
            {
                foreach (ServerGuest guest in pending) guest.Dispose();
                pending.Clear(); guests.Clear(); history.Clear(); attempts.Clear(); Array.Clear(codeHash, 0, codeHash.Length); Volatile.Write(ref online, 0);
            }
            if (ownsCertificate) certificate.Dispose();
        }
    }

    internal sealed class ChatServerForm : Form
    {
        private readonly NumericUpDown port = new NumericUpDown { Name = "ServerPort", Minimum = 1024, Maximum = 65535, Value = 38417 };
        private readonly TextBox code = new TextBox { Name = "ServerCode", MaxLength = 64 };
        private readonly ComboBox address = new ComboBox { Name = "ServerAddress", DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label state = new Label();
        private readonly Button start = new Button();
        private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
        private readonly X509Certificate2 certificate = ChatSecurity.CreateCertificate();
        private readonly IPAddress bindAddress;
        private readonly string appearancePath;
        private bool dark;
        private ChatServer server;
        public ChatServerForm() : this(IPAddress.Any, SavedAppearance.DefaultPath) { }
        internal ChatServerForm(IPAddress bindAddress, string appearancePath = null)
        {
            this.bindAddress = bindAddress; this.appearancePath = appearancePath ?? SavedAppearance.DefaultPath;
            Text = "良子小窗 · 聊天服务"; ClientSize = new Size(520, 406); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; Font = Ui.Font(10); AutoScaleMode = AutoScaleMode.None;
            Controls.Add(Label("在一台电脑上启动，大家连接同一个房间。", 24, 20, 472, 30));
            Controls.Add(Label("本机地址", 24, 72, 100, 28)); address.SetBounds(140, 72, 350, 30); Controls.Add(address);
            string[] ips = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.Description.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) < 0 && n.Description.IndexOf("tunnel", StringComparison.OrdinalIgnoreCase) < 0 ? 0 : 1)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.")).Select(a => a.Address.ToString()).Distinct().ToArray();
            address.Items.AddRange(ips.Length > 0 ? ips : new[] { "127.0.0.1" }); address.SelectedIndex = 0;
            Controls.Add(Label("端口", 24, 118, 100, 28)); port.SetBounds(140, 118, 110, 28); Controls.Add(port);
            Controls.Add(Label("房间码", 24, 164, 100, 28)); code.SetBounds(140, 164, 350, 28); Controls.Add(code);
            byte[] random = new byte[6]; using (RandomNumberGenerator generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
            code.Text = BitConverter.ToString(random).Replace("-", "");
            Controls.Add(Label("服务指纹", 24, 210, 100, 28));
            TextBox fingerprint = new TextBox { Name = "ServerFingerprint", ReadOnly = true, Text = ChatSecurity.Fingerprint(certificate), Bounds = new Rectangle(140, 210, 350, 28) }; Controls.Add(fingerprint);
            state.SetBounds(24, 252, 472, 30); state.Text = "服务未启动 · 加密连接"; Controls.Add(state);
            Controls.Add(Label("复制完整连接信息给同事，服务电脑需要保持开机。", 24, 289, 472, 32));
            start.Name = "ServerStart"; start.SetBounds(140, 344, 150, 36); start.Text = "启动服务"; Controls.Add(start); start.Click += (s, e) => Toggle();
            Button copy = new Button { Text = "复制连接信息", Bounds = new Rectangle(310, 344, 180, 36) }; Controls.Add(copy);
            copy.Click += (s, e) => { try { Clipboard.SetText(ConnectionInfo()); } catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "剪贴板正忙，请重试。", Text); } };
            refresh.Interval = 500; refresh.Tick += (s, e) => { RefreshAppearance(); if (server != null) state.Text = "运行中 · 在线 " + server.Online + " / 30 人 · TLS 加密"; }; refresh.Start();
            Ui.QuietHeader(this, Text, () => WindowState = FormWindowState.Minimized, Close);
            dark = SavedAppearance.Read(this.appearancePath, false); Ui.Skin(this, dark);
            HandleCreated += (s, e) => ThemeNative.Caption(Handle, dark);
            FormClosing += (s, e) => { if (server != null && MessageBox.Show(this, "关闭服务会断开所有人的聊天连接并清空服务端消息。确定关闭？", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) e.Cancel = true; };
        }
        internal string ConnectionInfo() { return "良子小窗 1.5\r\n服务地址：" + address.Text + "\r\n端口：" + port.Value + "\r\n房间码：" + code.Text.Trim() + "\r\n服务指纹：" + ChatSecurity.Fingerprint(certificate); }
        internal void RefreshAppearance()
        {
            bool next = SavedAppearance.Read(appearancePath, dark);
            if (next != dark) { dark = next; Ui.Skin(this, dark); Invalidate(true); }
        }
        private static Label Label(string text, int x, int y, int w, int h) { return new Label { Text = text, Bounds = new Rectangle(x, y, w, h), TextAlign = ContentAlignment.MiddleLeft }; }
        private void Toggle()
        {
            if (server != null) { server.Dispose(); server = null; start.Text = "启动服务"; state.Text = "服务已停止 · 消息已清空"; code.Enabled = port.Enabled = true; return; }
            try { server = new ChatServer(code.Text, null, certificate); server.Start(bindAddress, (int)port.Value); start.Text = "停止服务"; code.Enabled = port.Enabled = false; state.Text = "运行中 · 在线 0 / 30 人 · TLS 加密"; }
            catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; if (server != null) server.Dispose(); server = null; MessageBox.Show(this, "启动失败，请检查房间码和端口。\r\n" + ex.Message, Text); }
        }
        protected override void Dispose(bool disposing) { if (disposing) { refresh.Dispose(); if (server != null) server.Dispose(); certificate.Dispose(); } base.Dispose(disposing); }
    }

    internal static class ChatServerProgram
    {
        [STAThread] public static void Main() { Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new ChatServerForm()); }
    }
}

