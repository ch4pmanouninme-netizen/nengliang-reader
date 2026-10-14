using System.Reflection;

namespace Reader.Chat;

/// <summary>Interoperable with the connection text copied by the upstream 1.5 server.</summary>
public sealed record ConnectionInfo(string Host, int Port, string Code, string Fingerprint)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(char.IsControl) || Port is < 1 or > 65535
            || Code.Length is < 6 or > 64 || Code.Any(char.IsControl) || !ChatSecurity.ValidFingerprint(Fingerprint))
            throw new ArgumentException("请填写完整的地址、端口、6～64 位房间码和 64 位服务指纹。");
    }
    public static ConnectionInfo Parse(string text)
    {
        if (text == null || text.Length > 4096) throw new ArgumentException("连接信息过长或为空。");
        var fields = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            int colon = line.IndexOf('：');
            if (colon > 0) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (!fields.TryGetValue("服务地址", out var host) || !fields.TryGetValue("端口", out var port)
            || !int.TryParse(port, out int number) || !fields.TryGetValue("房间码", out var code)
            || !fields.TryGetValue("服务指纹", out var fingerprint)) throw new ArgumentException("请粘贴含指纹的完整连接信息。");
        var info = new ConnectionInfo(host, number, code, fingerprint); info.Validate(); return info;
    }
    private static string AppVersion => typeof(ConnectionInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public override string ToString() => $"良子小窗-v{AppVersion}\n服务地址：{Host}\n端口：{Port}\n房间码：{Code}\n服务指纹：{Fingerprint}";
}
