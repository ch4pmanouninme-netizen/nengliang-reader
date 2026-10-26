using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Reader.Chat;
using Reader.Core;
using Avalonia.Styling;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using System.Xml;
using System.Xml.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Reader.Desktop;

/// <summary>
/// 聊天服务窗口：配置监听地址、端口和房间码，启动/停止 TLS 聊天服务，并托管可选的 HTTP 助手桥接。
/// </summary>
/// <remarks>
/// 证书在构造时创建、窗口关闭时释放；服务与助手桥接也由本窗口持有并负责停止。
/// 定时器在 UI 线程刷新在线人数和主题；停止服务或关闭窗口前必须经过确认。
/// </remarks>
internal sealed class ServerWindow : Window
{
    private readonly ComboBox address = new();
    private readonly TextBox port = new() { Text = "38417", MaxLength = 5 };
    private readonly TextBox code = new() { Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)), MaxLength = 64 };
    private readonly TextBlock status = new() { Text = "服务未启动", TextWrapping = TextWrapping.Wrap };
    private readonly Button toggle = new() { Content = "启动服务" }, copy = new() { Content = "复制连接信息", IsEnabled = false };
    // 证书生命周期与窗口一致；停止后再次启动复用同一证书，服务指纹保持不变。
    private readonly X509Certificate2 certificate = ChatSecurity.CreateCertificate();
    // UI 线程定时刷新在线人数、助手状态，并轮询磁盘上的主题偏好。
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer copyNoticeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Border copyNotice = UiThemes.Header(new TextBlock { Text = "复制成功 (๑•̀ㅂ•́)و✧" });
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
    // confirming 防止重复打开停止确认框；confirmed 放行窗口关闭。
    private bool confirmed, confirming;
    // readingTheme 防止主题轮询重入；closed 表示窗口已关闭，后台回调不再改界面。
    private bool readingTheme, closed;
    // 记录上次读取的 state.xml 时间戳/长度，以及已应用的主题，避免重复重建调色板。
    private (DateTime Modified, long Length)? themeStamp;
    private (string Theme, bool Dark)? appliedTheme;

    private static string AgentPath => Path.Combine(Program.DataDirectory ?? DataPaths.Default, "agent.xml");

    /// <summary>创建聊天服务窗口；会立即生成 TLS 证书并读取本机助手设置。</summary>
    public ServerWindow()
    {
        Title = "良子小窗 · 聊天服务"; Icon = AppIcon.Shared;
        Width = 600; Height = 760; MinWidth = 520; MinHeight = 560;
        // 优先以太网地址，过滤回环和链路本地地址，最后补上回环以便本机测试。
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
        // 复制成功提示浮在内容上方，不参与布局点击，2 秒后自动隐藏。
        copyNotice.IsVisible = false;
        copyNotice.HorizontalAlignment = HorizontalAlignment.Center;
        copyNotice.VerticalAlignment = VerticalAlignment.Bottom;
        copyNotice.Margin = new Thickness(20);
        copyNotice.Padding = new Thickness(16, 10);
        copyNotice.CornerRadius = new CornerRadius(8);
        copyNotice.BorderThickness = new Thickness(1);
        copyNotice.IsHitTestVisible = false;
        copyNotice.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("ReaderHeaderForeground"));
        copyNoticeTimer.Tick += (_, _) => { copyNotice.IsVisible = false; copyNoticeTimer.Stop(); };
        Content = new Grid { Children = { new ScrollViewer { Content = panel }, copyNotice } };
        // 同一个按钮负责启动/停止；停止前会弹确认。
        toggle.Click += async (_, _) => await Toggle();
        agentEnabled.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) SyncAgentFields(); };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (server == null || Clipboard == null) return;
                // 连接信息包含地址、端口、房间码和服务指纹，接收方可据此验证服务身份。
                await Clipboard.SetTextAsync(new ConnectionInfo((string)address.SelectedItem!, server.Port, code.Text!.Trim(), server.Fingerprint).ToString());
                // 复制是异步的，期间窗口可能已关闭，不能再动界面。
                if (closed) return;
                copyNotice.IsVisible = true;
                copyNoticeTimer.Stop(); copyNoticeTimer.Start();
            }
            catch (Exception ex) { status.Text = "复制失败：" + ex.Message; }
        };
        timer.Tick += async (_, _) =>
        {
            // 状态行只在服务运行时刷新；主题轮询则始终进行。
            if (server != null) status.Text = $"TLS 加密 · 在线 {server.Online} / {chatOptions.ParticipantLimit} 人 · 端口 {server.Port}" + (bridge == null ? "" : " · 助手：" + bridge.Status);
            await RefreshTheme();
        };
        // 有服务时关闭窗口等同停止服务，必须确认；确认后设置 confirmed 再真正关闭。
        Closing += async (_, e) =>
        {
            if (server == null || confirmed) return;
            e.Cancel = true;
            if (await ConfirmStopAsync() && !closed) { confirmed = true; Close(); }
        };
        // 关闭顺序：停定时器、停助手、释放服务与证书，最后清除敏感房间码。
        Closed += (_, _) => { closed = true; timer.Stop(); copyNoticeTimer.Stop(); StopAgent(); server?.Dispose(); certificate.Dispose(); code.Clear(); };
        // 启动时恢复助手设置并同步控件，随后立即读取一次主题。
        LoadAgentSettings(); SyncAgentFields();
        _ = RefreshTheme(); timer.Start();
    }

    // 停止服务和关闭窗口共用同一个确认框；confirming 防止重复点击叠出多个模态窗口。
    private async Task<bool> ConfirmStopAsync()
    {
        if (confirming || closed) return false;
        confirming = true;
        try
        {
            return await Dialogs.Confirm(this, "关闭聊天服务",
                "是否确认关闭聊天服务？(｡•́︿•̀｡)\n你是主机哦 awa，你关了大家都没得聊了 qwq\n关闭会断开所有人并清空聊天历史。",
                "不聊？那就都别聊了！(╯°□°)╯", "接着奏乐接着舞 ヾ(≧▽≦*)o");
        }
        finally { confirming = false; }
    }

    // 助手配置保存在独立的 agent.xml，避免与书库状态文件耦合。
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

    // 服务运行期间锁定所有连接/助手字段，防止运行中改配置造成状态不一致。
    private void SyncAgentFields()
    {
        bool idle = server == null;
        agentEnabled.IsEnabled = idle;
        bool interactive = idle && agentEnabled.IsChecked == true;
        agentEndpoint.IsEnabled = agentKey.IsEnabled = agentModel.IsEnabled = agentNick.IsEnabled = agentTrigger.IsEnabled = interactive;
    }

    // 释放助手桥接；它会自行断开与房间的连接。
    private void StopAgent()
    {
        if (bridge == null) return;
        bridge.Dispose(); bridge = null;
    }

    // 轮询 state.xml 中的界面主题；服务进程只读取偏好，不创建书库。
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
                // 时间戳和长度都没变时跳过 XML 解析；阅读进度频繁写入时这能省下大量工作。
                var info = new FileInfo(path); if (!info.Exists) return null;
                var stamp = (info.LastWriteTimeUtc, info.Length); if (themeStamp == stamp) return null;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var xml = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var pref = XDocument.Load(xml).Root?.Element("preferences");
                themeStamp = stamp;
                return pref == null ? null : new Preferences { UiDark = (string?)pref.Attribute("uiDark") == "true", UiTheme = (string?)pref.Attribute("uiTheme") ?? "" };
            });
            // state.xml 会因为阅读进度频繁重写；主题没变时不要重建整套调色板。
            if (!closed && appearance != null && appliedTheme != (appearance.UiTheme, appearance.UiDark))
            {
                UiThemes.Apply(appearance);
                appliedTheme = (appearance.UiTheme, appearance.UiDark);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { /* Keep last appearance during an atomic save. */ }
        finally { readingTheme = false; }
    }
    // 启动或停止服务；两条分支都负责把界面恢复到一致状态。
    private async Task Toggle()
    {
        if (server != null)
        {
            // 停止前确认；确认框期间服务保持运行，取消不会打断在线用户。
            if (!await ConfirmStopAsync() || closed || server == null) return;
            StopAgent(); server.Dispose(); server = null; agentState.Text = "服务已停止。"; status.Text = "服务已停止，历史已清空。";
        }
        else
        {
            try
            {
                // 启动前先校验端口，避免构造服务后才发现参数无效。
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
            // 启动中途失败时回滚已创建的服务和助手，避免留下半运行状态。
            catch (Exception ex) { StopAgent(); server?.Dispose(); server = null; status.Text = "启动失败：" + ex.Message; agentState.Text = "助手未启用。"; }
        }
        // 统一按服务是否存在切换按钮文案与字段可编辑状态。
        toggle.Content = server == null ? "启动服务" : "停止服务"; copy.IsEnabled = server != null;
        address.IsEnabled = port.IsEnabled = code.IsEnabled = server == null;
        SyncAgentFields();
    }
}
