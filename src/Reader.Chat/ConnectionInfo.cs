// 连接信息的解析、校验与格式化，兼容上游 1.5 服务端复制的文本格式。
using System.Reflection;

namespace Reader.Chat;

/// <summary>Interoperable with the connection text copied by the upstream 1.5 server.</summary>
/// <remarks>文本由一行版本号和四行“字段：值”组成；解析只识别中文全角冒号，忽略无法识别的行。</remarks>
public sealed record ConnectionInfo(string Host, int Port, string Code, string Fingerprint)
{
    /// <summary>校验各字段的格式与范围。</summary>
    /// <exception cref="ArgumentException">地址、端口、房间码或指纹不符合协议要求。</exception>
    public void Validate()
    {
        // 直接构造时 Code 可能为 null；先归一为空串，让调用方拿到 ArgumentException 而不是 NullReferenceException。
        string code = Code ?? "";
        // 控制字符会破坏后续粘贴文本的行结构，必须和长度、端口范围、指纹格式一起校验。
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(char.IsControl) || Port is < 1 or > 65535
            || code.Length is < 6 or > 64 || code.Any(char.IsControl) || !ChatSecurity.ValidFingerprint(Fingerprint))
            throw new ArgumentException("请填写完整的地址、端口、6～64 位房间码和 64 位服务指纹。");
    }
    /// <summary>从用户粘贴的连接文本中解析出连接信息。</summary>
    /// <param name="text">包含服务地址、端口、房间码和服务指纹的多行文本。</param>
    /// <returns>已通过 <see cref="Validate"/> 校验的连接信息。</returns>
    /// <exception cref="ArgumentException">文本为空、过长、缺少字段或字段格式不合法。</exception>
    public static ConnectionInfo Parse(string text)
    {
        // 限制输入长度，避免解析异常巨大的剪贴板内容。
        if (text == null || text.Length > 4096) throw new ArgumentException("连接信息过长或为空。");
        var fields = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            // 只按全角冒号切分，与上游 1.5 生成的文本保持一致。
            int colon = line.IndexOf('：');
            if (colon > 0) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        // 四个必需字段缺一不可；重复字段按后出现的值覆盖，多余字段忽略以便未来扩展。
        if (!fields.TryGetValue("服务地址", out var host) || !fields.TryGetValue("端口", out var port)
            || !int.TryParse(port, out int number) || !fields.TryGetValue("房间码", out var code)
            || !fields.TryGetValue("服务指纹", out var fingerprint)) throw new ArgumentException("请粘贴含指纹的完整连接信息。");
        var info = new ConnectionInfo(host, number, code, fingerprint); info.Validate(); return info;
    }
    // 取程序集信息版本并去掉构建元数据（+commit 后缀），用于生成可粘贴的连接文本。
    private static string AppVersion => typeof(ConnectionInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    /// <summary>按上游 1.5 的文本格式输出版本号与四个连接字段。</summary>
    /// <returns>可直接粘贴到另一台设备的连接信息。</returns>
    public override string ToString() => $"良子小窗-v{AppVersion}\n服务地址：{Host}\n端口：{Port}\n房间码：{Code}\n服务指纹：{Fingerprint}";
}
