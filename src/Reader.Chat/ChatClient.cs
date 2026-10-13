using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Authentication;

using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;


namespace Reader.Chat
{
    public sealed class ChatJoinException : IOException { public ChatJoinException(string message) : base(message) { } }

    public sealed class ChatConnection : IDisposable
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

}

