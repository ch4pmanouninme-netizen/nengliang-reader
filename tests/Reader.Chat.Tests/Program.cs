using Reader.Chat;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
async Task Wait(Func<bool> condition, string name, int seconds = 8)
{
    var clock = Stopwatch.StartNew();
    while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(20);
    Check(condition(), name);
}
async Task Reject(Func<Task> action, string name)
{
    bool rejected = false;
    try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or AuthenticationException or ArgumentException or System.Xml.XmlException) { rejected = true; }
    Check(rejected, name);
}
try
{
    string text = "中文 & <标签> 😊\n第二行";
    using (var memory = new MemoryStream())
    {
        ChatWire.Write(memory, new ChatPacket { Text = text, Kind = "send" }); memory.Position = 0;
        Check(ChatWire.Read(memory).Text == text, "Unicode XML roundtrip");
    }
    foreach (byte[] frame in new[] { new byte[] { 127, 255, 255, 255 }, new byte[] { 0, 0, 0, 8, 1 } })
        await Reject(() => { ChatWire.Read(new MemoryStream(frame)); return Task.CompletedTask; }, "invalid/truncated frame rejected");
    foreach (var xml in new[] { "<!DOCTYPE chat [<!ENTITY x SYSTEM 'file:///nope'>]><chat version='2'><text>&x;</text></chat>", "<chat version='1'/>" })
    {
        byte[] body = Encoding.UTF8.GetBytes(xml); using var raw = new MemoryStream();
        raw.Write(new byte[] { 0, 0, (byte)(body.Length >> 8), (byte)body.Length }); raw.Write(body); raw.Position = 0;
        await Reject(() => { ChatWire.Read(raw); return Task.CompletedTask; }, "DTD or old protocol rejected");
    }
    var inbox = new ChatInbox(); int epoch = inbox.Reset();
    bool accepted = true; for (int i = 0; i < ChatInbox.Capacity; i++) accepted &= inbox.Enqueue(epoch, new ChatPacket());
    Check(accepted, "bounded inbox accepts capacity entries");
    Check(!inbox.Enqueue(epoch, new ChatPacket()) && inbox.Drain().Single().Kind == "overflow", "overflow clears backlog and requests reconnect");
    int next = inbox.Reset(); Check(!inbox.Enqueue(epoch, new ChatPacket()) && inbox.Enqueue(next, new ChatPacket()), "old connection callbacks are ignored");
    const string room = "test-room-only";
    using var certificate = ChatSecurity.CreateCertificate();
    using var server = new ChatServer(room, new ChatOptions(historyLimit: 2, sendBurst: 3), certificate);
    server.Start(IPAddress.Loopback, 0);
    string pin = server.Fingerprint;
    var info = new ConnectionInfo("127.0.0.1", server.Port, room, pin);
    string version = typeof(ConnectionInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
    Check(info.ToString().Split('\n')[0] == "良子小窗-v" + version, "connection header follows application version without build metadata");
    Check(ConnectionInfo.Parse(info.ToString()) == info, "upstream connection text roundtrip");
    Check(ConnectionInfo.Parse(info.ToString().Replace("良子小窗-v" + version, "良子小窗 1.5")) == info, "legacy 1.5 connection text remains readable");
    await Reject(() => { ConnectionInfo.Parse("服务地址：127.0.0.1"); return Task.CompletedTask; }, "incomplete connection info rejected");
    using var alice = new ChatConnection(); using var bob = new ChatConnection();
    var received = new ConcurrentQueue<ChatPacket>(); bob.Received += received.Enqueue;
    await alice.ConnectAsync(info.Host, info.Port, "Alice", room, pin); await bob.ConnectAsync(info.Host, info.Port, "Bob", room, pin);
    Check(server.Online == 2, "TLS clients join");
    await alice.SendAsync(text); await Wait(() => received.Any(p => p.Text == text), "TLS broadcast preserves content");
    foreach (var invalid in new[] { ("bad", room, new string('0', 64)), ("bad", "wrong-code", pin), ("Alice", room, pin) })
    {
        using var client = new ChatConnection();
        await Reject(() => client.ConnectAsync(info.Host, info.Port, invalid.Item1, invalid.Item2, invalid.Item3), "wrong pin/code or duplicate nickname rejected");
        Check(!client.Connected, "rejected peer disconnected");
    }
    await Reject(() => alice.SendAsync(new string('x', 501)), "oversized message rejected");
    await alice.SendAsync("second"); await alice.SendAsync("third");
    await Wait(() => received.Any(p => p.Text == "third"), "messages delivered in order");
    var errors = new ConcurrentQueue<ChatPacket>(); alice.Received += errors.Enqueue;
    await alice.SendAsync("rate-limited"); await Wait(() => errors.Any(p => p.Kind == "error"), "server rate limit enforced");
    using var late = new ChatConnection(); var replay = new ConcurrentQueue<ChatPacket>(); late.Received += replay.Enqueue;
    await late.ConnectAsync(info.Host, info.Port, "Late", room, pin);
    await Wait(() => replay.Count(p => p.Kind == "message") == 2, "bounded history replays only recent entries");
    Check(replay.Where(p => p.Kind == "message").Select(p => p.Text).SequenceEqual(new[] { "second", "third" }), "history evicts oldest entry");
    using (var plain = new TcpClient())
    {
        await plain.ConnectAsync(IPAddress.Loopback, info.Port);
        plain.ReceiveTimeout = 3000; plain.GetStream().Write(Encoding.ASCII.GetBytes("not a TLS handshake\n"));
        bool denied;
        try { denied = plain.GetStream().ReadByte() == -1; } catch (IOException) { denied = true; }
        Check(denied && server.Online == 3, "plaintext connection cannot join");
    }
    server.Dispose(); await Wait(() => !alice.Connected && !bob.Connected && !late.Connected, "server stop disconnects all clients");
    Check(server.Online == 0, "server stop resets online state");
    using var restarted = new ChatServer(room, certificate: certificate); restarted.Start(IPAddress.Loopback, 0);
    using var fresh = new ChatConnection(); var freshPackets = new ConcurrentQueue<ChatPacket>(); fresh.Received += freshPackets.Enqueue;
    await fresh.ConnectAsync(info.Host, restarted.Port, "Fresh", room, pin); await fresh.SendAsync("new-session");
    await Wait(() => freshPackets.Any(p => p.Text == "new-session"), "same certificate restart accepts fresh connection");
    Check(!freshPackets.Any(p => p.Text == "second"), "restart does not retain history");

    object parsed = AgentJson.Parse("{\"choices\":[{\"delta\":{\"content\":\"你好\\n世界 \\u4e2d\"}}],\"n\":1.5,\"ok\":true,\"nil\":null}");
    Check(AgentJson.Text(parsed, "choices", "0", "delta", "content") == "你好\n世界 中", "agent reader extracts nested streamed content and decodes escapes");
    Check(AgentJson.Text(parsed, "choices", "0", "gone") == null && AgentJson.Text(parsed, "nope", "0") == null, "missing agent JSON paths return null rather than throwing");
    Check(AgentJson.Quote("a\"b\\c\nd\u0001") == "\"a\\\"b\\\\c\\nd\\u0001\"", "agent request encoder escapes quotes backslashes and control characters");

    string emoji = new string('字', 19) + "😊" + new string('字', 300);
    int cut = AgentText.Cut(emoji, 20);
    Check(!(Char.IsHighSurrogate(emoji[cut - 1]) && Char.IsLowSurrogate(emoji[cut])), "chunk boundary never splits an emoji surrogate pair");

    var source = new StringBuilder();
    for (int i = 0; i < 200; i++) source.Append("第" + i + "段。");
    string longAnswer = source.ToString();
    var chunker = new AgentChunker(120);
    var pieces = new List<string>();
    for (int i = 0; i < longAnswer.Length; i += 7)
    {
        var ready = new List<string>();
        chunker.Add(longAnswer.Substring(i, Math.Min(7, longAnswer.Length - i)), ready);
        pieces.AddRange(ready);
    }
    string tail = chunker.Flush(); if (tail.Length > 0) pieces.Add(tail);
    Check(pieces.All(p => p.Length <= 120), "chunker keeps every piece within the room message length");
    Check(String.Concat(pieces) == longAnswer, "chunker reassembles a long answer in order without losing text");

    bool agentRejected = false;
    try { new AgentOptions("http://agent.example.com/v1", "k", "m", "小助手", ""); } catch (ArgumentException) { agentRejected = true; }
    Check(agentRejected, "agent endpoint refuses plaintext http away from loopback");
    agentRejected = false;
    try { new AgentOptions("https://agent.example.com/v1", "k", "m", "小助手", "", true, true, 2000, 480, 8, 8); } catch (ArgumentException) { agentRejected = true; }
    Check(agentRejected, "agent pacing above the room burst limit is rejected at construction");

    var endpoint = new TcpListener(IPAddress.Loopback, 0); endpoint.Start();
    int agentPort = ((IPEndPoint)endpoint.LocalEndpoint).Port;
    var requests = new ConcurrentQueue<string>();
    // The fixture repeats the agent nickname so that only the loop guard can stop a self-answer loop.
    string reply = String.Concat(Enumerable.Repeat("@小助手：这是完整回答。", 150));
    string events = "data: {\"choices\":[{\"delta\":{\"content\":" + AgentJson.Quote(reply) + "}}]}\r\n\r\ndata: [DONE]\r\n\r\n";
    _ = Task.Run(() => ServeAgent(endpoint, events, 4, requests));

    using (var agentCertificate = ChatSecurity.CreateCertificate())
    using (var service = new ChatServer(room, new ChatOptions(sendBurst: 100), agentCertificate))
    {
        service.Start(IPAddress.Loopback, 0);
        var agentSettings = new AgentOptions("http://127.0.0.1:" + agentPort + "/v1/chat", "test-key", "test-model", "小助手", "", true, true, 2000, 480);
        using (var bridge = new AgentBridge(agentSettings, "127.0.0.1", service.Port, room, service.Fingerprint, new ChatOptions(sendBurst: 100)))
        using (var asker = new ChatConnection())
        {
            var seen = new ConcurrentQueue<ChatPacket>();
            asker.Received += seen.Enqueue;
            await asker.ConnectAsync("127.0.0.1", service.Port, "提问的人", room, service.Fingerprint);
            await Wait(() => service.Online == 2, "agent joins the room as an ordinary online member", 25);
            await asker.SendAsync("请大家稍等，@小助手 看一下这个问题。");
            await Wait(() => seen.Any(p => p.Nick == "小助手" && p.Kind == "message"), "agent answers a mention inside the room", 25);
            await Wait(() => !bridge.Status.StartsWith("正在回答"), "agent returns to idle once the answer is delivered", 25);
            ChatPacket[] answers = seen.Where(p => p.Kind == "message" && p.Nick == "小助手").ToArray();
            Check(answers.All(p => p.Text.Length <= 480), "every chunk of a long answer fits the room message length");
            Check(String.Concat(answers.Select(p => p.Text)) == reply, "chunked answer reassembles to the full reply in order");
            Check(answers.Select(p => p.Id).Distinct().Count() == answers.Length, "each agent chunk gets its own server sequence number");
            Check(requests.Count == 1 && requests.Single().Contains("@小助手 看一下这个问题"), "agent forwards the question text and its asker to the endpoint");
            await Task.Delay(3000);
            Check(requests.Count == 1, "agent never answers its own messages, so the room cannot loop");
            Check(seen.Count(p => p.Kind == "message" && p.Nick == "小助手") == answers.Length, "no runaway repeat answers arrive after the reply settles");
        }
    }
    // A bind to one specific address does not answer on loopback, so the bridge has to dial back the
    // address the service was actually handed. Hardcoding 127.0.0.1 stranded the assistant on every
    // LAN deployment while the service window still reported that it had joined the room.
    IPAddress? lan = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254."));
    if (lan == null) Console.WriteLine("SKIP assistant reaches a non-loopback bind (no LAN address on this machine)");
    else
    {
        try
        {
            using var agentCertificate = ChatSecurity.CreateCertificate();
            using var service = new ChatServer(room, new ChatOptions(sendBurst: 100), agentCertificate);
            service.Start(lan, 0);
            var agentSettings = new AgentOptions("http://127.0.0.1:" + agentPort + "/v1/chat", "test-key", "test-model", "小助手", "", true, true, 2000, 480);
            using var bridge = new AgentBridge(agentSettings, lan.ToString(), service.Port, room, service.Fingerprint, new ChatOptions(sendBurst: 100));
            using var asker = new ChatConnection();
            var seen = new ConcurrentQueue<ChatPacket>();
            asker.Received += seen.Enqueue;
            await asker.ConnectAsync(lan.ToString(), service.Port, "同网段的人", room, service.Fingerprint);
            await Wait(() => service.Online == 2, "assistant joins a room bound to a non-loopback address", 25);
            await asker.SendAsync("@小助手 局域网里也能用吗");
            await Wait(() => seen.Any(p => p.Nick == "小助手" && p.Kind == "message"), "assistant answers in a room bound to a non-loopback address", 25);
        }
        catch (SocketException ex) { Console.WriteLine("SKIP assistant reaches a non-loopback bind (" + ex.Message + ")"); }
    }
    endpoint.Stop();

    // Written to a temp directory, never to the real %LOCALAPPDATA% agent.xml the service would use.
    string agentRoot = Path.Combine(Path.GetTempPath(), "nengliang-agent-" + Guid.NewGuid().ToString("N"));
    try
    {
        string configPath = Path.Combine(agentRoot, "agent.xml");
        var saved = new AgentOptions("http://127.0.0.1:1/v1/chat", "test-key", "test-model", "小助手", "");
        AgentConfig.Save(configPath, saved);
        AgentOptions reloaded = AgentConfig.Load(configPath);
        Check(reloaded != null && reloaded.Endpoint == saved.Endpoint && reloaded.ApiKey == "test-key" && reloaded.Nick == "小助手" && reloaded.MentionOnly, "agent settings survive a protected save and load round trip");
        string written = File.ReadAllText(configPath, Encoding.UTF8);
        // DPAPI is Windows-only, so the file says which of the two it got rather than implying safety.
        if (OperatingSystem.IsWindows()) Check(written.Contains("protected=\"true\"") && !written.Contains("test-key"), "agent API key is DPAPI-protected and never written in plaintext");
        else Check(written.Contains("protected=\"false\""), "outside Windows the agent key is stored unprotected and marked as such");
        Check(AgentConfig.Load(Path.Combine(agentRoot, "absent.xml")) == null, "missing agent settings load as null instead of throwing");
        File.WriteAllText(configPath, "<agent version='1'><endpoint>https://example.test/v1</endpoint><apiKey protected='true'>not-base64!</apiKey></agent>", Encoding.UTF8);
        Check(AgentConfig.Load(configPath) == null, "malformed protected agent key loads as null");
    }
    finally { try { Directory.Delete(agentRoot, true); } catch (IOException) { } }

    if (args.Length == 1)
    {
        var start = new ProcessStartInfo(args[0]) { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var bridge = Process.Start(start)!;
        try
        {
            string line = (await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
            var parts = line.Split('|'); int legacyPort = int.Parse(parts[0]);
            using var modern = new ChatConnection(); var packets = new ConcurrentQueue<ChatPacket>(); modern.Received += packets.Enqueue;
            await modern.ConnectAsync("127.0.0.1", legacyPort, "Modern", room, parts[1]); await modern.SendAsync("to-legacy");
            await Wait(() => packets.Any(p => p.Text == "to-legacy"), "modern client interoperates with original Framework server");
            await bridge.StandardInput.WriteLineAsync(restarted.Port + "|" + pin);
            string result = (await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
            Check(result == "OK", "original Framework client interoperates with modern server: " + result);
        }
        finally { await bridge.StandardInput.WriteLineAsync("exit"); if (!bridge.WaitForExit(3000)) bridge.Kill(); }
    }
    Console.WriteLine($"{checks} chat checks passed."); return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

// Minimal HTTP/1.1 fixture standing in for an OpenAI-compatible endpoint. HttpListener needs an
// admin URL ACL on Windows, so the raw socket is the portable choice.
static void ServeAgent(TcpListener listener, string body, int count, ConcurrentQueue<string> requests)
{
    byte[] payload = Encoding.UTF8.GetBytes(body);
    for (int i = 0; i < count; i++)
    {
        try
        {
            using (TcpClient client = listener.AcceptTcpClient())
            using (NetworkStream stream = client.GetStream())
            {
                string? request = ReadHttpRequest(stream);
                if (request == null) continue;
                requests.Enqueue(request);
                byte[] head = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(head, 0, head.Length); stream.Write(payload, 0, payload.Length); stream.Flush();
            }
        }
        catch (Exception ex) { if (!ChatWire.Expected(ex)) throw; }
    }
}

static string? ReadHttpRequest(Stream stream)
{
    MemoryStream raw = new MemoryStream();
    while (!EndsWithHeaderBreak(raw))
    {
        int next = stream.ReadByte();
        if (next < 0) return null;
        raw.WriteByte((byte)next);
    }
    string headers = Encoding.UTF8.GetString(raw.ToArray());
    int length = 0;
    foreach (string line in headers.Split('\n'))
    {
        int colon = line.IndexOf(':');
        if (colon > 0 && String.Equals(line.Substring(0, colon).Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase)) Int32.TryParse(line.Substring(colon + 1).Trim(), out length);
    }
    byte[] body = new byte[length]; int offset = 0;
    while (offset < length) { int read = stream.Read(body, offset, length - offset); if (read <= 0) break; offset += read; }
    return headers + Encoding.UTF8.GetString(body, 0, offset);
}

static bool EndsWithHeaderBreak(MemoryStream stream)
{
    if (stream.Length < 4) return false;
    byte[] buffer = stream.GetBuffer(); long end = stream.Length;
    return buffer[end - 4] == 13 && buffer[end - 3] == 10 && buffer[end - 2] == 13 && buffer[end - 1] == 10;
}
