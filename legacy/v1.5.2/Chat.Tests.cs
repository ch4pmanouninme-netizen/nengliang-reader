using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OneLineReader
{
    internal static class ChatTests
    {
        private const string Room = "local-test-room-only";
        private static int checks;
        private static string fingerprint;
        private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
        private static void Wait(Func<bool> ready, string name, int milliseconds = 7000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (!ready() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Check(ready(), name);
        }
        private static bool Contains(ConcurrentQueue<ChatPacket> packets, string text) { return packets.Any(p => p.Kind == "message" && p.Text == text); }
        private static Control Find(Control root, string name) { return root.Controls.Cast<Control>().First(c => c.Name == name); }
        private static MemoryStream Raw(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            MemoryStream stream = new MemoryStream(); stream.WriteByte((byte)(bytes.Length >> 24)); stream.WriteByte((byte)(bytes.Length >> 16)); stream.WriteByte((byte)(bytes.Length >> 8)); stream.WriteByte((byte)bytes.Length); stream.Write(bytes, 0, bytes.Length); stream.Position = 0; return stream;
        }
        private static void Connect(ChatConnection connection, int port, string nick, string code) { connection.ConnectAsync("127.0.0.1", port, nick, code, fingerprint).GetAwaiter().GetResult(); }
        [STAThread] public static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Directory.CreateDirectory(args[0]);
            try
            {
                string mixed = "中文 & <标签> 😊\n第二行";
                using (MemoryStream memory = new MemoryStream())
                {
                    ChatWire.Write(memory, new ChatPacket { Kind = "message", Nick = "小窗", Text = mixed, Id = 42 }); memory.Position = 0;
                    ChatPacket roundTrip = ChatWire.Read(memory);
                    Check(roundTrip.Text == mixed && roundTrip.Id == 42, "wire preserves Chinese, emoji, escaping and multiline messages");
                }
                bool rejected = false;
                try { using (MemoryStream memory = new MemoryStream(new byte[] { 127, 255, 255, 255 })) ChatWire.Read(memory); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "oversized frames rejected before allocating body");
                rejected = false;
                try { using (MemoryStream memory = Raw("<!DOCTYPE chat [<!ENTITY x SYSTEM 'file:///C:/nope'>]><chat kind='send'><text>&x;</text></chat>")) ChatWire.Read(memory); } catch (System.Xml.XmlException) { rejected = true; }
                Check(rejected, "chat protocol blocks DTD and external entity expansion");
                rejected = false;
                try { using (MemoryStream memory = new MemoryStream(new byte[] { 0, 0, 0, 9, 1 })) ChatWire.Read(memory); } catch (EndOfStreamException) { rejected = true; }
                Check(rejected, "incomplete frames terminate safely");
                Check(ChatWire.Clean(" \0测试\r\n文字\t ") == "测试\n文字", "message sanitization removes control characters and preserves newlines");
                var serviceCertificate = ChatSecurity.CreateCertificate(); fingerprint = ChatSecurity.Fingerprint(serviceCertificate);
                ChatServer service = new ChatServer(Room, null, serviceCertificate); service.Start(IPAddress.Loopback, 0); int port = service.Port;
                try
                {
                    ConcurrentQueue<ChatPacket> aliceInbox = new ConcurrentQueue<ChatPacket>(), bobInbox = new ConcurrentQueue<ChatPacket>();
                    using (ChatConnection alice = new ChatConnection())
                    using (ChatConnection bob = new ChatConnection())
                    {
                        alice.Received += p => aliceInbox.Enqueue(p); bob.Received += p => bobInbox.Enqueue(p);
                        Connect(alice, port, "同事A", Room); Connect(bob, port, "同事B", Room);
                        Wait(() => service.Online == 2, "two independent TCP clients join the same room");
                        alice.SendAsync(mixed).GetAwaiter().GetResult();
                        Wait(() => Contains(aliceInbox, mixed) && Contains(bobInbox, mixed), "server broadcasts identical content to sender and other client");
                        using (ChatConnection wrong = new ChatConnection())
                        {
                            rejected = false; try { Connect(wrong, port, "错误房间", "wrong-code"); } catch (ChatJoinException) { rejected = true; }
                            Check(rejected && !wrong.Connected, "wrong room code cannot join");
                        }
                        using (ChatConnection duplicate = new ChatConnection())
                        {
                            rejected = false; try { Connect(duplicate, port, "同事A", Room); } catch (ChatJoinException) { rejected = true; }
                            Check(rejected, "duplicate nicknames are rejected");
                        }
                        using (TcpClient raw = new TcpClient())
                        {
                            raw.Connect(IPAddress.Loopback, port);
                            using (ChatPeer secure = new ChatPeer(raw))
                            {
                            raw.ReceiveTimeout = 10000; secure.SecureAsClient(fingerprint);
                            ChatWire.Write(secure.Stream, new ChatPacket { Kind = "join", Code = Room, Nick = "真实昵称" });
                            Check(ChatWire.Read(secure.Stream).Kind == "welcome", "protocol-level client completes authenticated handshake");
                            ChatWire.Write(secure.Stream, new ChatPacket { Kind = "send", Nick = "伪造昵称", Text = "昵称由服务端确定", Id = 999999 });
                            Wait(() => bobInbox.Any(p => p.Text == "昵称由服务端确定" && p.Nick == "真实昵称" && p.Id != 999999), "server supplies trusted nickname and sequence rather than client claims");
                            }
                        }
                        Wait(() => service.Online == 2, "disconnected users are removed from online count");
                        using (ChatConnection late = new ChatConnection())
                        {
                            ConcurrentQueue<ChatPacket> replay = new ConcurrentQueue<ChatPacket>(); late.Received += p => replay.Enqueue(p); Connect(late, port, "后加入", Room);
                            Wait(() => Contains(replay, mixed) && Contains(replay, "昵称由服务端确定"), "new arrivals receive recent room history in order");
                            long[] ids = replay.Where(p => p.Kind == "message").Select(p => p.Id).ToArray(); Check(ids.SequenceEqual(ids.OrderBy(i => i)), "history replay has increasing server sequence numbers");
                        }
                        using (TcpClient broken = new TcpClient())
                        {
                            broken.ReceiveTimeout = 2000; broken.Connect(IPAddress.Loopback, port); ChatPeer secureBroken = new ChatPeer(broken); secureBroken.SecureAsClient(fingerprint); secureBroken.Stream.Write(new byte[] { 127, 255, 255, 255 }, 0, 4);
                            bool closed = false; try { closed = secureBroken.Stream.ReadByte() == -1; } catch (IOException) { closed = true; }
                            Check(closed && service.Online >= 2, "malformed peer is isolated while the room remains available");
                        }
                        Store store = new Store(Path.Combine(args[0], "chat-ui-data"));
                        store.Pref.ChatHost = "127.0.0.1"; store.Pref.ChatPort = port; store.Pref.ChatNick = "小窗测试";
                        store.Add("测试书籍", "第一章\n阅读和聊天同时使用。", "TXT");
                        using (LibraryForm library = new LibraryForm(store))
                        {
                            library.Show(); Application.DoEvents();
                            library.OpenChat(); Application.DoEvents();
                            ChatForm chat = Application.OpenForms.OfType<ChatForm>().Single();
                            Check(chat.Visible && chat.Text == "会话" && !chat.ShowInTaskbar, "main window opens a discreet independent chat window");
                            Check(!((Button)Find(chat, "Send")).Enabled && !((TextBox)Find(chat, "ChatCode")).Text.Any(), "opening chat alone stays offline with no saved room code");
                            ((TextBox)Find(chat, "ChatCode")).Text = Room;
                            ((TextBox)Find(chat, "ChatFingerprint")).Text = fingerprint;
                            ((Button)Find(chat, "Connect")).PerformClick();
                            ChatTranscript transcript = (ChatTranscript)Find(chat, "Transcript");
                            Wait(() => ((Button)Find(chat, "Send")).Enabled && transcript.Text.Contains("昵称由服务端确定"), "chat UI connects and displays room history");
                            alice.SendAsync("已经连通，可以边读边聊了。😊").GetAwaiter().GetResult();
                            Wait(() => transcript.Text.Contains("已经连通"), "incoming messages reach the visible chat window");
                            ((TextBox)Find(chat, "Compose")).Text = "收到，我这边也能发送。"; ((Button)Find(chat, "Send")).PerformClick();
                            Wait(() => Contains(bobInbox, "收到，我这边也能发送。"), "chat UI sends a message to another actual TCP client");
                            Wait(() => ((TextBox)Find(chat, "Compose")).TextLength == 0 && transcript.Text.Contains("收到，我这边也能发送。"), "successful send clears input and displays server echo");
                            Store saved = new Store(store.Root);
                            Check(saved.Pref.ChatHost == "127.0.0.1" && saved.Pref.ChatPort == port && saved.Pref.ChatNick == "小窗测试" && !File.ReadAllText(Path.Combine(store.Root, "state.xml")).Contains(Room), "address and nickname persist without storing room secret");
                            using (Bitmap image = new Bitmap(chat.Width, chat.Height)) { chat.DrawToBitmap(image, new Rectangle(Point.Empty, chat.Size)); image.Save(Path.Combine(args[0], "chat-light-preview.png")); }
                            library.UsePreset(5); Check(chat.BackColor == Color.FromArgb(25, 26, 28), "chat window follows IDEA dark palette");
                            using (Bitmap image = new Bitmap(chat.Width, chat.Height)) { chat.DrawToBitmap(image, new Rectangle(Point.Empty, chat.Size)); image.Save(Path.Combine(args[0], "chat-dark-preview.png")); }
                            typeof(LibraryForm).GetMethod("ToggleReader", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(library, null);
                            ChatConnection active = (ChatConnection)typeof(ChatForm).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(chat);
                            Check(!chat.Visible && !library.Visible && active.Connected, "global hide conceals chat without disconnecting it");
                            library.OpenChat(); Check(chat.Visible, "chat can be restored after hiding");
                            for (int i = 0; i < 8; i++) alice.SendAsync("限速检查" + i).GetAwaiter().GetResult();
                            Wait(() => aliceInbox.Any(p => p.Kind == "error" && p.Text.Contains("发送太快")), "server limits rapid repeated messages");
                            rejected = false; try { alice.SendAsync(new string('字', 501)).GetAwaiter().GetResult(); } catch (ArgumentException) { rejected = true; }
                            Check(rejected, "client blocks messages above 500 characters");
                            service.Dispose();
                            Wait(() => !alice.Connected && !bob.Connected && !((Button)Find(chat, "Send")).Enabled, "server shutdown disconnects all clients and disables chat sending");
                            service = new ChatServer(Room, null, serviceCertificate); service.Start(IPAddress.Loopback, port);
                            Wait(() => service.Online == 1 && ((Button)Find(chat, "Send")).Enabled, "chat UI automatically reconnects after service restart", 14000);
                            Wait(() => !transcript.Text.Contains("已经连通"), "service restart clears volatile history and starts a fresh session");
                            using (ChatConnection recovered = new ChatConnection())
                            {
                                Connect(recovered, port, "同事B", Room); recovered.SendAsync("重启后的会话已恢复。").GetAwaiter().GetResult();
                                Wait(() => transcript.Text.Contains("重启后的会话已恢复"), "reconnected UI receives messages from a fresh peer");
                                library.ExitApp(); Check(chat.IsDisposed && library.IsDisposed, "exiting application disposes chat window and its connection");
                                Wait(() => service.Online == 1, "application exit removes chat participant from server");
                            }
                        }
                    }
                }
                finally { service.Dispose(); serviceCertificate.Dispose(); }
                TcpListener reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int guiPort = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
                using (ChatServerForm serverUi = new ChatServerForm(IPAddress.Loopback, Path.Combine(args[0], "server-theme", "state.xml")))
                using (ChatConnection guest = new ChatConnection())
                {
                    serverUi.Show(); Application.DoEvents();
                    NumericUpDown guiPortControl = (NumericUpDown)Find(serverUi, "ServerPort"); guiPortControl.Value = guiPort;
                    TextBox guiCode = (TextBox)Find(serverUi, "ServerCode"); guiCode.Text = Room;
                    Button startService = (Button)Find(serverUi, "ServerStart"); startService.PerformClick();
                    Check(startService.Text == "停止服务" && !guiCode.Enabled && !guiPortControl.Enabled, "server UI starts service and locks active connection settings");
                    fingerprint = ((TextBox)Find(serverUi, "ServerFingerprint")).Text;
                    Connect(guest, guiPort, "服务界面联调", Room); Check(guest.Connected, "service started by GUI accepts an actual client");
                    startService.PerformClick();
                    Wait(() => !guest.Connected && startService.Text == "启动服务" && guiCode.Enabled, "server UI stops connections and restores editable settings");
                }
                RunNewChecks(args[0]);
                Console.WriteLine("SUCCESS: " + checks + " chat protocol, network and UI checks passed."); return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); return 1; }
        }
        private static void RunNewChecks(string root)
        {
            bool oldProtocolRejected = false;
            try { using (MemoryStream old = Raw("<chat version='1' kind='send'><text>legacy</text></chat>")) ChatWire.Read(old); }
            catch (InvalidDataException) { oldProtocolRejected = true; }
            Check(oldProtocolRejected, "older message protocol is explicitly rejected");
            Store store = new Store(Path.Combine(root, "module-data"));
            store.Add("模块测试", "第一章\n这是一段用于验证模块独立关闭的正文。", "TXT"); store.Save();
            using (var certificate = ChatSecurity.CreateCertificate())
            using (ChatServer service = new ChatServer(Room, new ChatOptions(historyLimit: 5, sendBurst: 100), certificate))
            {
                service.Start(IPAddress.Loopback, 0); fingerprint = service.Fingerprint;
                using (TcpClient plain = new TcpClient())
                {
                    plain.Connect(IPAddress.Loopback, service.Port); plain.ReceiveTimeout = 3000;
                    plain.GetStream().Write(new byte[] { 0, 0, 0, 10, 60, 99, 104, 97, 116 }, 0, 9);
                    bool closed = false;
                    try { for (int i = 0; i < 64; i++) if (plain.GetStream().ReadByte() < 0) { closed = true; break; } }
                    catch (IOException) { closed = true; }
                    Check(closed && service.Online == 0, "plaintext connections are rejected rather than silently downgraded");
                }
                using (ChatConnection writer = new ChatConnection())
                {
                    Connect(writer, service.Port, "缓存测试", Room);
                    Check(writer.Encrypted, "actual TCP connection uses authenticated encrypted TLS transport");
                    for (int i = 0; i < 20; i++) writer.SendAsync("有界消息" + i).GetAwaiter().GetResult();
                    Wait(() => service.HistoryCount == 5, "configured history drops old entries as messages arrive");
                    using (ChatConnection reader = new ChatConnection())
                    {
                        ConcurrentQueue<ChatPacket> recent = new ConcurrentQueue<ChatPacket>(); reader.Received += p => recent.Enqueue(p);
                        Connect(reader, service.Port, "最近消息", Room);
                        Wait(() => Contains(recent, "有界消息19"), "late client receives newest retained history");
                        Check(recent.Count(p => p.Kind == "message") == 5 && !Contains(recent, "有界消息0"), "history replay excludes evicted messages");
                    }
                    using (ChatConnection wrong = new ChatConnection())
                    {
                        bool rejected = false;
                        try { wrong.ConnectAsync("127.0.0.1", service.Port, "假服务", Room, new string('0', 64)).GetAwaiter().GetResult(); }
                        catch (System.Security.Authentication.AuthenticationException) { rejected = true; }
                        Check(rejected && !wrong.Connected, "wrong server fingerprint blocks TLS before room credentials are sent");
                    }
                    using (ChatPeer raw = new ChatPeer(new TcpClient("127.0.0.1", service.Port)))
                    {
                        raw.SecureAsClient(fingerprint); raw.Send(new ChatPacket { Kind = "join", Nick = "扩展测试", Code = Room });
                        ChatWire.Read(raw.Stream);
                        // Drain bounded history and state before exercising an unsupported future message type.
                        for (int i = 0; i < 6; i++) ChatWire.Read(raw.Stream);
                        raw.Send(new ChatPacket { Kind = "send", ContentType = "image", Text = "unused" });
                        Check(ChatWire.Read(raw.Stream).Kind == "error", "unknown content types are rejected without treating them as text");
                    }
                    using (AppController app = new AppController(store))
                    using (ChatServerForm serverUi = new ChatServerForm(IPAddress.Loopback, Path.Combine(store.Root, "state.xml")))
                    {
                        serverUi.Show(); Application.DoEvents();
                        Check(app.Home.Visible && app.Modules.Count() == 2 && app.Modules.All(m => m.Window == null), "launcher starts with two lazy independent modules");
                        using (Bitmap preview = new Bitmap(app.Home.Width, app.Home.Height)) { app.Home.DrawToBitmap(preview, new Rectangle(Point.Empty, app.Home.Size)); preview.Save(Path.Combine(root, "home-light-preview.png")); }
                        app.OpenModule("reading"); LibraryForm library = (LibraryForm)app.Modules.Single(m => m.Id == "reading").Window;
                        library.Controls.OfType<Button>().Single(b => b.Text == "显示").PerformClick(); Application.DoEvents();
                        ReaderForm floating = Application.OpenForms.OfType<ReaderForm>().Single();
                        app.OpenModule("chat"); ChatForm chat = (ChatForm)app.Modules.Single(m => m.Id == "chat").Window;
                        string info = "服务地址：127.0.0.1\n端口：" + service.Port + "\n房间码：" + Room + "\n服务指纹：" + fingerprint;
                        Check(chat.ApplyConnectionInfo(info), "pasting complete invitation fills address port code and certificate pin");
                        Check(!chat.ApplyConnectionInfo("服务地址：127.0.0.1"), "incomplete invitation cannot silently disable certificate verification");
                        ((TextBox)Find(chat, "ChatNick")).Text = "模块联调"; ((Button)Find(chat, "Connect")).PerformClick();
                        Wait(() => ((Button)Find(chat, "Send")).Enabled, "chat module connects while floating reader remains active");
                        library.CloseReading();
                        Check(floating.IsDisposed && !library.Visible && chat.Visible && ((Button)Find(chat, "Send")).Enabled, "closing reading releases floating reader without closing or disconnecting chat");
                        app.UsePreset(5);
                        Wait(() => serverUi.BackColor == Ui.Surface(true), "separate server window observes saved IDEA theme while open");
                        Check(chat.BackColor == Ui.Surface(true) && app.Home.BackColor == Ui.Surface(true), "theme covers launcher and active chat module");
                        Check(Find(serverUi, "ServerCode").BackColor == Color.FromArgb(43, 45, 48), "server input controls follow dark palette");
                        using (Bitmap preview = new Bitmap(serverUi.Width, serverUi.Height)) { serverUi.DrawToBitmap(preview, new Rectangle(Point.Empty, serverUi.Size)); preview.Save(Path.Combine(root, "server-dark-preview.png")); }
                        using (Bitmap preview = new Bitmap(app.Home.Width, app.Home.Height)) { app.Home.DrawToBitmap(preview, new Rectangle(Point.Empty, app.Home.Size)); preview.Save(Path.Combine(root, "home-dark-preview.png")); }
                        app.UsePreset(4); Wait(() => serverUi.BackColor == Ui.Surface(false), "server returns to WeCom theme without restarting service");
                        using (Bitmap preview = new Bitmap(serverUi.Width, serverUi.Height)) { serverUi.DrawToBitmap(preview, new Rectangle(Point.Empty, serverUi.Size)); preview.Save(Path.Combine(root, "server-light-preview.png")); }
                        app.OpenModule("reading");
                        library.Controls.OfType<Button>().Single(b => b.Text == "显示").PerformClick(); Application.DoEvents();
                        floating = Application.OpenForms.OfType<ReaderForm>().Single();
                        typeof(ChatForm).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(chat, new object[] { new ChatPacket { Kind = "welcome", Session = "cache-ui" } });
                        for (int i = 1; i <= 300; i++) typeof(ChatForm).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(chat,
                            new object[] { new ChatPacket { Kind = "message", Session = "cache-ui", Id = i, Text = "界面缓存" + i } });
                        Check(chat.CachedCount == 150 && !Find(chat, "Transcript").Text.Contains("界面缓存1\r"), "client transcript retains only bounded latest entries");
                        app.ToggleVisibility(); Check(!chat.Visible && !floating.Visible, "global hide preserves both modules");
                        app.ToggleVisibility(); Check(chat.Visible && floating.Visible, "global restore restores previously visible modules");
                        chat.Close();
                        Check(chat.IsDisposed && chat.CachedCount == 0 && !floating.IsDisposed && floating.Visible, "closing chat clears client cache and leaves reading active");
                        app.OpenModule("chat"); ChatForm reopened = (ChatForm)app.Modules.Single(m => m.Id == "chat").Window;
                        Check(reopened != chat && reopened.CachedCount == 0 && !((Button)Find(reopened, "Send")).Enabled, "reopening closed chat starts with an empty disconnected module");
                        MethodInfo enqueue = typeof(ChatForm).GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic);
                        for (int i = 0; i < 1000; i++) enqueue.Invoke(reopened, new object[] { 0, new ChatPacket { Kind = "message", Text = "待处理消息" } });
                        var waiting = (ConcurrentQueue<KeyValuePair<int, ChatPacket>>)typeof(ChatForm).GetField("inbox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reopened);
                        Check(waiting.Count <= 182, "blocked UI has a bounded incoming queue under message flood");
                        app.ExitApplication(); Check(library.IsDisposed && reopened.IsDisposed && app.Home.IsDisposed, "explicit application exit closes all modules");
                        Check(waiting.Count == 0, "closing chat drains pending incoming messages");
                    }
                    service.Dispose(); Check(service.HistoryCount == 0, "stopping service explicitly releases room history");
                }
            }
        }
    }
}

