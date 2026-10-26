// 聊天客户端连接：TCP 建连、TLS 指纹校验、join/welcome 握手、心跳保活与收发循环。
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
    /// <summary>加入房间被服务端拒绝时抛出的异常，消息内容即拒绝原因。</summary>
    public sealed class ChatJoinException : IOException { public ChatJoinException(string message) : base(message) { } }

    /// <summary>到聊天室服务端的单条客户端连接。</summary>
    /// <remarks>连接状态位通过 <see cref="Interlocked"/> 和 volatile 保护，但整体不是线程安全的：
    /// <see cref="Received"/> 与 <see cref="Dropped"/> 在后台线程触发，订阅方需自行切换线程。</remarks>
    public sealed class ChatConnection : IDisposable
    {
        private readonly TcpClient client = new TcpClient();
        private readonly ChatOptions options;
        private ChatPeer peer;
        private System.Threading.Timer heartbeat;
        private int stopped;
        private int droppedOnce;
        private volatile bool connected;
        /// <summary>是否已完成 TLS 握手并收到 welcome。</summary>
        public bool Connected { get { return connected; } }
        /// <summary>当前传输是否已通过 TLS 认证并处于加密状态。</summary>
        internal bool Encrypted { get { var tls = peer == null ? null : peer.Stream as System.Net.Security.SslStream; return tls != null && tls.IsAuthenticated && tls.IsEncrypted; } }
        /// <summary>收到服务端数据包时触发，包括欢迎帧；在接收线程上调用。</summary>
        public event Action<ChatPacket> Received;
        /// <summary>连接中断时触发一次，参数为面向用户的中文原因。</summary>
        public event Action<string> Dropped;
        /// <summary>创建尚未连接的客户端。</summary>
        /// <param name="options">容量与限流参数；为 null 时使用默认值。</param>
        public ChatConnection(ChatOptions options = null) { this.options = options ?? new ChatOptions(); }
        /// <summary>异步建立连接、完成 TLS 与房间握手，并启动心跳和接收循环。</summary>
        /// <param name="host">服务端地址。</param>
        /// <param name="port">服务端端口。</param>
        /// <param name="nick">加入房间使用的昵称。</param>
        /// <param name="code">房间码。</param>
        /// <param name="fingerprint">服务端证书的 SHA-256 指纹。</param>
        /// <returns>握手完成后结束的任务。</returns>
        /// <remarks>TCP 连接超时为 5 秒。任何失败都会先释放连接再抛出，不会遗留 socket 或定时器。</remarks>
        public Task ConnectAsync(string host, int port, string nick, string code, string fingerprint)
        {
            return Task.Run(() =>
            {
                try
                {
                    // TcpClient 没有连接超时参数：用 BeginConnect + 等待句柄实现 5 秒上限。
                    IAsyncResult pending = client.BeginConnect(host, port, null, null);
                    using (pending.AsyncWaitHandle)
                    {
                        if (!pending.AsyncWaitHandle.WaitOne(5000)) throw new TimeoutException("连接超时，请检查地址、端口和服务电脑。");
                        client.EndConnect(pending);
                    }
                    if (Volatile.Read(ref stopped) != 0) throw new ObjectDisposedException("聊天连接");
                    // 握手阶段使用较短的接收超时，防止服务端迟迟不回应而长期占用连接。
                    peer = new ChatPeer(client); client.ReceiveTimeout = 10000;
                    // 用带外指纹认证服务端证书，防止中间人替换证书。
                    peer.SecureAsClient(fingerprint);
                    peer.Send(new ChatPacket { Kind = "join", Nick = nick, Code = code });
                    ChatPacket welcome = ChatWire.Read(peer.Stream);
                    if (welcome.Kind == "reject") throw new ChatJoinException(welcome.Text);
                    if (welcome.Kind != "welcome") throw new InvalidDataException("服务端响应不正确。");
                    // welcome 之后放宽接收超时：客户端每 15 秒发送心跳，45 秒可容忍数次丢包。
                    client.ReceiveTimeout = 45000; connected = true;
                    if (Volatile.Read(ref stopped) != 0) throw new ObjectDisposedException("聊天连接");
                    Action<ChatPacket> received = Received; if (received != null) received(welcome);
                    // 心跳独立于接收循环：发送失败即视为断线，由 Drop 统一收尾。
                    heartbeat = new System.Threading.Timer(s =>
                    {
                        try { if (Connected) peer.Send(new ChatPacket { Kind = "ping" }); }
                        catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; Drop("连接已中断"); }
                    }, null, 15000, 15000);
                    // 连接可能刚好在心跳创建后被释放，此时立即停止定时器。
                    if (Volatile.Read(ref stopped) != 0) heartbeat.Dispose();
                    // 接收循环放到独立任务，避免 ConnectAsync 返回后仍在当前线程阻塞。
                    Task.Run(() => ReceiveLoop());
                }
                // 任一阶段失败都统一释放：关闭 socket、定时器和 TLS 流。
                catch { Dispose(); throw; }
            });
        }
        // 循环读取服务端帧；pong 仅用于保活，不向上层转发。
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
        /// <summary>异步发送一条文本消息。</summary>
        /// <param name="text">消息原文；发送前会清洗控制字符并校验长度。</param>
        /// <returns>写入完成后结束的任务。</returns>
        /// <exception cref="IOException">连接尚未建立。</exception>
        /// <exception cref="ArgumentException">清洗后为空或超过服务端长度限制。</exception>
        public Task SendAsync(string text)
        {
            return Task.Run(() =>
            {
                if (!Connected) throw new IOException("尚未连接到会话。");
                // 发送前按与服务端相同的规则清洗和校验，尽早暴露非法输入。
                string body = ChatWire.Clean(text);
                if (body.Length == 0 || body.Length > options.MessageLength) throw new ArgumentException("消息需要 1～" + options.MessageLength + " 个字符。");
                try { peer.Send(new ChatPacket { Kind = "send", Text = body }); }
                catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; Drop("发送失败，连接已中断"); throw; }
            });
        }
        // 只通知一次掉线：接收循环与心跳可能同时失败，用 droppedOnce 去重后统一释放。
        private void Drop(string message)
        {
            bool notify = Connected && Interlocked.Exchange(ref droppedOnce, 1) == 0; Dispose();
            Action<string> dropped = Dropped; if (notify && dropped != null) dropped(message);
        }
        /// <summary>关闭连接并停止心跳与接收循环；可重复调用。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            connected = false; if (heartbeat != null) heartbeat.Dispose(); client.Close(); if (peer != null) peer.Dispose();
        }
    }

}

