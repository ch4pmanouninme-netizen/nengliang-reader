// 聊天协议共享定义：数据包结构、长度前缀 XML 帧的编解码、输入清洗与预期异常分类。
using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reader.Chat
{
    /// <summary>聊天协议的数据包，承载 XML 帧的属性与正文。</summary>
    /// <remarks>同一类型承载 join、welcome、message、ping、pong、state、error、reject 等线上帧，
    /// 客户端还用 overflow 表示收件箱溢出；具体语义由 <see cref="Kind"/> 决定，未使用的字段保持默认值。</remarks>
    public sealed class ChatPacket
    {
        /// <summary>帧类型（join、welcome、message 等）、昵称、正文、房间码、服务端时间、会话标识和内容类型。</summary>
        public string Kind = "", Nick = "", Text = "", Code = "", Time = "", Session = "", ContentType = "text";
        /// <summary>协议版本；当前固定为 2，版本不符会在读取时被拒绝。</summary>
        public int Version = 2;
        /// <summary>消息序号，由服务端单调递增分配。</summary>
        public long Id;
        /// <summary>服务端广播的当前在线人数。</summary>
        public int Online;
    }

    /// <summary>聊天帧的编解码与输入清洗。</summary>
    /// <remarks>帧格式为 4 字节大端长度前缀加 UTF-8 XML 正文；单帧上限 <see cref="MaxFrame"/> 字节。
    /// 读取端禁用 DTD 和外部解析器，防止 XXE 与实体膨胀。</remarks>
    public static class ChatWire
    {
        /// <summary>单帧字节上限；用于拒绝对端声明的超大长度，避免内存耗尽。</summary>
        public const int MaxFrame = 8192;
        /// <summary>把数据包写成带长度前缀的 XML 帧并刷新流。</summary>
        /// <param name="stream">已建立（可能已加密）的传输流。</param>
        /// <param name="packet">待发送的数据包。</param>
        /// <exception cref="InvalidDataException">编码后的正文超过 <see cref="MaxFrame"/>。</exception>
        public static void Write(Stream stream, ChatPacket packet)
        {
            // 固定结构：所有标量放属性，正文放唯一的 text 子元素，便于两端稳定解析。
            XElement xml = new XElement("chat", new XAttribute("version", packet.Version), new XAttribute("contentType", packet.ContentType), new XAttribute("kind", packet.Kind), new XAttribute("nick", packet.Nick),
                new XAttribute("code", packet.Code), new XAttribute("time", packet.Time), new XAttribute("session", packet.Session),
                new XAttribute("id", packet.Id), new XAttribute("online", packet.Online), new XElement("text", packet.Text));
            byte[] body = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
            // 必须在写出长度头之前检查，避免对端收到无法处理的超限帧。
            if (body.Length > MaxFrame) throw new InvalidDataException("消息过长。");
            // 4 字节大端长度；正文不包含终止符。
            byte[] header = { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length };
            stream.Write(header, 0, header.Length); stream.Write(body, 0, body.Length); stream.Flush();
        }
        /// <summary>从流中读取一个完整的聊天帧。</summary>
        /// <param name="stream">已建立（可能已加密）的传输流。</param>
        /// <returns>解析后的数据包。</returns>
        /// <exception cref="InvalidDataException">长度非法、根元素错误或协议版本不兼容。</exception>
        /// <exception cref="XmlException">XML 内容格式错误。</exception>
        /// <exception cref="EndOfStreamException">对端在帧中途关闭连接。</exception>
        public static ChatPacket Read(Stream stream)
        {
            byte[] header = new byte[4]; ReadExact(stream, header);
            int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            // 拒绝非正数和超限长度：前者没有合法用途，后者可能是内存耗尽探测。
            if (length <= 0 || length > MaxFrame) throw new InvalidDataException("无效的消息长度。");
            byte[] body = new byte[length]; ReadExact(stream, body);
            XElement xml;
            // UTF8Encoding(false, true) 遇到非法字节直接抛异常，不会静默替换成 U+FFFD。
            using (StringReader text = new StringReader(new UTF8Encoding(false, true).GetString(body)))
            // 禁用 DTD、外部解析器并限制文档字符数，防止 XXE 和实体膨胀。
            using (XmlReader reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFrame })) xml = XElement.Load(reader);
            if (xml.Name != "chat") throw new InvalidDataException("无效的聊天消息。");
            long id; int online, version;
            // 只接受 version=2：字段语义随版本变化，混用会导致消息被错误解释。
            if (!Int32.TryParse((string)xml.Attribute("version"), out version) || version != 2) throw new InvalidDataException("聊天协议不兼容，请同时升级客户端和服务端。");
            Int64.TryParse((string)xml.Attribute("id"), out id); Int32.TryParse((string)xml.Attribute("online"), out online);
            return new ChatPacket { Version = version, ContentType = (string)xml.Attribute("contentType") ?? "text", Kind = (string)xml.Attribute("kind") ?? "", Nick = (string)xml.Attribute("nick") ?? "", Code = (string)xml.Attribute("code") ?? "",
                Time = (string)xml.Attribute("time") ?? "", Session = (string)xml.Attribute("session") ?? "", Id = id, Online = online, Text = (string)xml.Element("text") ?? "" };
        }
        // 网络流一次 Read 可能只返回部分数据，必须循环到填满整个缓冲区。
        private static void ReadExact(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0) throw new EndOfStreamException("聊天连接已断开。");
                offset += read;
            }
        }
        /// <summary>清洗用户输入：去掉除换行外的控制字符，并校验其符合 XML 字符集。</summary>
        /// <param name="value">原始文本，可为 null。</param>
        /// <returns>去除首尾空白后的安全文本。</returns>
        /// <exception cref="XmlException">文本包含 XML 不允许的字符。</exception>
        public static string Clean(string value)
        {
            // 控制字符会破坏终端显示和 XML 结构；保留换行以支持多行消息。
            StringBuilder result = new StringBuilder();
            foreach (char c in value ?? "") if (!Char.IsControl(c) || c == '\n') result.Append(c);
            string clean = result.ToString().Trim(); XmlConvert.VerifyXmlChars(clean); return clean;
        }
        /// <summary>判断异常是否属于连接生命周期内的预期失败。</summary>
        /// <param name="ex">待判断的异常。</param>
        /// <returns>属于预期失败返回 true。</returns>
        /// <remarks>对端断开、超时、TLS 失败等异常应转化为连接状态变化，而不是让后台任务崩溃。</remarks>
        public static bool Expected(Exception ex)
        {
            return ex is InvalidDataException || ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is XmlException || ex is ArgumentException || ex is TimeoutException || ex is AuthenticationException || ex is CryptographicException || ex is System.ComponentModel.Win32Exception;
        }
    }

    /// <summary>基于 TcpClient 的协议端点，负责发送串行化与 TLS 升级。</summary>
    /// <remarks>发送加锁以保证同一连接上的帧不会交错；接收循环由调用方驱动。
    /// <see cref="Dispose"/> 通过 <see cref="System.Threading.Interlocked"/> 保证可重复调用。</remarks>
    public sealed class ChatPeer : IDisposable
    {
        /// <summary>底层 TCP 客户端；服务端通过它调整接收超时。</summary>
        public readonly TcpClient Client;
        /// <summary>当前传输流；TLS 升级后替换为加密流。</summary>
        public Stream Stream { get; private set; }
        private readonly object sending = new object();
        private int disposed;
        /// <summary>服务端记录的对端昵称；客户端侧通常为空。</summary>
        public string Nick = "";
        /// <summary>包装一个已连接的 TCP 客户端并设置收发超时。</summary>
        /// <param name="client">已建立的 TCP 连接。</param>
        public ChatPeer(TcpClient client)
        {
            // NoDelay 降低小帧的发送延迟；发送超时 4 秒防止慢客户端阻塞广播线程，
            // 接收超时 45 秒与 15 秒心跳节奏匹配，可容忍数次丢失。
            Client = client; Client.NoDelay = true; Client.ReceiveTimeout = 45000; Client.SendTimeout = 4000; Stream = client.GetStream();
        }
        /// <summary>发送一个数据包；多个线程调用时按帧串行写入。</summary>
        /// <param name="packet">待发送的数据包。</param>
        public void Send(ChatPacket packet) { lock (sending) ChatWire.Write(Stream, packet); }
        /// <summary>把底层流升级为 TLS 服务端流。</summary>
        /// <param name="certificate">服务端证书。</param>
        public void SecureAsServer(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) { Stream = ChatSecurity.Server(Stream, certificate); }
        /// <summary>把底层流升级为 TLS 客户端流，并校验服务端指纹。</summary>
        /// <param name="fingerprint">带外获得的服务端证书 SHA-256 指纹。</param>
        public void SecureAsClient(string fingerprint) { Stream = ChatSecurity.Client(Stream, fingerprint); }
        /// <summary>关闭 TCP 连接并释放传输流；可重复调用。</summary>
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0) return;
            Client.Close();
            // Close 已关闭底层 socket，SslStream 再释放时可能抛 IOException，属预期情况。
            try { Stream.Dispose(); } catch (IOException) { }
        }
    }
}


