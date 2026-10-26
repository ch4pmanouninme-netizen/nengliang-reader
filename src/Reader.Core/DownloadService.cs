using System.Net;
using System.Net.Http;
using System.Text;

namespace Reader.Core;

/// <summary>下载进度。</summary>
/// <param name="Bytes">已接收的字节数。</param>
/// <param name="Total">服务器声明的总字节数；未知时为 null。</param>
public record DownloadProgress(long Bytes, long? Total);

/// <summary>
/// 下载 TXT/EPUB 并交给 <see cref="Importer"/> 解析。只允许 HTTP(S) 链接并手动逐跳跟随重定向；
/// 体积上限 100 MB，整体超时 5 分钟。
/// </summary>
public sealed class DownloadService : IDisposable
{
    // 声明大小与实际接收字节都会检查，防止分块响应或伪造 Content-Length 绕过限制。
    private const long MaximumBytes = 100L * 1024 * 1024;
    private readonly HttpClient client;

    /// <summary>创建下载服务。</summary>
    /// <param name="handler">可选的 HTTP 处理器，测试时注入；null 时使用不自动跟随重定向的默认处理器。</param>
    public DownloadService(HttpMessageHandler handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NengliangReader/2.0");
    }

    /// <summary>
    /// 下载并导入一个 TXT/EPUB。下载阶段通过 <paramref name="progress"/> 回报字节数；
    /// 下载完成后若是 EPUB，解析阶段通过 <paramref name="importProgress"/> 回报章节进度。
    /// </summary>
    /// <param name="address">HTTP/HTTPS 文件链接；拒绝带用户信息的地址。</param>
    /// <param name="progress">下载字节进度回调；可为 null。</param>
    /// <param name="cancellation">外部取消令牌。</param>
    /// <param name="importProgress">解析进度回调；可为 null。</param>
    /// <returns>已解析、尚未写入书库的书籍。</returns>
    /// <remarks>内部另有 5 分钟总超时；临时文件无论成功失败都会被清理。</remarks>
    /// <exception cref="InvalidDataException">链接非法、内容超限或无法识别为 TXT/EPUB。</exception>
    /// <exception cref="TimeoutException">下载超过 5 分钟。</exception>
    public async Task<ImportedBook> DownloadAsync(string address, IProgress<DownloadProgress> progress, CancellationToken cancellation, IProgress<ImportProgress> importProgress = null)
    {
        // 先校验协议与用户信息，避免把 file://、内嵌凭据等内容交给 HttpClient。
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || !Allowed(uri))
            throw new InvalidDataException("请输入 HTTP 或 HTTPS 文件下载链接。");
        // 链接外部令牌再加 5 分钟内部超时，后面据此区分用户取消与超时。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        // 每次下载使用独立临时目录，结束后整体删除。
        string temporary = Path.Combine(Path.GetTempPath(), "NengliangReader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            // 手动跟随重定向并逐跳校验，避免 HttpClient 自动跳到非 HTTP(S) 地址。
            for (int redirects = 0; redirects <= 5; redirects++)
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects == 5 || response.Headers.Location == null) throw new InvalidDataException("下载链接重定向次数过多或缺少目标地址。");
                    uri = new Uri(uri, response.Headers.Location);
                    if (!Allowed(uri)) throw new InvalidDataException("不支持此重定向地址。");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                // 声明大小超限直接拒绝；实际接收时还会再检查一次。
                if (total > MaximumBytes) throw new InvalidDataException("下载文件超过 100 MB。");
                string media = response.Content.Headers.ContentType?.MediaType ?? "";
                // 有的下载站先用 200 返回网页登录/错误页，按 Content-Type 先挡一次。
                if (media.Contains("html", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("该链接返回网页，请复制 TXT 或 EPUB 文件下载链接。");
                string remoteName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
                // Remote names are display text only, never filesystem paths.
                remoteName = remoteName.Trim('"').Replace('\\', '/').Split('/').Last();
                string raw = Path.Combine(temporary, "download.bin");
                long received = 0;
                // 边读边写临时文件并回报进度；累计字节超限立即中止。
                await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var output = new FileStream(raw, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    int count;
                    while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        received += count;
                        if (received > MaximumBytes) throw new InvalidDataException("下载文件超过 100 MB。");
                        await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                        progress?.Report(new DownloadProgress(received, total));
                    }
                }
                // 声明长度与实际不符说明连接中途断开；分块响应没有声明长度时跳过此检查。
                if (total.HasValue && received != total.Value) throw new InvalidDataException("下载未完成，请重试。");
                byte[] prefix = new byte[1024];
                int length;
                using (var file = File.OpenRead(raw)) length = file.Read(prefix, 0, prefix.Length);
                // 用 ZIP 文件头判断 EPUB，不信任扩展名或 Content-Type。
                bool zip = length >= 4 && prefix[0] == 0x50 && prefix[1] == 0x4b && prefix[2] == 3 && prefix[3] == 4;
                string leading = Encoding.UTF8.GetString(prefix, 0, length).TrimStart('\ufeff', ' ', '\t', '\r', '\n');
                // 再按内容判断一次，防止服务器把网页伪装成 text/plain。
                if (!zip && (leading.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || leading.StartsWith("<html", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("下载内容是网页，无法导入。");
                // 既不是 ZIP 也不是纯文本时拒绝，避免把二进制文件当 TXT 导入。
                if (!zip && !media.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) && !remoteName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("无法识别为 TXT 或 EPUB 文件。");
                string ready = Path.Combine(temporary, zip ? "book.epub" : "book.txt");
                // 改成与内容一致的扩展名，Importer 依赖扩展名分派 TXT/EPUB。
                File.Move(raw, ready);
                timeout.Token.ThrowIfCancellationRequested();
                // 解析放到线程池执行，避免大 EPUB 的同步 CPU 工作阻塞调用方线程。
                var book = await Task.Run(() => Importer.Read(ready, importProgress), timeout.Token).ConfigureAwait(false);
                // TXT 用远端文件名作标题；EPUB 保留书内 <title>。
                if (!zip) book.Title = string.IsNullOrWhiteSpace(remoteName) ? "下载的书籍" : Path.GetFileNameWithoutExtension(remoteName);
                timeout.Token.ThrowIfCancellationRequested();
                return book;
            }
            throw new InvalidDataException("下载失败。");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // 链接令牌区分“外部取消”和“内部 5 分钟超时”：只有后者转成 TimeoutException。
            throw new TimeoutException("下载超过 5 分钟，请重试。");
        }
        finally
        {
            // 清理失败不能掩盖真正的下载结果（成功或失败都一样）。
            try { Directory.Delete(temporary, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // 只允许 HTTP(S)，并拒绝 URL 内嵌用户名/密码。
    private static bool Allowed(Uri uri) => (uri.Scheme == "http" || uri.Scheme == "https") && string.IsNullOrEmpty(uri.UserInfo);
    /// <summary>释放内部 <see cref="HttpClient"/>。</summary>
    public void Dispose() => client.Dispose();
}
