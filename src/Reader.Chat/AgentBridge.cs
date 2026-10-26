using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Reader.Chat
{
    // Minimal JSON reader for agent replies. Retained from the 1.5 line deliberately: the shapes we
    // read are three fixed paths, and System.Text.Json would pull a full DOM in to walk them.
    internal static class AgentJson
    {
        public static object Parse(string text)
        {
            if (text == null) throw new InvalidDataException("JSON 内容为空。");
            int index = 0;
            object value = ParseValue(text, ref index, 0);
            Skip(text, ref index);
            if (index != text.Length) throw new InvalidDataException("JSON 末尾有多余内容。");
            return value;
        }

        // Walks nested objects and arrays; numeric path segments index into arrays.
        public static object Member(object node, params string[] path)
        {
            object current = node;
            foreach (string name in path)
            {
                Dictionary<string, object> map = current as Dictionary<string, object>;
                if (map != null) { if (!map.TryGetValue(name, out current)) return null; continue; }
                List<object> list = current as List<object>;
                int index;
                if (list != null && Int32.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < list.Count) { current = list[index]; continue; }
                return null;
            }
            return current;
        }

        public static string Text(object node, params string[] path) { return Member(node, path) as string; }

        private static void Skip(string text, ref int index)
        {
            while (index < text.Length && (text[index] == ' ' || text[index] == '\t' || text[index] == '\n' || text[index] == '\r')) index++;
        }

        private static object ParseValue(string text, ref int index, int depth)
        {
            // 端点响应是外部输入：限制嵌套深度，避免恶意 JSON 触发 StackOverflow。
            if (depth > 64) throw new InvalidDataException("JSON 嵌套过深。");
            Skip(text, ref index);
            if (index >= text.Length) throw new InvalidDataException("JSON 内容不完整。");
            char c = text[index];
            if (c == '{') return ParseObject(text, ref index, depth + 1);
            if (c == '[') return ParseArray(text, ref index, depth + 1);
            if (c == '"') return ParseString(text, ref index);
            if (String.CompareOrdinal(text, index, "true", 0, 4) == 0) { index += 4; return true; }
            if (String.CompareOrdinal(text, index, "false", 0, 5) == 0) { index += 5; return false; }
            if (String.CompareOrdinal(text, index, "null", 0, 4) == 0) { index += 4; return null; }
            return ParseNumber(text, ref index);
        }

        private static Dictionary<string, object> ParseObject(string text, ref int index, int depth)
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            index++; Skip(text, ref index);
            if (index < text.Length && text[index] == '}') { index++; return map; }
            while (true)
            {
                Skip(text, ref index);
                string name = ParseString(text, ref index);
                Skip(text, ref index);
                if (index >= text.Length || text[index] != ':') throw new InvalidDataException("JSON 对象缺少冒号。");
                index++;
                map[name] = ParseValue(text, ref index, depth);
                Skip(text, ref index);
                if (index >= text.Length) throw new InvalidDataException("JSON 对象没有结束。");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == '}') { index++; return map; }
                throw new InvalidDataException("JSON 对象格式不正确。");
            }
        }

        private static List<object> ParseArray(string text, ref int index, int depth)
        {
            List<object> items = new List<object>();
            index++; Skip(text, ref index);
            if (index < text.Length && text[index] == ']') { index++; return items; }
            while (true)
            {
                items.Add(ParseValue(text, ref index, depth));
                Skip(text, ref index);
                if (index >= text.Length) throw new InvalidDataException("JSON 数组没有结束。");
                if (text[index] == ',') { index++; continue; }
                if (text[index] == ']') { index++; return items; }
                throw new InvalidDataException("JSON 数组格式不正确。");
            }
        }

        private static string ParseString(string text, ref int index)
        {
            if (index >= text.Length || text[index] != '"') throw new InvalidDataException("JSON 缺少字符串。");
            index++;
            StringBuilder result = new StringBuilder();
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') return result.ToString();
                if (c != '\\') { result.Append(c); continue; }
                if (index >= text.Length) break;
                char escape = text[index++];
                if (escape == '"') result.Append('"');
                else if (escape == '\\') result.Append('\\');
                else if (escape == '/') result.Append('/');
                else if (escape == 'b') result.Append('\b');
                else if (escape == 'f') result.Append('\f');
                else if (escape == 'n') result.Append('\n');
                else if (escape == 'r') result.Append('\r');
                else if (escape == 't') result.Append('\t');
                else if (escape == 'u')
                {
                    int code;
                    if (index + 4 > text.Length || !Int32.TryParse(text.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) throw new InvalidDataException("JSON 的 \\u 转义不正确。");
                    result.Append((char)code); index += 4;
                }
                else throw new InvalidDataException("JSON 的转义字符不正确。");
            }
            throw new InvalidDataException("JSON 字符串没有结束。");
        }

        private static object ParseNumber(string text, ref int index)
        {
            int start = index;
            while (index < text.Length && "-+.eE0123456789".IndexOf(text[index]) >= 0) index++;
            double value;
            if (index == start || !Double.TryParse(text.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new InvalidDataException("JSON 数字不正确。");
            return value;
        }

        public static string Quote(string value)
        {
            StringBuilder result = new StringBuilder("\"");
            foreach (char c in value ?? "")
            {
                if (c == '"' || c == '\\') result.Append('\\').Append(c);
                else if (c == '\n') result.Append("\\n");
                else if (c == '\r') result.Append("\\r");
                else if (c == '\t') result.Append("\\t");
                else if (c < ' ') result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else result.Append(c);
            }
            return result.Append('"').ToString();
        }
    }

    internal static class AgentText
    {
        private const string Breaks = "。！？；\n";

        // Longest cut at or below limit that lands after a sentence or line break, never splitting a surrogate pair.
        public static int Cut(string text, int limit)
        {
            if (text.Length <= limit) return text.Length;
            int floor = Math.Max(1, limit / 2);
            int cut = limit;
            for (int i = limit - 1; i >= floor; i--) if (Breaks.IndexOf(text[i]) >= 0) { cut = i + 1; break; }
            if (cut > 0 && cut < text.Length && Char.IsHighSurrogate(text[cut - 1]) && Char.IsLowSurrogate(text[cut])) cut--;
            return Math.Max(1, cut);
        }
    }

    // Turns a stream of deltas into room-sized messages. Pure buffering: no I/O, no throttling.
    internal sealed class AgentChunker
    {
        private readonly int limit;
        private readonly StringBuilder buffer = new StringBuilder();
        public AgentChunker(int limit) { this.limit = limit; }

        public void Add(string delta, List<string> output)
        {
            if (String.IsNullOrEmpty(delta)) return;
            buffer.Append(delta);
            while (buffer.Length >= limit)
            {
                string pending = buffer.ToString();
                int cut = AgentText.Cut(pending, limit);
                string chunk = pending.Substring(0, cut).Trim();
                buffer.Remove(0, cut);
                if (chunk.Length > 0) output.Add(chunk);
            }
        }

        public string Flush()
        {
            string rest = buffer.ToString().Trim();
            buffer.Length = 0;
            return rest;
        }
    }

    public sealed class AgentOptions
    {
        public readonly string Endpoint, ApiKey, Model, Nick, Prompt;
        public readonly bool MentionOnly, Stream;
        public readonly int ReplyLimit, ChunkSize, QuestionLimit, SendBurst;
        public AgentOptions(string endpoint, string apiKey, string model, string nick, string prompt, bool mentionOnly = true, bool stream = true,
            int replyLimit = 2000, int chunkSize = 480, int questionLimit = 8, int sendBurst = 7)
        {
            endpoint = (endpoint ?? "").Trim(); ApiKey = (apiKey ?? "").Trim(); Model = (model ?? "").Trim();
            Nick = (nick ?? "").Trim(); Prompt = (prompt ?? "").Trim();
            Uri parsed;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
                throw new ArgumentException("Agent 端点需要完整的 http(s) 地址。");
            // The key travels in a header, so plaintext is only acceptable when it never leaves this machine.
            if (parsed.Scheme != Uri.UriSchemeHttps && !parsed.IsLoopback) throw new ArgumentException("Agent 端点必须使用 https；只有本机回环地址允许 http。");
            if (ApiKey.Length == 0 || ApiKey.Length > 512) throw new ArgumentException("Agent 密钥需要 1～512 个字符。");
            if (Model.Length == 0 || Model.Length > 128) throw new ArgumentException("模型名需要 1～128 个字符。");
            if (Nick.Length == 0 || Nick.Length > 20 || Nick.IndexOf('\n') >= 0) throw new ArgumentException("Agent 昵称需要 1～20 个字符。");
            if (Nick.IndexOf('@') >= 0) throw new ArgumentException("Agent 昵称不能包含 @。");
            if (Prompt.Length > 4000) throw new ArgumentException("Agent 角色设定不能超过 4000 个字符。");
            if (chunkSize < 50 || chunkSize > 500) throw new ArgumentException("分块大小需要在 50～500 之间。");
            if (replyLimit < chunkSize || replyLimit > 20000) throw new ArgumentException("单次回答上限需要在 " + chunkSize + "～20000 之间。");
            if (questionLimit < 1 || questionLimit > 32) throw new ArgumentException("待处理提问上限需要在 1～32 之间。");
            // The room service rejects a burst of 8 per 10s; the bridge must stay below that on its own.
            if (sendBurst < 1 || sendBurst > 7) throw new ArgumentException("发送节奏需要在 1～7 之间，留出服务端限流的余量。");
            Endpoint = endpoint; MentionOnly = mentionOnly; Stream = stream;
            ReplyLimit = replyLimit; ChunkSize = chunkSize; QuestionLimit = questionLimit; SendBurst = sendBurst;
        }
    }

    public static class AgentConfig
    {
        // The caller supplies the path: the data directory belongs to Reader.Core, and this library
        // deliberately does not reference it just to re-derive a folder name.
        public static void Save(string path, AgentOptions options)
        {
            XElement root = new XElement("agent", new XAttribute("version", "1"),
                new XElement("endpoint", options.Endpoint), new XElement("model", options.Model), new XElement("nick", options.Nick),
                new XElement("mentionOnly", options.MentionOnly ? "true" : "false"), new XElement("stream", options.Stream ? "true" : "false"),
                new XElement("replyLimit", options.ReplyLimit), new XElement("chunkSize", options.ChunkSize),
                new XElement("prompt", options.Prompt), Secret(options.ApiKey));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string target = path, temp = target + ".tmp";
            File.WriteAllText(temp, root.ToString(), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temp, target, target + ".bak", true);
            else File.Move(temp, target);
            // Without DPAPI the key is only as safe as the file, so at least keep it owner-only.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public static AgentOptions Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                XElement root;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (XmlReader xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 }))
                    root = XElement.Load(xml);
                if (root == null || root.Name != "agent") return null;
                return new AgentOptions((string)root.Element("endpoint"), Reveal(root), (string)root.Element("model"), (string)root.Element("nick"),
                    (string)root.Element("prompt"), (string)root.Element("mentionOnly") != "false", (string)root.Element("stream") != "false",
                    Number(root, "replyLimit", 2000), Number(root, "chunkSize", 480));
            }
            catch (Exception ex)
            {
                // FormatException：损坏的 base64 密钥；DllNotFoundException / PlatformNotSupportedException：
                // 在非 Windows 上读到 protected="true" 的 DPAPI 密钥。都应当降级为“没有可用配置”。
                if (!(ex is XmlException || ex is IOException || ex is InvalidDataException || ex is ArgumentException
                    || ex is CryptographicException || ex is UnauthorizedAccessException
                    || ex is FormatException || ex is DllNotFoundException || ex is PlatformNotSupportedException)) throw;
                return null;
            }
        }

        private static int Number(XElement root, string name, int fallback)
        {
            int value;
            return Int32.TryParse((string)root.Element(name), out value) ? value : fallback;
        }

        // On Windows the key is stored per user through DPAPI. Other platforms have no equivalent
        // wired up yet, so the value is marked unprotected rather than silently implying safety.
        private static XElement Secret(string value)
        {
            if (!OperatingSystem.IsWindows()) return new XElement("apiKey", new XAttribute("protected", "false"), value ?? "");
            byte[] plain = Encoding.UTF8.GetBytes(value ?? "");
            try { return new XElement("apiKey", new XAttribute("protected", "true"), Convert.ToBase64String(Dpapi.Protect(plain))); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        private static string Reveal(XElement root)
        {
            XElement key = root.Element("apiKey");
            if (key == null) return "";
            if ((string)key.Attribute("protected") != "true") return key.Value;
            byte[] plain = Dpapi.Unprotect(Convert.FromBase64String(key.Value));
            try { return Encoding.UTF8.GetString(plain); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        // crypt32 directly instead of the System.Security.Cryptography.ProtectedData package:
        // it keeps the chat library dependency-free, matching how the platform projects already interop.
        private static class Dpapi
        {
            private const int UiForbidden = 0x1;

            public static byte[] Protect(byte[] plain)
            {
                return Transform(plain, true);
            }

            public static byte[] Unprotect(byte[] protectedBytes)
            {
                return Transform(protectedBytes, false);
            }

            private static byte[] Transform(byte[] input, bool encrypt)
            {
                Blob source = new Blob(), result = new Blob();
                source.Data = Marshal.AllocHGlobal(input.Length);
                try
                {
                    Marshal.Copy(input, 0, source.Data, input.Length);
                    source.Size = input.Length;
                    bool ok = encrypt
                        ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out result)
                        : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out result);
                    if (!ok)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        throw new CryptographicException("DPAPI " + (encrypt ? "CryptProtectData" : "CryptUnprotectData") + " 失败，Win32 错误 " + error + "。");
                    }
                    byte[] output = new byte[result.Size];
                    Marshal.Copy(result.Data, output, 0, result.Size);
                    return output;
                }
                finally
                {
                    Marshal.FreeHGlobal(source.Data);
                    if (result.Data != IntPtr.Zero) LocalFree(result.Data);
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct Blob { public int Size; public IntPtr Data; }

            [DllImport("crypt32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

            [DllImport("crypt32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool LocalFree(IntPtr memory);
        }
    }

    internal sealed class AgentClient
    {
        // One handler for the whole process: the endpoint is per-request, and sockets stay warm.
        // No proxy discovery, matching the previous behaviour; the deployment decides reachability.
        private static readonly HttpClient Http = CreateClient();
        private readonly AgentOptions options;
        public AgentClient(AgentOptions options) { this.options = options; }

        private static HttpClient CreateClient()
        {
            HttpClientHandler handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        }

        // 只通过 onDelta 回报内容；调用方不需要完整副本，避免长回答在内存里留两份。
        public void Ask(string prompt, CancellationToken token, Action<string> onDelta)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + options.ApiKey);
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                request.Content = new StringContent(Payload(prompt), new UTF8Encoding(false), "application/json");
                // ResponseHeadersRead: without it the handler buffers the whole SSE body before we see a delta.
                using (HttpResponseMessage response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult())
                {
                    if ((int)response.StatusCode >= 400) throw new InvalidDataException("Agent 返回 HTTP " + (int)response.StatusCode + "。");
                    string type = response.Content.Headers.ContentType == null ? "" : response.Content.Headers.ContentType.ToString();
                    using (Stream output = response.Content.ReadAsStream(token))
                    using (StreamReader reader = new StreamReader(output, new UTF8Encoding(false)))
                    {
                        if (type.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0) ReadStream(reader, onDelta, token);
                        else ReadWhole(reader.ReadToEnd(), onDelta);
                    }
                }
            }
        }

        private string Payload(string prompt)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"model\":").Append(AgentJson.Quote(options.Model)).Append(",\"stream\":").Append(options.Stream ? "true" : "false").Append(",\"messages\":[");
            if (options.Prompt.Length > 0) json.Append("{\"role\":\"system\",\"content\":").Append(AgentJson.Quote(options.Prompt)).Append("},");
            return json.Append("{\"role\":\"user\",\"content\":").Append(AgentJson.Quote(prompt)).Append("}]}").ToString();
        }

        private void ReadStream(StreamReader reader, Action<string> onDelta, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                string line = reader.ReadLine();
                if (line == null) break;
                if (line.Length == 0) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string payload = line.Substring(5).Trim();
                if (payload.Length == 0 || payload == "[DONE]") { if (payload.Length == 0) continue; break; }
                string delta;
                try { delta = Delta(payload); }
                catch (InvalidDataException) { continue; }   // ignore keep-alive and comment frames
                if (String.IsNullOrEmpty(delta)) continue;
                onDelta(delta);
            }
        }

        private static string Delta(string json)
        {
            object root = AgentJson.Parse(json);
            string error = AgentJson.Text(root, "error", "message");
            if (error != null) throw new InvalidDataException("Agent 返回错误：" + error);
            return AgentJson.Text(root, "choices", "0", "delta", "content");
        }

        private static void ReadWhole(string json, Action<string> onDelta)
        {
            object root = AgentJson.Parse(json);
            string error = AgentJson.Text(root, "error", "message");
            if (error != null) throw new InvalidDataException("Agent 返回错误：" + error);
            string content = AgentJson.Text(root, "choices", "0", "message", "content");
            if (content == null) throw new InvalidDataException("Agent 返回的内容无法识别。");
            onDelta(content);
        }
    }

    // Joins the room as an ordinary local client so the service keeps applying every existing limit:
    // message length, 8-per-10s burst, participant cap, nickname uniqueness, history and online count.
    public sealed class AgentBridge : IDisposable
    {
        private readonly AgentOptions options;
        private readonly ChatOptions chat;
        private readonly AgentClient client;
        private readonly int port;
        private readonly string host, code, fingerprint;
        private readonly object gate = new object();
        private readonly Queue<ChatPacket> questions = new Queue<ChatPacket>();
        private readonly BlockingCollection<string> outbox = new BlockingCollection<string>(OutboxLimit);
        private readonly Queue<DateTime> sent = new Queue<DateTime>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Timer retry, pump;
        private volatile string status = "未启动";
        private ChatConnection connection;
        private string pending;
        private int disposed, joining, inflight, sending;
        private DateTime nextAttempt = DateTime.MinValue;
        private int backoff = 2;
        private const int OutboxLimit = 64;
        public string Status { get { return status; } }

        // host is the address the service was told to listen on, exactly as the server window picked
        // it. Reachable below maps a wildcard bind to loopback, which is what such a bind accepts.
        public AgentBridge(AgentOptions options, string host, int port, string code, string fingerprint, ChatOptions chat = null)
        {
            this.options = options; this.host = Reachable(host); this.port = port; this.code = code; this.fingerprint = fingerprint;
            this.chat = chat ?? new ChatOptions(); client = new AgentClient(options);
            retry = new Timer(delegate { Reconnect(); }, null, 1500, 2000);
            pump = new Timer(delegate { Flush(); }, null, 400, 250);
        }

        // The service binds exactly one address, and a bind to a specific address does not answer on
        // loopback, so the bridge has to dial back that same address. A wildcard bind is the exception:
        // it accepts loopback connections but is not itself a connectable destination.
        private static string Reachable(string host)
        {
            string trimmed = (host ?? "").Trim();
            IPAddress parsed;
            if (trimmed.Length == 0 || !IPAddress.TryParse(trimmed, out parsed)) return "127.0.0.1";
            return parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any) ? "127.0.0.1" : trimmed;
        }

        private void Reconnect()
        {
            if (Volatile.Read(ref disposed) != 0 || Interlocked.CompareExchange(ref joining, 1, 0) != 0) return;
            Task.Run(delegate
            {
                try { Connect(); }
                catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
                finally { Volatile.Write(ref joining, 0); }
            });
        }

        private void Connect()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (connection != null)
            {
                if (connection.Connected) { backoff = 2; return; }
                connection.Dispose(); connection = null;
            }
            if (DateTime.UtcNow < nextAttempt) return;
            ChatConnection candidate = new ChatConnection(chat);
            candidate.Received += delegate(ChatPacket packet) { Enqueue(packet); };
            candidate.Dropped += delegate(string message) { status = "已掉线，正在重连"; nextAttempt = DateTime.MinValue; };
            connection = candidate;
            // Dispose 可能在上面的检查之后运行；赋值后再确认一次，避免留下幽灵连接。
            if (Volatile.Read(ref disposed) != 0) { candidate.Dispose(); connection = null; return; }
            try
            {
                candidate.ConnectAsync(host, port, options.Nick, code, fingerprint).GetAwaiter().GetResult();
                backoff = 2; status = "已加入房间";
            }
            catch (Exception ex)
            {
                if (!ChatWire.Expected(ex)) throw;
                // 连接期间已经 Dispose：关闭 candidate，不要留下接收循环和心跳。
                if (Volatile.Read(ref disposed) != 0) { candidate.Dispose(); if (ReferenceEquals(connection, candidate)) connection = null; return; }
                status = "未加入房间：" + ex.Message;
                nextAttempt = DateTime.UtcNow.AddSeconds(backoff);
                backoff = Math.Min(60, backoff * 2);   // the service rate-limits 12 attempts per minute per address
            }
        }

        private void Enqueue(ChatPacket packet)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (packet.Kind != "message") return;
            if (String.Equals(packet.Nick, options.Nick, StringComparison.OrdinalIgnoreCase)) return;   // never answer itself, or the room loops forever
            if (options.MentionOnly && (packet.Text ?? "").IndexOf("@" + options.Nick, StringComparison.OrdinalIgnoreCase) < 0) return;
            lock (gate)
            {
                if (questions.Count >= options.QuestionLimit) return;
                questions.Enqueue(packet);
            }
            Pump();
        }

        private void Pump()
        {
            if (Volatile.Read(ref disposed) != 0 || Interlocked.CompareExchange(ref inflight, 1, 0) != 0) return;
            ChatPacket question;
            lock (gate)
            {
                if (questions.Count == 0) { Volatile.Write(ref inflight, 0); return; }
                question = questions.Dequeue();
            }
            Task.Run(delegate
            {
                try { Answer(question); }
                catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
                finally
                {
                    Volatile.Write(ref inflight, 0);
                    lock (gate) { if (questions.Count > 0) Pump(); }
                }
            });
        }

        // One reply at a time: two simultaneous questions would interleave their chunks in the room.
        private void Answer(ChatPacket question)
        {
            status = "正在回答 " + question.Nick;
            CancellationToken token = cancellation.Token;
            AgentChunker chunker = new AgentChunker(options.ChunkSize);
            List<string> ready = new List<string>();
            int total = 0; bool truncated = false;
            try
            {
                client.Ask(question.Nick + "：" + question.Text, token, delegate(string delta)
                {
                    // 非流式端点会把整段回答一次送来；按剩余额度截断后继续分片，
                    // 不能整段丢弃，否则 stream:false 的端点会完全没有回答。
                    int remaining = options.ReplyLimit - total;
                    if (remaining <= 0) { truncated = true; return; }
                    if (delta.Length > remaining)
                    {
                        delta = delta.Substring(0, remaining);
                        if (Char.IsHighSurrogate(delta[^1])) delta = delta[..^1];
                        truncated = true;
                        if (delta.Length == 0) return;
                    }
                    total += delta.Length;
                    ready.Clear(); chunker.Add(delta, ready);
                    foreach (string chunk in ready) if (!outbox.TryAdd(chunk)) truncated = true;
                });
                string rest = chunker.Flush();
                if (rest.Length > 0 && !outbox.TryAdd(rest)) truncated = true;
                if (truncated) outbox.TryAdd("（回答较长，已截断）");
                status = "已加入房间";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { status = "已停止"; }
            // HttpClient reports its own Timeout as a cancellation, so this must be told apart from a stop.
            catch (TaskCanceledException ex) { status = "请求超时：" + ex.Message; outbox.TryAdd("（助手响应超时）"); }
            catch (HttpRequestException ex) { status = "请求失败：" + ex.Message; outbox.TryAdd("（连接不上助手服务）"); }
            catch (Exception ex)
            {
                if (!ChatWire.Expected(ex)) throw;
                status = "回答失败：" + ex.Message; outbox.TryAdd("（助手回答失败）");
            }
        }

        private void Flush()
        {
            if (Volatile.Read(ref disposed) != 0 || Interlocked.CompareExchange(ref sending, 1, 0) != 0) return;
            Task.Run(delegate
            {
                try { Send(); }
                catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
                finally { Volatile.Write(ref sending, 0); }
            });
        }

        private void Send()
        {
            DateTime now = DateTime.UtcNow;
            while (sent.Count > 0 && (now - sent.Peek()).TotalSeconds >= 10) sent.Dequeue();
            while (sent.Count < options.SendBurst)
            {
                // 断线时把没发出去的分片暂存在 pending，下一轮优先补发；
                // 不能放回 outbox 队尾，否则分片顺序会被打乱。
                string chunk;
                if (pending != null) { chunk = pending; pending = null; }
                else if (!outbox.TryTake(out chunk)) break;
                ChatConnection active = connection;
                if (chunk.Length == 0) continue;
                if (active == null || !active.Connected) { pending = chunk; break; }
                active.SendAsync(chunk).GetAwaiter().GetResult();
                sent.Enqueue(DateTime.UtcNow); now = DateTime.UtcNow;
                while (sent.Count > 0 && (now - sent.Peek()).TotalSeconds >= 10) sent.Dequeue();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            retry.Dispose(); pump.Dispose();
            cancellation.Cancel();
            if (connection != null) connection.Dispose();
            string discarded; while (outbox.TryTake(out discarded)) { }
            outbox.Dispose();
            // The token source is deliberately left undisposed: a blocked HTTP read may still hold its
            // registration, and disposing it here races with that callback.
        }
    }
}
