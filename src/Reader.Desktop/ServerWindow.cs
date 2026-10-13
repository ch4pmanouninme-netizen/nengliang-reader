using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Reader.Chat;
using Reader.Core;
using Avalonia.Styling;
using System.Xml;
using System.Xml.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Reader.Desktop;

internal sealed class ServerWindow : Window
{
    private readonly ComboBox address = new();
    private readonly TextBox port = new() { Text = "38417", MaxLength = 5 };
    private readonly TextBox code = new() { Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)), MaxLength = 64 };
    private readonly TextBlock status = new() { Text = "服务未启动", TextWrapping = TextWrapping.Wrap };
    private readonly Button toggle = new() { Content = "启动服务" }, copy = new() { Content = "复制连接信息", IsEnabled = false };
    private readonly X509Certificate2 certificate = ChatSecurity.CreateCertificate();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // The status line and the agent's chunk size both derive from this, not from a hardcoded copy of the defaults.
    private readonly ChatOptions chatOptions = new();
    private readonly CheckBox agentEnabled = new() { Content = "启用助手（作为房间成员加入）" };
    private readonly TextBox agentEndpoint = new() { MaxLength = 300, Watermark = "https://.../v1/chat/completions" };
    private readonly TextBox agentKey = new() { MaxLength = 512, PasswordChar = '●' };
    private readonly TextBox agentModel = new() { MaxLength = 128 };
    private readonly TextBox agentNick = new() { MaxLength = 20 };
    private readonly ComboBox agentTrigger = new() { ItemsSource = new[] { "仅 @昵称 时回答", "每条消息都回答" }, SelectedIndex = 0 };
    private readonly TextBlock agentState = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private ChatServer? server;
    private AgentBridge? bridge;
    private bool confirmed, confirming;
    private bool readingTheme, closed;
    private (DateTime Modified, long Length)? themeStamp;

    private static string AgentPath => Path.Combine(Program.DataDirectory ?? DataPaths.Default, "agent.xml");

    public ServerWindow()
    {
        Title = "良子小窗 · 聊天服务"; Width = 600; Height = 760; MinWidth = 520; MinHeight = 560;
        string[] addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.")).Select(a => a.ToString()).Distinct().Append("127.0.0.1").ToArray();
        address.ItemsSource = addresses; address.SelectedIndex = 0;
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "局域网加密聊天服务", FontSize = 22 });
        panel.Children.Add(new TextBlock { Text = "选择要监听的网卡地址。关闭服务会断开所有人并清空历史。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(UiActions.Labeled("本机监听地址", address)); panel.Children.Add(UiActions.Row(UiActions.Labeled("端口", port), UiActions.Labeled("房间码", code)));
        panel.Children.Add(UiActions.Labeled("SHA-256 服务指纹", new TextBox { Text = ChatSecurity.Fingerprint(certificate), IsReadOnly = true }));
        panel.Children.Add(new TextBlock { Text = "助手（可选）", FontSize = 16, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(agentEnabled);
        panel.Children.Add(UiActions.Labeled("HTTP 端点", agentEndpoint));
        panel.Children.Add(UiActions.Row(UiActions.Labeled("密钥", agentKey), UiActions.Labeled("模型", agentModel)));
        panel.Children.Add(UiActions.Row(UiActions.Labeled("昵称", agentNick), UiActions.Labeled("触发方式", agentTrigger)));
        panel.Children.Add(agentState);
        panel.Children.Add(new TextBlock { Text = "助手通过上面选中的监听地址加入房间，可被 @昵称 提问。端点必须是 https，只有 127.0.0.1 允许 http，因为密钥随请求头发送。应答直接广播给全房间，没有人工审核。", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        panel.Children.Add(status); panel.Children.Add(UiActions.Row(toggle, copy));
        panel.Children.Add(new TextBlock { Text = "仅向可信人员分享完整连接信息。传输加密，服务电脑可以读取消息。", TextWrapping = TextWrapping.Wrap });
        Content = new ScrollViewer { Content = panel };
        toggle.Click += (_, _) => Toggle();
        agentEnabled.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) SyncAgentFields(); };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (server == null || Clipboard == null) return;
                await Clipboard.SetTextAsync(new ConnectionInfo((string)address.SelectedItem!, server.Port, code.Text!.Trim(), server.Fingerprint).ToString());
            }
            catch (Exception ex) { status.Text = "复制失败：" + ex.Message; }
        };
        timer.Tick += async (_, _) =>
        {
            if (server != null) status.Text = $"TLS 加密 · 在线 {server.Online} / {chatOptions.ParticipantLimit} 人 · 端口 {server.Port}" + (bridge == null ? "" : " · 助手：" + bridge.Status);
            await RefreshTheme();
        };
        Closing += async (_, e) =>
        {
            if (server == null || confirmed) return;
            e.Cancel = true; if (confirming) return; confirming = true;
            try { if (await Dialogs.Prompt(this, "关闭聊天服务", "关闭会断开所有人并清空历史。输入“关闭”确认。") == "关闭") { confirmed = true; Close(); } }
            finally { confirming = false; }
        };
        Closed += (_, _) => { closed = true; timer.Stop(); StopAgent(); server?.Dispose(); certificate.Dispose(); code.Clear(); };
        LoadAgentSettings(); SyncAgentFields();
        _ = RefreshTheme(); timer.Start();
    }

    private void LoadAgentSettings()
    {
        AgentOptions saved = AgentConfig.Load(AgentPath);
        if (saved == null) { agentNick.Text = "小助手"; agentState.Text = "未保存过助手设置。"; return; }
        agentEndpoint.Text = saved.Endpoint; agentKey.Text = saved.ApiKey; agentModel.Text = saved.Model; agentNick.Text = saved.Nick;
        agentTrigger.SelectedIndex = saved.MentionOnly ? 0 : 1;
        agentState.Text = "已读取本机保存的助手设置。";
    }

    // Validated before the service starts so a bad endpoint cannot leave a running server behind.
    private AgentOptions ReadAgentSettings()
    {
        return new AgentOptions(agentEndpoint.Text ?? "", agentKey.Text ?? "", agentModel.Text ?? "", agentNick.Text ?? "", "",
            mentionOnly: agentTrigger.SelectedIndex == 0, stream: true, replyLimit: 2000, chunkSize: chatOptions.MessageLength - 20);
    }

    private void SyncAgentFields()
    {
        bool idle = server == null;
        agentEnabled.IsEnabled = idle;
        bool interactive = idle && agentEnabled.IsChecked == true;
        agentEndpoint.IsEnabled = agentKey.IsEnabled = agentModel.IsEnabled = agentNick.IsEnabled = agentTrigger.IsEnabled = interactive;
    }

    private void StopAgent()
    {
        if (bridge == null) return;
        bridge.Dispose(); bridge = null;
    }

    private async Task RefreshTheme()
    {
        if (readingTheme || closed) return;
        readingTheme = true;
        // Read only preferences; never instantiate or save a library from the service process.
        try
        {
            string path = Path.Combine(Program.DataDirectory ?? DataPaths.Default, "state.xml");
            Preferences? appearance = await Task.Run<Preferences?>(() =>
            {
                var info = new FileInfo(path); if (!info.Exists) return null;
                var stamp = (info.LastWriteTimeUtc, info.Length); if (themeStamp == stamp) return null;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var xml = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var pref = XDocument.Load(xml).Root?.Element("preferences");
                themeStamp = stamp;
                return pref == null ? null : new Preferences { UiDark = (string?)pref.Attribute("uiDark") == "true", UiTheme = (string?)pref.Attribute("uiTheme") ?? "" };
            });
            if (!closed && appearance != null) UiThemes.Apply(appearance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { /* Keep last appearance during an atomic save. */ }
        finally { readingTheme = false; }
    }
    private void Toggle()
    {
        if (server != null) { StopAgent(); server.Dispose(); server = null; agentState.Text = "服务已停止。"; status.Text = "服务已停止，历史已清空。"; }
        else
        {
            try
            {
                if (!int.TryParse(port.Text, out int number) || number is < 1024 or > 65535) throw new ArgumentException("端口范围为 1024～65535。");
                AgentOptions? agent = agentEnabled.IsChecked == true ? ReadAgentSettings() : null;
                string host = (string)address.SelectedItem!;
                server = new ChatServer(code.Text, chatOptions, certificate); server.Start(IPAddress.Parse(host), number);
                if (agent != null)
                {
                    AgentConfig.Save(AgentPath, agent);
                    // The bridge joins through the very address the service binds. Hardcoding 127.0.0.1 here
                    // used to strand the assistant on every non-loopback deployment while the status line
                    // still reported that it had joined.
                    bridge = new AgentBridge(agent, host, server.Port, code.Text!.Trim(), server.Fingerprint, chatOptions);
                    agentState.Text = "助手已启动，正在加入房间。";
                }
                status.Text = $"TLS 加密 · 在线 0 / {chatOptions.ParticipantLimit} 人 · 端口 {server.Port}";
            }
            catch (Exception ex) { StopAgent(); server?.Dispose(); server = null; status.Text = "启动失败：" + ex.Message; agentState.Text = "助手未启用。"; }
        }
        toggle.Content = server == null ? "启动服务" : "停止服务"; copy.IsEnabled = server != null;
        address.IsEnabled = port.IsEnabled = code.IsEnabled = server == null;
        SyncAgentFields();
    }
}
