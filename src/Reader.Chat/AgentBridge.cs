// Reader.Chat 的助手桥接实现：解析 OpenAI 兼容端点返回的 JSON/SSE，把长回答切分成房间消息，
// 并以普通客户端身份加入聊天室完成提问与回答的收发。
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
    /// <summary>端点主动返回的错误；与无法解析的帧区分，避免在流式读取里被当成 keep-alive 吞掉。</summary>
    internal sealed class AgentResponseException : Exception
    {
        public AgentResponseException(string message) : base(message) { }
    }

    // Minimal JSON reader for agent replies. Retained from the 1.5 line deliberately: the shapes we
    // read are three fixed paths, and System.Text.Json would pull a full DOM in to walk them.
    /// <summary>面向 Agent 响应的极简 JSON 解析器，只覆盖响应中固定的三条取值路径。</summary>
    /// <remarks>端点响应属于外部输入：解析失败统一抛出 <see cref="InvalidDataException"/>，由调用方决定降级或报错。</remarks>
    internal static class AgentJson
    {
        /// <summary>解析完整的 JSON 文本。</summary>
        /// <param name="text">待解析的 JSON 文本。</param>
        /// <returns>对象、数组、字符串、数字、布尔值或 null。</returns>
        /// <exception cref="InvalidDataException">内容为空、格式错误或末尾存在多余内容。</exception>
        public static object Parse(string text)
        {
            if (text == null) throw new InvalidDataException("JSON 内容为空。");
            int index = 0;
            object value = ParseValue(text, ref index, 0);
            Skip(text, ref index);
            // 严格消费全部输入，防止把 "{}garbage" 之类的响应误认为成功。
            if (index != text.Length) throw new InvalidDataException("JSON 末尾有多余内容。");
            return value;
        }

        // Walks nested objects and arrays; numeric path segments index into arrays.
        /// <summary>按路径逐层读取成员。</summary>
        /// <param name="node">起始节点。</param>
        /// <param name="path">属性名序列；数字段表示数组下标。</param>
        /// <returns>路径终点的值；任一层缺失或类型不符时返回 null。</returns>
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

        /// <summary>按路径读取字符串成员。</summary>
        /// <param name="node">起始节点。</param>
        /// <param name="path">属性名序列；数字段表示数组下标。</param>
        /// <returns>终点为字符串时返回其值，否则返回 null。</returns>
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
                // 重复键按后出现的值覆盖；协议不依赖重复键，这里只保证行为确定。
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
                // 只接受 JSON 标准转义；未知转义视为协议错误，避免静默放行。
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

        /// <summary>把字符串编码为 JSON 字面量。</summary>
        /// <param name="value">原始字符串；null 视为空串。</param>
        /// <returns>带引号并完成转义的 JSON 字符串。</returns>
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

    /// <summary>助手回答的文本切分工具。</summary>
    internal static class AgentText
    {
        private const string Breaks = "。！？；\n";

        // Longest cut at or below limit that lands after a sentence or line break, never splitting a surrogate pair.
        /// <summary>在不超过上限的前提下选择切分位置，并避免拆开代理对。</summary>
        /// <param name="text">待切分文本。</param>
        /// <param name="limit">允许的最大字符数。</param>
        /// <returns>切分位置；始终大于等于 1。</returns>
        public static int Cut(string text, int limit)
        {
            if (text.Length <= limit) return text.Length;
            // 最多回退到上限的一半，避免为了找句号把首块切得过短。
            int floor = Math.Max(1, limit / 2);
            int cut = limit;
            for (int i = limit - 1; i >= floor; i--) if (Breaks.IndexOf(text[i]) >= 0) { cut = i + 1; break; }
            // 代理对由两个 char 组成，从中间切开会产生非法 UTF-16，向后退一位。
            if (cut > 0 && cut < text.Length && Char.IsHighSurrogate(text[cut - 1]) && Char.IsLowSurrogate(text[cut])) cut--;
            return Math.Max(1, cut);
        }
    }

    // Turns a stream of deltas into room-sized messages. Pure buffering: no I/O, no throttling.
    /// <summary>把流式增量累积并切分成房间消息大小的块。</summary>
    /// <remarks>只做缓冲与切分，不涉及 I/O 或节流；调用方负责发送。</remarks>
    internal sealed class AgentChunker
    {
        private readonly int limit;
        private readonly StringBuilder buffer = new StringBuilder();
        /// <summary>创建分片器。</summary>
        /// <param name="limit">每块的最大字符数。</param>
        public AgentChunker(int limit) { this.limit = limit; }

        /// <summary>追加一段增量，并把已够长的完整块写入 <paramref name="output"/>。</summary>
        /// <param name="delta">新到达的文本；为空时忽略。</param>
        /// <param name="output">接收切分结果的列表，由调用方提供。</param>
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
                // Trim 后可能只剩空白，丢弃以免发出空消息。
                if (chunk.Length > 0) output.Add(chunk);
            }
        }

        /// <summary>取出并清空剩余缓冲。</summary>
        /// <returns>去除首尾空白后的剩余文本；可能为空串。</returns>
        public string Flush()
        {
            string rest = buffer.ToString().Trim();
            buffer.Length = 0;
            return rest;
        }
    }

    /// <summary>Agent 桥接的运行参数；构造时完成归一化与范围校验。</summary>
    /// <remarks>实例不可变。配置错误在构造阶段即抛出 <see cref="ArgumentException"/>，不会延迟到首次请求。</remarks>
    public sealed class AgentOptions
    {
        /// <summary>端点地址、API 密钥、模型名、昵称和角色设定。</summary>
        public readonly string Endpoint, ApiKey, Model, Nick, Prompt;
        /// <summary>是否只响应 @ 提及自己的消息；是否使用流式响应。</summary>
        public readonly bool MentionOnly, Stream;
        /// <summary>单次回答字符上限、消息分块大小、待处理提问上限和 10 秒发送条数上限。</summary>
        public readonly int ReplyLimit, ChunkSize, QuestionLimit, SendBurst;
        /// <summary>创建并校验 Agent 桥接参数。</summary>
        /// <param name="endpoint">OpenAI 兼容端点；必须是完整的 http(s) 绝对地址。</param>
        /// <param name="apiKey">端点密钥，1～512 个字符。</param>
        /// <param name="model">模型名，1～128 个字符。</param>
        /// <param name="nick">房间内使用的昵称，1～20 个字符且不能包含 @。</param>
        /// <param name="prompt">系统角色设定，最长 4000 个字符。</param>
        /// <param name="mentionOnly">为 true 时只回答 @ 提及自己的消息。</param>
        /// <param name="stream">为 true 时请求并解析 SSE 流式响应。</param>
        /// <param name="replyLimit">单次回答最多转发的字符数，范围 [chunkSize, 20000]。</param>
        /// <param name="chunkSize">每条房间消息的最大字符数，范围 50～500。</param>
        /// <param name="questionLimit">待处理提问队列上限，范围 1～32。</param>
        /// <param name="sendBurst">10 秒内最多发送的房间消息条数，范围 1～7。</param>
        /// <exception cref="ArgumentException">任一参数不满足上述约束。</exception>
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
            // 昵称含 @ 会让 “@昵称” 的提及识别产生歧义，因此直接禁止。
            if (Nick.IndexOf('@') >= 0) throw new ArgumentException("Agent 昵称不能包含 @。");
            if (Prompt.Length > 4000) throw new ArgumentException("Agent 角色设定不能超过 4000 个字符。");
            if (chunkSize < 50 || chunkSize > 500) throw new ArgumentException("分块大小需要在 50～500 之间。");
            // 回答上限不能小于一个分块，否则单块消息永远无法发出。
            if (replyLimit < chunkSize || replyLimit > 20000) throw new ArgumentException("单次回答上限需要在 " + chunkSize + "～20000 之间。");
            if (questionLimit < 1 || questionLimit > 32) throw new ArgumentException("待处理提问上限需要在 1～32 之间。");
            // The room service rejects a burst of 8 per 10s; the bridge must stay below that on its own.
            if (sendBurst < 1 || sendBurst > 7) throw new ArgumentException("发送节奏需要在 1～7 之间，留出服务端限流的余量。");
            Endpoint = endpoint; MentionOnly = mentionOnly; Stream = stream;
            ReplyLimit = replyLimit; ChunkSize = chunkSize; QuestionLimit = questionLimit; SendBurst = sendBurst;
        }
    }

    /// <summary>Agent 配置的 XML 持久化；Windows 上使用 DPAPI 保护密钥。</summary>
    public static class AgentConfig
    {
        // The caller supplies the path: the data directory belongs to Reader.Core, and this library
        // deliberately does not reference it just to re-derive a folder name.
        /// <summary>把配置写入指定路径。</summary>
        /// <param name="path">配置文件的完整路径；父目录不存在时自动创建。</param>
        /// <param name="options">待保存的参数。</param>
        /// <remarks>先写临时文件再替换目标，避免进程中断留下半写文件；非 Windows 上把权限收紧为仅属主可读写。</remarks>
        public static void Save(string path, AgentOptions options)
        {
            // 根节点带版本号，便于将来迁移格式；密钥由 Secret 单独处理。
            XElement root = new XElement("agent", new XAttribute("version", "1"),
                new XElement("endpoint", options.Endpoint), new XElement("model", options.Model), new XElement("nick", options.Nick),
                new XElement("mentionOnly", options.MentionOnly ? "true" : "false"), new XElement("stream", options.Stream ? "true" : "false"),
                new XElement("replyLimit", options.ReplyLimit), new XElement("chunkSize", options.ChunkSize),
                new XElement("prompt", options.Prompt), Secret(options.ApiKey));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string target = path, temp = target + ".tmp";
            File.WriteAllText(temp, root.ToString(), new UTF8Encoding(false));
            // 临时文件与目标同目录，替换时才能保持原文件的原子性与权限。
            if (File.Exists(target)) File.Replace(temp, target, target + ".bak", true);
            else File.Move(temp, target);
            // Without DPAPI the key is only as safe as the file, so at least keep it owner-only.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        /// <summary>读取配置文件。</summary>
        /// <param name="path">配置文件的完整路径。</param>
        /// <returns>解析后的参数；文件不存在或内容无法使用时返回 null。</returns>
        /// <remarks>XML 解析禁用 DTD 与外部解析器，并限制文档大小；Windows 上的密钥经 DPAPI 解密。</remarks>
        public static AgentOptions Load(string path)
        {
            // 配置不存在不是错误，调用方会以“未配置”状态继续运行。
            if (!File.Exists(path)) return null;
            try
            {
                XElement root;
                // 配置是本地文件但仍按不可信输入处理：禁用 DTD/外部解析器，限制 4 MiB 防止内存膨胀。
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (XmlReader xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 }))
                    root = XElement.Load(xml);
                // 根元素名不符说明是旧格式或无关文件，按“无配置”处理。
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

        // 缺失或非法数字回退到默认值，保证旧版本配置仍可加载。
        private static int Number(XElement root, string name, int fallback)
        {
            int value;
            return Int32.TryParse((string)root.Element(name), out value) ? value : fallback;
        }

        // On Windows the key is stored per user through DPAPI. Other platforms have no equivalent
        // wired up yet, so the value is marked unprotected rather than silently implying safety.
        /// <summary>把密钥编码为配置节点：Windows 用 DPAPI 保护，其他平台标记为未保护。</summary>
        /// <param name="value">明文密钥。</param>
        /// <returns>包含 protected 属性与（可能的）密文的 apiKey 元素。</returns>
        private static XElement Secret(string value)
        {
            if (!OperatingSystem.IsWindows()) return new XElement("apiKey", new XAttribute("protected", "false"), value ?? "");
            byte[] plain = Encoding.UTF8.GetBytes(value ?? "");
            // 明文副本只用于加密，完成后立即清零，减少密钥在内存中的驻留时间。
            try { return new XElement("apiKey", new XAttribute("protected", "true"), Convert.ToBase64String(Dpapi.Protect(plain))); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        // 与 Secret 对称：只有标记 protected="true" 时才走 DPAPI，未保护的旧配置按明文读取。
        private static string Reveal(XElement root)
        {
            XElement key = root.Element("apiKey");
            if (key == null) return "";
            if ((string)key.Attribute("protected") != "true") return key.Value;
            byte[] plain = Dpapi.Unprotect(Convert.FromBase64String(key.Value));
            // 解密后的明文只在构造字符串时短暂存在，随后清零。
            try { return Encoding.UTF8.GetString(plain); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        // crypt32 directly instead of the System.Security.Cryptography.ProtectedData package:
        // it keeps the chat library dependency-free, matching how the platform projects already interop.
        /// <summary>直接 P/Invoke crypt32 的 DPAPI 封装。</summary>
        /// <remarks>避免为聊天库引入额外的 ProtectedData 依赖；调用线程必须能访问当前用户的密钥材料。</remarks>
        private static class Dpapi
        {
            // CRYPTPROTECT_UI_FORBIDDEN：禁止弹出 UI，后台线程调用时不会卡住等待用户交互。
            private const int UiForbidden = 0x1;

            /// <summary>用当前用户的 DPAPI 主密钥加密数据。</summary>
            /// <param name="plain">明文。</param>
            /// <returns>DPAPI 密文。</returns>
            /// <exception cref="CryptographicException">加密失败。</exception>
            public static byte[] Protect(byte[] plain)
            {
                return Transform(plain, true);
            }

            /// <summary>用当前用户的 DPAPI 主密钥解密数据。</summary>
            /// <param name="protectedBytes">DPAPI 密文。</param>
            /// <returns>明文。</returns>
            /// <exception cref="CryptographicException">解密失败，或密文不属于当前用户。</exception>
            public static byte[] Unprotect(byte[] protectedBytes)
            {
                return Transform(protectedBytes, false);
            }

            private static byte[] Transform(byte[] input, bool encrypt)
            {
                Blob source = new Blob(), result = new Blob();
                // DPAPI 只接受非托管缓冲区，因此先把输入复制到 HGlobal。
                source.Data = Marshal.AllocHGlobal(input.Length);
                try
                {
                    Marshal.Copy(input, 0, source.Data, input.Length);
                    source.Size = input.Length;
                    // 两个入口共用 UiForbidden 标志；SetLastError=true 让失败时可以取到具体错误码。
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
                    // 源缓冲区由 AllocHGlobal 分配；输出缓冲区由 DPAPI 通过 LocalAlloc 分配，只能用 LocalFree 释放。
                    Marshal.FreeHGlobal(source.Data);
                    if (result.Data != IntPtr.Zero) LocalFree(result.Data);
                }
            }

            // DATA_BLOB：Size 为字节数，Data 指向非托管缓冲区，布局必须与 crypt32 的定义一致。
            [StructLayout(LayoutKind.Sequential)]
            private struct Blob { public int Size; public IntPtr Data; }

            // description/entropy/prompt/reserved 均为可选，这里不需要描述文本或附加熵。
            [DllImport("crypt32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

            [DllImport("crypt32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

            // DPAPI 的输出缓冲区由 LocalAlloc 分配，必须用 LocalFree 而不是 FreeHGlobal 释放。
            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool LocalFree(IntPtr memory);
        }
    }

    /// <summary>OpenAI 兼容端点的最小客户端，支持 SSE 流式与整段 JSON 两种响应。</summary>
    /// <remarks>同步调用：<see cref="Ask"/> 会阻塞到响应结束，增量通过回调交付。
    /// 共享的 <see cref="HttpClient"/> 不发现代理、不自动重定向、不保存 Cookie。</remarks>
    internal sealed class AgentClient
    {
        // One handler for the whole process: the endpoint is per-request, and sockets stay warm.
        // No proxy discovery, matching the previous behaviour; the deployment decides reachability.
        private static readonly HttpClient Http = CreateClient();
        private readonly AgentOptions options;
        /// <summary>使用给定配置创建客户端；实际网络请求在调用 <see cref="Ask"/> 时发生。</summary>
        /// <param name="options">已校验的端点参数。</param>
        public AgentClient(AgentOptions options) { this.options = options; }

        private static HttpClient CreateClient()
        {
            // 关闭重定向与 Cookie：降低密钥被转发到其他主机的风险，也避免共享 Cookie 状态；
            // 关闭代理发现，是否可达由部署环境决定。60 秒超时覆盖大多数慢速模型响应。
            HttpClientHandler handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        }

        // 只通过 onDelta 回报内容；调用方不需要完整副本，避免长回答在内存里留两份。
        /// <summary>发送提问并把回答增量交给回调。</summary>
        /// <param name="prompt">用户提问文本。</param>
        /// <param name="token">取消令牌；取消时抛出 <see cref="OperationCanceledException"/>。</param>
        /// <param name="onDelta">每段回答的回调，在本方法所在线程同步触发。</param>
        /// <remarks>阻塞直到响应结束。响应为 SSE 时逐段回调，否则整段回调一次。</remarks>
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
            // 手工拼接请求体：字段固定，流式与非流式共用；Prompt 为空时省略 system 消息。
            StringBuilder json = new StringBuilder();
            json.Append("{\"model\":").Append(AgentJson.Quote(options.Model)).Append(",\"stream\":").Append(options.Stream ? "true" : "false").Append(",\"messages\":[");
            if (options.Prompt.Length > 0) json.Append("{\"role\":\"system\",\"content\":").Append(AgentJson.Quote(options.Prompt)).Append("},");
            return json.Append("{\"role\":\"user\",\"content\":").Append(AgentJson.Quote(prompt)).Append("}]}").ToString();
        }

        // 逐行解析 SSE：只处理 data: 行，event/id/retry 字段和注释行一律忽略。
        private void ReadStream(StreamReader reader, Action<string> onDelta, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                string line = reader.ReadLine();
                if (line == null) break;
                if (line.Length == 0) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                string payload = line.Substring(5).Trim();
                // 空 data 行是保活；[DONE] 表示流结束。
                if (payload.Length == 0 || payload == "[DONE]") { if (payload.Length == 0) continue; break; }
                string delta;
                try { delta = Delta(payload); }
                // 端点主动报告的错误必须抛出；只有无法解析的 data 帧才继续读下一行。
                catch (AgentResponseException) { throw; }
                catch (InvalidDataException) { continue; }
                if (String.IsNullOrEmpty(delta)) continue;
                onDelta(delta);
            }
        }

        // 端点可能用 error.message 报告失败；只有 choices[0].delta.content 才是正文增量。
        private static string Delta(string json)
        {
            object root = AgentJson.Parse(json);
            string error = AgentJson.Text(root, "error", "message");
            if (error != null) throw new AgentResponseException("Agent 返回错误：" + error);
            return AgentJson.Text(root, "choices", "0", "delta", "content");
        }

        // 非流式响应只有一份完整内容；缺少 choices[0].message.content 视为协议错误。
        private static void ReadWhole(string json, Action<string> onDelta)
        {
            object root = AgentJson.Parse(json);
            string error = AgentJson.Text(root, "error", "message");
            if (error != null) throw new AgentResponseException("Agent 返回错误：" + error);
            string content = AgentJson.Text(root, "choices", "0", "message", "content");
            if (content == null) throw new InvalidDataException("Agent 返回的内容无法识别。");
            onDelta(content);
        }
    }

    // Joins the room as an ordinary local client so the service keeps applying every existing limit:
    // message length, 8-per-10s burst, participant cap, nickname uniqueness, history and online count.
    /// <summary>把聊天室提问转发给 Agent，并把回答分片发回房间的桥接器。</summary>
    /// <remarks>以普通客户端身份加入房间，因此服务端的长度、频率、人数、昵称等限制仍然生效。
    /// 状态字段与队列使用锁或原子操作保护；<see cref="Dispose"/> 可重复调用。</remarks>
    public sealed class AgentBridge : IDisposable
    {
        private readonly AgentOptions options;
        private readonly ChatOptions chat;
        private readonly AgentClient client;
        private readonly int port;
        private readonly string host, code, fingerprint;
        // gate 保护 questions 与发送状态；outbox 自带同步；sent 只由串行化的 Send 访问。
        private readonly object gate = new object();
        private readonly Queue<ChatPacket> questions = new Queue<ChatPacket>();
        // 有界出站队列：Agent 产出快于网络发送时限制内存占用，满时由调用方标记截断。
        private readonly BlockingCollection<string> outbox = new BlockingCollection<string>(OutboxLimit);
        private readonly Queue<DateTime> sent = new Queue<DateTime>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Timer retry, pump;
        private volatile string status = "未启动";
        private ChatConnection connection;
        // 断线时暂存的单个分片；保留到下一轮优先补发，避免回到队尾打乱顺序。
        private string pending;
        // joining/inflight/sending 分别串行化建连、回答和发送，disposed 是幂等释放标志。
        private int disposed, joining, inflight, sending;
        // 重连退避：失败后按指数增长（上限 60 秒），成功后重置为 2 秒。
        private DateTime nextAttempt = DateTime.MinValue;
        private int backoff = 2;
        private const int OutboxLimit = 64;
        /// <summary>当前桥接状态的简短中文描述，供界面展示。</summary>
        public string Status { get { return status; } }

        // host is the address the service was told to listen on, exactly as the server window picked
        // it. Reachable below maps a wildcard bind to loopback, which is what such a bind accepts.
        /// <summary>创建桥接器并启动重连与发送定时器。</summary>
        /// <param name="options">Agent 端点与回答参数。</param>
        /// <param name="host">服务端监听地址；通配地址会映射为回环地址。</param>
        /// <param name="port">服务端端口。</param>
        /// <param name="code">房间码。</param>
        /// <param name="fingerprint">服务端证书指纹。</param>
        /// <param name="chat">聊天容量参数；为 null 时使用默认值。</param>
        public AgentBridge(AgentOptions options, string host, int port, string code, string fingerprint, ChatOptions chat = null)
        {
            this.options = options; this.host = Reachable(host); this.port = port; this.code = code; this.fingerprint = fingerprint;
            this.chat = chat ?? new ChatOptions(); client = new AgentClient(options);
            // 重连在 1.5 秒后首次尝试，之后每 2 秒检查一次；发送每 250 毫秒尝试排空出站队列。
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

        // 定时器回调：joining 保证同一时刻只有一个连接任务，避免定时器重叠触发并发建连。
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
                // 已有连接仍在线时直接复用；已断开则释放后重建。
                if (connection.Connected) { backoff = 2; return; }
                connection.Dispose(); connection = null;
            }
            // 退避时间未到就跳过本轮，避免定时器触发紧密重试。
            if (DateTime.UtcNow < nextAttempt) return;
            ChatConnection candidate = new ChatConnection(chat);
            // 收到消息进入待处理队列；掉线时清空退避，让下一轮立即重连。
            candidate.Received += delegate(ChatPacket packet) { Enqueue(packet); };
            candidate.Dropped += delegate(string message) { status = "已掉线，正在重连"; nextAttempt = DateTime.MinValue; };
            connection = candidate;
            // Dispose 可能在上面的检查之后运行；赋值后再确认一次，避免留下幽灵连接。
            if (Volatile.Read(ref disposed) != 0) { candidate.Dispose(); connection = null; return; }
            try
            {
                // 在 Task.Run 线程上同步等待握手完成，不会阻塞定时器线程。
                candidate.ConnectAsync(host, port, options.Nick, code, fingerprint).GetAwaiter().GetResult();
                backoff = 2; status = "已加入房间";
            }
            catch (Exception ex)
            {
                if (!ChatWire.Expected(ex)) throw;
                // 连接期间已经 Dispose：关闭 candidate，不要留下接收循环和心跳。
                if (Volatile.Read(ref disposed) != 0) { candidate.Dispose(); if (ReferenceEquals(connection, candidate)) connection = null; return; }
                status = "未加入房间：" + ex.Message;
                // 指数退避，上限 60 秒；退避期间 Send 会把分片留在 outbox/pending 中保持顺序。
                nextAttempt = DateTime.UtcNow.AddSeconds(backoff);
                backoff = Math.Min(60, backoff * 2);   // the service rate-limits 12 attempts per minute per address
            }
        }

        private void Enqueue(ChatPacket packet)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            // 只处理普通消息；状态帧、历史回放等不进入提问队列。
            if (packet.Kind != "message") return;
            if (String.Equals(packet.Nick, options.Nick, StringComparison.OrdinalIgnoreCase)) return;   // never answer itself, or the room loops forever
            if (options.MentionOnly && (packet.Text ?? "").IndexOf("@" + options.Nick, StringComparison.OrdinalIgnoreCase) < 0) return;
            lock (gate)
            {
                // 队列有界：满时丢弃新问题，保持已排队问题的顺序与响应性。
                if (questions.Count >= options.QuestionLimit) return;
                questions.Enqueue(packet);
            }
            Pump();
        }

        private void Pump()
        {
            // inflight 保证同一时刻只有一个回答任务，避免两次回答的分片在房间里交错。
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
                    // 回答结束后释放标志；若还有排队问题则继续泵出下一个。
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
            // 每轮回答使用独立的分片器与计数；ready 复用列表以减少流式回调中的分配。
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
                        // 截断点可能落在代理对中间，回退一个 char 避免产生非法 UTF-16。
                        if (Char.IsHighSurrogate(delta[^1])) delta = delta[..^1];
                        truncated = true;
                        if (delta.Length == 0) return;
                    }
                    total += delta.Length;
                    ready.Clear(); chunker.Add(delta, ready);
                    // outbox 有界：TryAdd 失败说明队列已满，最终统一补一条截断提示。
                    foreach (string chunk in ready) if (!outbox.TryAdd(chunk)) truncated = true;
                });
                // 处理最后不足一块的剩余文本，再统一追加截断提示。
                string rest = chunker.Flush();
                if (rest.Length > 0 && !outbox.TryAdd(rest)) truncated = true;
                if (truncated) outbox.TryAdd("（回答较长，已截断）");
                status = "已加入房间";
            }
            // 端点明确报错与网络失败使用不同提示，便于用户区分配置问题和网络问题。
            catch (AgentResponseException ex) { status = "回答失败：" + ex.Message; outbox.TryAdd("（助手回答失败）"); }
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

        // sending 串行化发送任务，定时器回调不会重叠执行。
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
            // 滑动窗口：只统计最近 10 秒的发送记录，窗口内最多 SendBurst 条，避免触发服务端限流。
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
                // 无连接时保留当前分片并结束本轮，已取出的分片不会丢失。
                if (active == null || !active.Connected) { pending = chunk; break; }
                // 同步等待发送完成；Send 运行在独立任务中，不会阻塞定时器线程。
                active.SendAsync(chunk).GetAwaiter().GetResult();
                sent.Enqueue(DateTime.UtcNow); now = DateTime.UtcNow;
                while (sent.Count > 0 && (now - sent.Peek()).TotalSeconds >= 10) sent.Dequeue();
            }
        }

        /// <summary>停止定时器、取消进行中的请求并释放连接；可重复调用。</summary>
        /// <remarks>取消令牌源故意不释放：仍阻塞的 HTTP 读取可能持有其注册回调，提前释放会与其竞争。</remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            // 先停定时器再取消请求，避免释放过程中又有回调启动新任务。
            retry.Dispose(); pump.Dispose();
            cancellation.Cancel();
            if (connection != null) connection.Dispose();
            // 清空并释放出站队列；仍在进行的回答会收到 ObjectDisposedException，由预期异常分支处理。
            string discarded; while (outbox.TryTake(out discarded)) { }
            outbox.Dispose();
            // The token source is deliberately left undisposed: a blocked HTTP read may still hold its
            // registration, and disposing it here races with that callback.
        }
    }
}
