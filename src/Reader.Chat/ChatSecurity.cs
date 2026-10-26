using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Reader.Chat
{
    public static class ChatSecurity
    {
        public static X509Certificate2 CreateCertificate()
        {
            using (RSA key = RSA.Create())
            {
                key.KeySize = 2048;
                CertificateRequest request = new CertificateRequest("CN=LiangziWindow", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                using (X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1)))
                {
                    // Schannel requires a user key container; macOS requires its temporary keychain.
                    // Do not set PersistKeySet or install the certificate into a trusted store.
                    byte[] pfx = generated.Export(X509ContentType.Pfx);
                    try { return X509CertificateLoader.LoadPkcs12(pfx, null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet); }
                    finally { CryptographicOperations.ZeroMemory(pfx); }
                }
            }
        }
        public static string Fingerprint(X509Certificate certificate)
            => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        public static bool ValidFingerprint(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
        public static SslStream Client(Stream transport, string expected)
        {
            if (!ValidFingerprint(expected)) throw new AuthenticationException("请粘贴服务电脑提供的完整连接信息，缺少有效服务指纹。");
            SslStream stream = new SslStream(transport, false, (sender, certificate, chain, errors) =>
            {
                if (certificate == null) return false;
                using (X509Certificate2 actual = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()))
                    return actual.NotBefore.ToUniversalTime() <= DateTime.UtcNow && actual.NotAfter.ToUniversalTime() > DateTime.UtcNow
                        && String.Equals(Fingerprint(actual), expected, StringComparison.OrdinalIgnoreCase);
            });
            try { stream.AuthenticateAsClient("LiangziWindow", null, SslProtocols.Tls12, false); return stream; }
            catch { stream.Dispose(); throw; }
        }
        public static SslStream Server(Stream transport, X509Certificate2 certificate)
        {
            SslStream stream = new SslStream(transport, false);
            try { stream.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false); return stream; }
            catch { stream.Dispose(); throw; }
        }
    }

    public sealed class ChatOptions
    {
        public readonly int HistoryLimit, ClientHistoryLimit, ParticipantLimit, MessageLength, PendingLimit, OutboundLimit, SendBurst;
        public ChatOptions(int historyLimit = 100, int clientHistoryLimit = 150, int participantLimit = 30, int messageLength = 500,
            int pendingLimit = 64, int outboundLimit = 160, int sendBurst = 8)
        {
            if (historyLimit < 0 || historyLimit > 1000 || clientHistoryLimit < 1 || clientHistoryLimit > 1000 || participantLimit < 1 || participantLimit > 100
                || messageLength < 1 || messageLength > 500 || pendingLimit < 1 || pendingLimit > 128 || outboundLimit < historyLimit + 4 || outboundLimit > 2000 || sendBurst < 1 || sendBurst > 100)
                throw new ArgumentOutOfRangeException("聊天容量设置超出范围。");
            HistoryLimit = historyLimit; ClientHistoryLimit = clientHistoryLimit; ParticipantLimit = participantLimit; MessageLength = messageLength;
            PendingLimit = pendingLimit; OutboundLimit = outboundLimit; SendBurst = sendBurst;
        }
    }
}


