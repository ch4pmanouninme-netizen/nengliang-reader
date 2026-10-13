using Avalonia;
using Avalonia.Controls;
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
    private ChatServer? server;
    private bool confirmed, confirming;
    private bool readingTheme, closed;
    private (DateTime Modified, long Length)? themeStamp;
    public ServerWindow()
    {
        Title = "良子小窗 · 聊天服务"; Width = 600; Height = 480; MinWidth = 480; MinHeight = 400;
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
        panel.Children.Add(status); panel.Children.Add(UiActions.Row(toggle, copy));
        panel.Children.Add(new TextBlock { Text = "仅向可信人员分享完整连接信息。传输加密，服务电脑可以读取消息。", TextWrapping = TextWrapping.Wrap });
        Content = panel;
        toggle.Click += (_, _) => Toggle();
        copy.Click += async (_, _) =>
        {
            try
            {
                if (server == null || Clipboard == null) return;
                await Clipboard.SetTextAsync(new ConnectionInfo((string)address.SelectedItem!, server.Port, code.Text!.Trim(), server.Fingerprint).ToString());
            }
            catch (Exception ex) { status.Text = "复制失败：" + ex.Message; }
        };
        timer.Tick += async (_, _) => { if (server != null) status.Text = $"TLS 加密 · 在线 {server.Online} / 30 人 · 端口 {server.Port}"; await RefreshTheme(); };
        Closing += async (_, e) =>
        {
            if (server == null || confirmed) return;
            e.Cancel = true; if (confirming) return; confirming = true;
            try { if (await Dialogs.Prompt(this, "关闭聊天服务", "关闭会断开所有人并清空历史。输入“关闭”确认。") == "关闭") { confirmed = true; Close(); } }
            finally { confirming = false; }
        };
        Closed += (_, _) => { closed = true; timer.Stop(); server?.Dispose(); certificate.Dispose(); code.Clear(); };
        _ = RefreshTheme(); timer.Start();
    }
    private async Task RefreshTheme()
    {
        if (readingTheme || closed) return;
        readingTheme = true;
        // Read only preferences; never instantiate or save a library from the service process.
        try
        {
            string path = Path.Combine(Program.DataDirectory ?? DataPaths.Default, "state.xml");
            bool? dark = await Task.Run<bool?>(() =>
            {
                var info = new FileInfo(path); if (!info.Exists) return null;
                var stamp = (info.LastWriteTimeUtc, info.Length); if (themeStamp == stamp) return null;
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var xml = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var pref = XDocument.Load(xml).Root?.Element("preferences");
                themeStamp = stamp; return pref == null ? null : (string?)pref.Attribute("uiDark") == "true";
            });
            if (!closed && dark.HasValue) RequestedThemeVariant = dark.Value ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { /* Keep last appearance during an atomic save. */ }
        finally { readingTheme = false; }
    }
    private void Toggle()
    {
        if (server != null) { server.Dispose(); server = null; status.Text = "服务已停止，历史已清空。"; }
        else
        {
            try
            {
                if (!int.TryParse(port.Text, out int number) || number is < 1024 or > 65535) throw new ArgumentException("端口范围为 1024～65535。");
                server = new ChatServer(code.Text, certificate: certificate); server.Start(IPAddress.Parse((string)address.SelectedItem!), number);
                status.Text = $"TLS 加密 · 在线 0 / 30 人 · 端口 {server.Port}";
            }
            catch (Exception ex) { server?.Dispose(); server = null; status.Text = "启动失败：" + ex.Message; }
        }
        toggle.Content = server == null ? "启动服务" : "停止服务"; copy.IsEnabled = server != null;
        address.IsEnabled = port.IsEnabled = code.IsEnabled = server == null;
    }
}
