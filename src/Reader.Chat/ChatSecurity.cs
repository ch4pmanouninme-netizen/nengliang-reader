// 聊天加密与容量参数：自签名证书生成、指纹校验（TOFU）以及 TLS 1.2 的客户端/服务端封装。
using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Reader.Chat
{
    /// <summary>聊天室的 TLS 证书生成与指纹校验工具。</summary>
    /// <remarks>使用自签名证书，并以带外传递的 SHA-256 指纹代替 CA 链校验（TOFU 模型）。
    /// 证书不会安装到系统信任库，连接方必须事先获得完整指纹。</remarks>
    public static class ChatSecurity
    {
        /// <summary>生成聊天室使用的自签名服务端证书。</summary>
        /// <returns>带私钥的证书；私钥位于平台可用的密钥容器中。</returns>
        public static X509Certificate2 CreateCertificate()
        {
            using (RSA key = RSA.Create())
            {
                key.KeySize = 2048;
                CertificateRequest request = new CertificateRequest("CN=LiangziWindow", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                // CA=false 且 critical：明确该证书只能自用，不能签发其他证书。
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                // 仅用于数字签名与密钥交换，符合 TLS 服务端证书的用途限制。
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                // 1.3.6.1.5.5.7.3.1 = serverAuth，限定为 TLS 服务端证书。
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                // 起始时间回拨 5 分钟，避免两端时钟偏差导致证书尚未生效。
                using (X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1)))
                {
                    // Schannel requires a user key container; macOS requires its temporary keychain.
                    // Do not set PersistKeySet or install the certificate into a trusted store.
                    byte[] pfx = generated.Export(X509ContentType.Pfx);
                    // 经 PFX 重新导入，让私钥落入当前用户的密钥容器；PFX 明文用完立即清零。
                    try { return X509CertificateLoader.LoadPkcs12(pfx, null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet); }
                    finally { CryptographicOperations.ZeroMemory(pfx); }
                }
            }
        }
        /// <summary>计算证书的 SHA-256 指纹。</summary>
        /// <param name="certificate">待计算证书。</param>
        /// <returns>64 个十六进制字符的大写指纹字符串。</returns>
        public static string Fingerprint(X509Certificate certificate)
            => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        /// <summary>检查字符串是否为合法的证书指纹。</summary>
        /// <param name="value">待检查的指纹。</param>
        /// <returns>长度恰为 64 且全部为十六进制字符时返回 true。</returns>
        public static bool ValidFingerprint(string value)
        {
            // 指纹必须完整，长度或字符不符都直接拒绝，避免截断比较。
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
        /// <summary>在现有传输流上建立 TLS 客户端连接，并校验服务端证书。</summary>
        /// <param name="transport">已连接的底层流。</param>
        /// <param name="expected">带外获得的服务端证书 SHA-256 指纹。</param>
        /// <returns>已完成握手并加密的流。</returns>
        /// <exception cref="AuthenticationException">指纹缺失/格式错误、证书过期或指纹不匹配。</exception>
        public static SslStream Client(Stream transport, string expected)
        {
            // 没有有效指纹就无法安全地认证服务端，直接失败而不是退回明文。
            if (!ValidFingerprint(expected)) throw new AuthenticationException("请粘贴服务电脑提供的完整连接信息，缺少有效服务指纹。");
            SslStream stream = new SslStream(transport, false, (sender, certificate, chain, errors) =>
            {
                if (certificate == null) return false;
                // 每次握手都重新计算指纹并检查有效期：指纹固定，但证书可能被替换或已过期。
                using (X509Certificate2 actual = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()))
                    return actual.NotBefore.ToUniversalTime() <= DateTime.UtcNow && actual.NotAfter.ToUniversalTime() > DateTime.UtcNow
                        && String.Equals(Fingerprint(actual), expected, StringComparison.OrdinalIgnoreCase);
            });
            // 只启用 TLS 1.2；握手失败时释放半初始化的流，避免底层连接泄漏。
            try { stream.AuthenticateAsClient("LiangziWindow", null, SslProtocols.Tls12, false); return stream; }
            catch { stream.Dispose(); throw; }
        }
        /// <summary>在现有传输流上建立 TLS 服务端连接。</summary>
        /// <param name="transport">已接受的底层流。</param>
        /// <param name="certificate">服务端证书，由调用方持有并负责释放。</param>
        /// <returns>已完成握手并加密的流。</returns>
        /// <remarks>不要求客户端证书：房间码用于准入，带外指纹用于客户端认证服务端。</remarks>
        public static SslStream Server(Stream transport, X509Certificate2 certificate)
        {
            SslStream stream = new SslStream(transport, false);
            try { stream.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false); return stream; }
            catch { stream.Dispose(); throw; }
        }
    }

    /// <summary>聊天容量与限流参数，服务端和客户端共用。</summary>
    /// <remarks>所有值在构造时校验。<see cref="OutboundLimit"/> 必须能容纳完整历史加若干控制帧。</remarks>
    public sealed class ChatOptions
    {
        /// <summary>历史记录上限、客户端历史上限、人数上限、消息长度、待处理连接上限、出站队列上限和 10 秒发送上限。</summary>
        public readonly int HistoryLimit, ClientHistoryLimit, ParticipantLimit, MessageLength, PendingLimit, OutboundLimit, SendBurst;
        /// <summary>创建容量参数并校验各值范围。</summary>
        /// <param name="historyLimit">服务端保留的历史消息条数，0 表示不保留。</param>
        /// <param name="clientHistoryLimit">客户端保留的历史消息条数。</param>
        /// <param name="participantLimit">房间同时在线人数上限。</param>
        /// <param name="messageLength">单条消息的最大字符数。</param>
        /// <param name="pendingLimit">正在握手、尚未加入房间的连接上限。</param>
        /// <param name="outboundLimit">每个连接出站队列的容量。</param>
        /// <param name="sendBurst">单个连接在 10 秒内允许发送的消息条数。</param>
        /// <exception cref="ArgumentOutOfRangeException">任一参数超出允许范围。</exception>
        public ChatOptions(int historyLimit = 100, int clientHistoryLimit = 150, int participantLimit = 30, int messageLength = 500,
            int pendingLimit = 64, int outboundLimit = 160, int sendBurst = 8)
        {
            // OutboundLimit 至少要能装下完整历史再加 4 帧（welcome/state/error/pong 等），
            // 否则新成员加入时出站队列会在补发历史的过程中立即溢出。
            if (historyLimit < 0 || historyLimit > 1000 || clientHistoryLimit < 1 || clientHistoryLimit > 1000 || participantLimit < 1 || participantLimit > 100
                || messageLength < 1 || messageLength > 500 || pendingLimit < 1 || pendingLimit > 128 || outboundLimit < historyLimit + 4 || outboundLimit > 2000 || sendBurst < 1 || sendBurst > 100)
                throw new ArgumentOutOfRangeException("聊天容量设置超出范围。");
            HistoryLimit = historyLimit; ClientHistoryLimit = clientHistoryLimit; ParticipantLimit = participantLimit; MessageLength = messageLength;
            PendingLimit = pendingLimit; OutboundLimit = outboundLimit; SendBurst = sendBurst;
        }
    }
}


