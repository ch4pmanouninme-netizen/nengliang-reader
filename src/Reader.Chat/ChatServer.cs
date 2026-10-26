// 聊天室服务端：监听 TCP、TLS 握手、房间码与昵称校验、成员管理、历史记录与广播。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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


namespace Reader.Chat
{
    /// <summary>服务端视角的单个接入连接：有界出站队列加专用发送任务。</summary>
    /// <remarks>每个连接一个发送任务，把并发的广播串行化；队列满说明客户端消费过慢，
    /// 此时直接断开而不是阻塞广播线程。</remarks>
    public sealed class ServerGuest : IDisposable
    {
        /// <summary>底层聊天端点；正常广播与历史补发经由 <see cref="Queue"/>，以保持帧顺序。</summary>
        public readonly ChatPeer Peer;
        private readonly BlockingCollection<ChatPacket> outbound;
        private int closed;
        /// <summary>包装已接受的连接并启动发送任务。</summary>
        /// <param name="client">已接受的 TCP 连接。</param>
        /// <param name="capacity">出站队列容量；队列满时连接会被关闭。</param>
        public ServerGuest(TcpClient client, int capacity)
        {
            outbound = new BlockingCollection<ChatPacket>(capacity);
            Peer = new ChatPeer(client);
            // 专用发送任务按入队顺序取包；任一发送失败即关闭连接并丢弃剩余包。
            Task.Run(() =>
            {
                try { foreach (ChatPacket packet in outbound.GetConsumingEnumerable()) Peer.Send(packet); }
                catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
                finally { Dispose(); ChatPacket discarded; while (outbound.TryTake(out discarded)) { } outbound.Dispose(); }
            });
        }
        /// <summary>把数据包放入出站队列；非阻塞。</summary>
        /// <param name="packet">待发送的数据包。</param>
        public void Queue(ChatPacket packet)
        {
            if (Volatile.Read(ref closed) != 0) return;
            // 队列满时不等待：关闭该连接，避免一个慢客户端拖住整个广播循环。
            try { if (!outbound.TryAdd(packet)) Dispose(); }
            // Dispose 与 CompleteAdding 竞争时 TryAdd 会抛 InvalidOperationException，视为已关闭。
            catch (InvalidOperationException) { }
        }
        /// <summary>停止发送任务并关闭底层连接；可重复调用。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            outbound.CompleteAdding(); Peer.Dispose();
        }
    }

    /// <summary>聊天室服务端：接受 TCP+TLS 连接、校验房间码、维护成员与历史并广播消息。</summary>
    /// <remarks>共享状态由 <c>gate</c> 保护；<see cref="Start"/> 只可调用一次，<see cref="Dispose"/> 可重复调用。
    /// 所有广播都在 <c>gate</c> 内完成，保证消息顺序与历史一致。</remarks>
    public sealed class ChatServer : IDisposable
    {
        private readonly object gate = new object();
        // 以下四个集合均由 gate 保护：在线成员、握手中的连接、历史消息、按 IP 的连接尝试记录。
        private readonly List<ServerGuest> guests = new List<ServerGuest>();
        private readonly HashSet<ServerGuest> pending = new HashSet<ServerGuest>();
        private readonly Queue<ChatPacket> history = new Queue<ChatPacket>();
        private readonly Dictionary<string, Queue<DateTime>> attempts = new Dictionary<string, Queue<DateTime>>();
        // 房间码只保存 SHA-256，避免明文长期驻留内存。
        private readonly byte[] codeHash;
        private readonly ChatOptions options;
        // 外部传入的证书归调用方所有；自动生成的证书随服务器一起释放。
        private readonly X509Certificate2 certificate;
        private readonly bool ownsCertificate;
        /// <summary>服务端证书的 SHA-256 指纹，供客户端带外校验。</summary>
        public string Fingerprint { get { return ChatSecurity.Fingerprint(certificate); } }
        /// <summary>当前保留的历史消息条数，供测试读取。</summary>
        internal int HistoryCount { get { lock (gate) return history.Count; } }
        // 每次服务启动生成新的会话标识，客户端据此区分不同批次的欢迎帧与历史消息。
        private readonly string session = Guid.NewGuid().ToString("N");
        private TcpListener listener;
        private volatile bool running;
        private int online;
        // 消息序号在 gate 内递增，客户端可用于排序和去重。
        private long sequence;
        private int disposed;
        /// <summary>当前在线人数。</summary>
        public int Online { get { return Volatile.Read(ref online); } }
        /// <summary>实际监听端口；传入 0 时由系统分配。</summary>
        public int Port { get; private set; }
        /// <summary>创建聊天室服务端。</summary>
        /// <param name="code">房间码，要求 6～64 个字符且不含控制字符。</param>
        /// <param name="options">容量与限流参数；为 null 时使用默认值。</param>
        /// <param name="certificate">服务端证书；为 null 时自动生成并由本对象释放。</param>
        /// <exception cref="ArgumentException">房间码长度或内容不合法。</exception>
        public ChatServer(string code, ChatOptions options = null, X509Certificate2 certificate = null)
        {
            code = (code ?? "").Trim();
            if (code.Length < 6 || code.Length > 64 || code.Any(Char.IsControl)) throw new ArgumentException("房间码需要 6～64 个字符。");
            this.options = options ?? new ChatOptions();
            this.certificate = certificate ?? ChatSecurity.CreateCertificate(); ownsCertificate = certificate == null;
            using (SHA256 hash = SHA256.Create()) codeHash = hash.ComputeHash(Encoding.UTF8.GetBytes(code));
        }
        /// <summary>在指定地址和端口启动监听，并在后台接受连接。</summary>
        /// <param name="address">监听地址；通配地址表示接受所有网卡。</param>
        /// <param name="port">监听端口；0 表示由系统自动分配，实际值见 <see cref="Port"/>。</param>
        /// <exception cref="InvalidOperationException">服务已启动或已释放。</exception>
        public void Start(IPAddress address, int port)
        {
            if (Volatile.Read(ref disposed) != 0 || listener != null) throw new InvalidOperationException("请新建服务后启动。");
            // 监听队列最多 32 个等待连接，配合 PendingLimit 限制握手期间的资源占用。
            listener = new TcpListener(address, port); listener.Start(32); Port = ((IPEndPoint)listener.LocalEndpoint).Port; running = true;
            Task.Run(() =>
            {
                try
                {
                    while (running)
                    {
                        TcpClient client = listener.AcceptTcpClient();
                        // 所有准入判断都在锁内完成，避免并发接受时突破人数与尝试次数上限。
                        lock (gate)
                        {
                            // 握手未完成的连接达到上限时直接关闭，防止半开连接耗尽资源。
                            if (!running || pending.Count >= options.PendingLimit) { client.Close(); continue; }
                            string ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                            DateTime now = DateTime.UtcNow;
                            // 清理一分钟内没有新尝试的 IP 记录；表最多 256 项，防止大量来源撑爆内存。
                            foreach (string old in attempts.Where(p => p.Value.Count == 0 || (now - p.Value.Last()).TotalMinutes > 1).Select(p => p.Key).ToArray()) attempts.Remove(old);
                            Queue<DateTime> recent;
                            if (!attempts.TryGetValue(ip, out recent)) { if (attempts.Count >= 256) { client.Close(); continue; } recent = new Queue<DateTime>(); attempts[ip] = recent; }
                            // 同一 IP 每分钟最多 12 次连接尝试，超出直接断开。
                            while (recent.Count > 0 && (now - recent.Peek()).TotalSeconds > 60) recent.Dequeue();
                            if (recent.Count >= 12) { client.Close(); continue; } recent.Enqueue(now);
                            // 每个连接独立任务处理；先放入 pending，握手成功后才移入 guests。
                            ServerGuest guest = new ServerGuest(client, options.OutboundLimit); pending.Add(guest); Task.Run(() => Serve(guest));
                        }
                    }
                }
                // Stop() 会让 AcceptTcpClient 抛异常，这属于正常关闭路径。
                catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
            });
        }
        // 固定时间比较房间码哈希：循环始终遍历全部字节，避免通过响应时间推断前缀。
        private bool CorrectCode(string code)
        {
            byte[] supplied; using (SHA256 hash = SHA256.Create()) supplied = hash.ComputeHash(Encoding.UTF8.GetBytes((code ?? "").Trim()));
            int different = 0; for (int i = 0; i < supplied.Length; i++) different |= supplied[i] ^ codeHash[i]; return different == 0;
        }
        // 单个连接的完整生命周期：TLS、join 校验、消息循环，最后从成员表移除。
        private void Serve(ServerGuest guest)
        {
            bool joined = false;
            try
            {
                // 握手阶段使用较短的接收超时，防止连接后迟迟不发送 join。
                guest.Peer.Client.ReceiveTimeout = 10000;
                guest.Peer.SecureAsServer(certificate);
                ChatPacket join = ChatWire.Read(guest.Peer.Stream);
                if (join.Kind != "join" || !CorrectCode(join.Code)) { guest.Peer.Send(new ChatPacket { Kind = "reject", Text = "房间码不正确。" }); return; }
                string nick = ChatWire.Clean(join.Nick);
                // 昵称在服务端再次清洗和限长，不能信任客户端校验。
                if (nick.Length == 0 || nick.Length > 20 || nick.Contains("\n")) { guest.Peer.Send(new ChatPacket { Kind = "reject", Text = "昵称需要 1～20 个字符。" }); return; }
                string reject = null;
                lock (gate)
                {
                    if (!running) return;
                    if (guests.Count >= options.ParticipantLimit) reject = "房间已满，最多 " + options.ParticipantLimit + " 人。";
                    else if (guests.Any(g => String.Equals(g.Peer.Nick, nick, StringComparison.OrdinalIgnoreCase))) reject = "这个昵称正在使用，请换一个。";
                    else
                    {
                        guest.Peer.Nick = nick; guests.Add(guest); joined = true; Volatile.Write(ref online, guests.Count);
                        // welcome 与历史消息都经由该连接的有界队列发送，顺序固定为先欢迎后历史。
                        guest.Queue(new ChatPacket { Kind = "welcome", Session = session, Online = guests.Count });
                        foreach (ChatPacket item in history) guest.Queue(item);
                        Broadcast(new ChatPacket { Kind = "state", Online = guests.Count, Text = nick + " 加入了会话" });
                    }
                }
                // 拒绝消息在锁外发送：写超时（最长 4 秒）不能阻塞其他客户端的加入、离开和广播。
                if (reject != null) { guest.Peer.Send(new ChatPacket { Kind = "reject", Text = reject }); return; }
                // welcome 之后放宽接收超时：客户端每 15 秒心跳，45 秒足以容忍数次丢包。
                guest.Peer.Client.ReceiveTimeout = 45000;
                // 每个连接独立维护 10 秒滑动窗口内的发送计数，服务端限流不依赖客户端自觉。
                Queue<DateTime> sent = new Queue<DateTime>();
                while (running)
                {
                    ChatPacket packet = ChatWire.Read(guest.Peer.Stream);
                    if (packet.Kind == "ping") { guest.Queue(new ChatPacket { Kind = "pong" }); continue; }
                    // 非 text 内容类型单独提示升级，为后续协议扩展保留空间。
                    if (packet.ContentType != "text") { guest.Queue(new ChatPacket { Kind = "error", Text = "暂不支持这种消息类型，请升级客户端。" }); continue; }
                    if (packet.Kind != "send") throw new System.IO.InvalidDataException("无效的客户端消息。");
                    string body = ChatWire.Clean(packet.Text);
                    // 按清洗后的文本计长，服务端是最终权威。
                    if (body.Length == 0 || body.Length > options.MessageLength) { guest.Queue(new ChatPacket { Kind = "error", Text = "消息需要 1～" + options.MessageLength + " 个字符。" }); continue; }
                    DateTime now = DateTime.UtcNow;
                    while (sent.Count > 0 && (now - sent.Peek()).TotalSeconds >= 10) sent.Dequeue();
                    if (sent.Count >= options.SendBurst) { guest.Queue(new ChatPacket { Kind = "error", Text = "发送太快，请稍等几秒。" }); continue; } sent.Enqueue(now);
                    lock (gate)
                    {
                        if (!running) return;
                        // 入历史与广播在同一把锁内完成，保证所有客户端看到相同顺序。
                        ChatPacket message = new ChatPacket { Kind = "message", Id = ++sequence, Session = session, Nick = nick, Text = body, Time = now.ToString("o") };
                        history.Enqueue(message); while (history.Count > options.HistoryLimit) history.Dequeue(); Broadcast(message);
                    }
                }
            }
            catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
            finally
            {
                // 无论正常离开还是异常掉线，都先释放连接，再从 pending/guests 中移除。
                guest.Dispose();
                lock (gate)
                {
                    pending.Remove(guest);
                    if (joined) { guests.Remove(guest); Volatile.Write(ref online, guests.Count); if (running) Broadcast(new ChatPacket { Kind = "state", Online = guests.Count, Text = guest.Peer.Nick + " 离开了会话" }); }
                }
            }
        }
        // 调用方必须持有 gate：枚举 guests 期间成员列表不能变化。
        private void Broadcast(ChatPacket packet) { foreach (ServerGuest guest in guests) guest.Queue(packet); }
        /// <summary>停止监听并清空共享状态；可重复调用。</summary>
        /// <remarks>只同步关闭尚未完成握手的连接；已加入的连接由各自的 Serve 循环在读写失败后释放。</remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            running = false; if (listener != null) listener.Stop();
            lock (gate)
            {
                foreach (ServerGuest guest in pending) guest.Dispose();
                // 已加入的成员也要主动关闭；否则它们的读循环要等 45 秒超时，客户端不会及时断开。
                foreach (ServerGuest guest in guests) guest.Dispose();
                pending.Clear(); guests.Clear(); history.Clear(); attempts.Clear(); Array.Clear(codeHash, 0, codeHash.Length); Volatile.Write(ref online, 0);
            }
            // 只释放自己创建的证书；外部传入的证书由调用方负责。
            if (ownsCertificate) certificate.Dispose();
        }
    }

}

