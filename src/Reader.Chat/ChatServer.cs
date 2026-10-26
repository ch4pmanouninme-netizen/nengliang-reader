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
    public sealed class ServerGuest : IDisposable
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

    public sealed class ChatServer : IDisposable
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
                string reject = null;
                lock (gate)
                {
                    if (!running) return;
                    if (guests.Count >= options.ParticipantLimit) reject = "房间已满，最多 " + options.ParticipantLimit + " 人。";
                    else if (guests.Any(g => String.Equals(g.Peer.Nick, nick, StringComparison.OrdinalIgnoreCase))) reject = "这个昵称正在使用，请换一个。";
                    else
                    {
                        guest.Peer.Nick = nick; guests.Add(guest); joined = true; Volatile.Write(ref online, guests.Count);
                        guest.Queue(new ChatPacket { Kind = "welcome", Session = session, Online = guests.Count });
                        foreach (ChatPacket item in history) guest.Queue(item);
                        Broadcast(new ChatPacket { Kind = "state", Online = guests.Count, Text = nick + " 加入了会话" });
                    }
                }
                // 拒绝消息在锁外发送：写超时（最长 4 秒）不能阻塞其他客户端的加入、离开和广播。
                if (reject != null) { guest.Peer.Send(new ChatPacket { Kind = "reject", Text = reject }); return; }
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
                        if (!running) return;
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

}

