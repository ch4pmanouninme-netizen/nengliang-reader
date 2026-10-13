using Reader.Chat;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
async Task Wait(Func<bool> condition, string name)
{
    var clock = Stopwatch.StartNew();
    while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
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
    Check(ConnectionInfo.Parse(info.ToString()) == info, "upstream connection text roundtrip");
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
