using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OneLineReader
{
    internal sealed class ChatPacket
    {
        public string Kind = "", Nick = "", Text = "", Code = "", Time = "", Session = "", ContentType = "text";
        public int Version = 2;
        public long Id;
        public int Online;
    }

    internal static class ChatWire
    {
        public const int MaxFrame = 8192;
        public static void Write(Stream stream, ChatPacket packet)
        {
            XElement xml = new XElement("chat", new XAttribute("version", packet.Version), new XAttribute("contentType", packet.ContentType), new XAttribute("kind", packet.Kind), new XAttribute("nick", packet.Nick),
                new XAttribute("code", packet.Code), new XAttribute("time", packet.Time), new XAttribute("session", packet.Session),
                new XAttribute("id", packet.Id), new XAttribute("online", packet.Online), new XElement("text", packet.Text));
            byte[] body = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
            if (body.Length > MaxFrame) throw new InvalidDataException("消息过长。");
            byte[] header = { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length };
            stream.Write(header, 0, header.Length); stream.Write(body, 0, body.Length); stream.Flush();
        }
        public static ChatPacket Read(Stream stream)
        {
            byte[] header = new byte[4]; ReadExact(stream, header);
            int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (length <= 0 || length > MaxFrame) throw new InvalidDataException("无效的消息长度。");
            byte[] body = new byte[length]; ReadExact(stream, body);
            XElement xml;
            using (StringReader text = new StringReader(new UTF8Encoding(false, true).GetString(body)))
            using (XmlReader reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFrame })) xml = XElement.Load(reader);
            if (xml.Name != "chat") throw new InvalidDataException("无效的聊天消息。");
            long id; int online, version;
            if (!Int32.TryParse((string)xml.Attribute("version"), out version) || version != 2) throw new InvalidDataException("聊天协议不兼容，请同时升级客户端和服务端。");
            Int64.TryParse((string)xml.Attribute("id"), out id); Int32.TryParse((string)xml.Attribute("online"), out online);
            return new ChatPacket { Version = version, ContentType = (string)xml.Attribute("contentType") ?? "text", Kind = (string)xml.Attribute("kind") ?? "", Nick = (string)xml.Attribute("nick") ?? "", Code = (string)xml.Attribute("code") ?? "",
                Time = (string)xml.Attribute("time") ?? "", Session = (string)xml.Attribute("session") ?? "", Id = id, Online = online, Text = (string)xml.Element("text") ?? "" };
        }
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
        public static string Clean(string value)
        {
            StringBuilder result = new StringBuilder();
            foreach (char c in value ?? "") if (!Char.IsControl(c) || c == '\n') result.Append(c);
            string clean = result.ToString().Trim(); XmlConvert.VerifyXmlChars(clean); return clean;
        }
        public static bool Expected(Exception ex)
        {
            return ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is XmlException || ex is ArgumentException || ex is TimeoutException || ex is AuthenticationException || ex is CryptographicException || ex is System.ComponentModel.Win32Exception;
        }
    }

    internal sealed class ChatPeer : IDisposable
    {
        public readonly TcpClient Client;
        public Stream Stream { get; private set; }
        private readonly object sending = new object();
        private int disposed;
        public string Nick = "";
        public ChatPeer(TcpClient client)
        {
            Client = client; Client.NoDelay = true; Client.ReceiveTimeout = 45000; Client.SendTimeout = 4000; Stream = client.GetStream();
        }
        public void Send(ChatPacket packet) { lock (sending) ChatWire.Write(Stream, packet); }
        public void SecureAsServer(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) { Stream = ChatSecurity.Server(Stream, certificate); }
        public void SecureAsClient(string fingerprint) { Stream = ChatSecurity.Client(Stream, fingerprint); }
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref disposed, 1) != 0) return;
            Client.Close();
            try { Stream.Dispose(); } catch (IOException) { }
        }
    }
}

