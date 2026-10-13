using System.Net;
using System.Net.Http;
using System.Text;

namespace Reader.Core;

public record DownloadProgress(long Bytes, long? Total);

public sealed class DownloadService : IDisposable
{
    private const long MaximumBytes = 100L * 1024 * 1024;
    private readonly HttpClient client;

    public DownloadService(HttpMessageHandler handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NengliangReader/2.0");
    }

    public async Task<ImportedBook> DownloadAsync(string address, IProgress<DownloadProgress> progress, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || !Allowed(uri))
            throw new InvalidDataException("请输入 HTTP 或 HTTPS 文件下载链接。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        string temporary = Path.Combine(Path.GetTempPath(), "NengliangReader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
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
                if (total > MaximumBytes) throw new InvalidDataException("下载文件超过 100 MB。");
                string media = response.Content.Headers.ContentType?.MediaType ?? "";
                if (media.Contains("html", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("该链接返回网页，请复制 TXT 或 EPUB 文件下载链接。");
                string remoteName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
                // Remote names are display text only, never filesystem paths.
                remoteName = remoteName.Trim('"').Replace('\\', '/').Split('/').Last();
                string raw = Path.Combine(temporary, "download.bin");
                long received = 0;
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
                if (total.HasValue && received != total.Value) throw new InvalidDataException("下载未完成，请重试。");
                byte[] prefix = new byte[1024];
                int length;
                using (var file = File.OpenRead(raw)) length = file.Read(prefix, 0, prefix.Length);
                bool zip = length >= 4 && prefix[0] == 0x50 && prefix[1] == 0x4b && prefix[2] == 3 && prefix[3] == 4;
                string leading = Encoding.UTF8.GetString(prefix, 0, length).TrimStart('\ufeff', ' ', '\t', '\r', '\n');
                if (!zip && (leading.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || leading.StartsWith("<html", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("下载内容是网页，无法导入。");
                if (!zip && !media.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) && !remoteName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("无法识别为 TXT 或 EPUB 文件。");
                string ready = Path.Combine(temporary, zip ? "book.epub" : "book.txt");
                File.Move(raw, ready);
                timeout.Token.ThrowIfCancellationRequested();
                var book = await Task.Run(() => Importer.Read(ready), timeout.Token).ConfigureAwait(false);
                if (!zip) book.Title = string.IsNullOrWhiteSpace(remoteName) ? "下载的书籍" : Path.GetFileNameWithoutExtension(remoteName);
                timeout.Token.ThrowIfCancellationRequested();
                return book;
            }
            throw new InvalidDataException("下载失败。");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("下载超过 5 分钟，请重试。");
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static bool Allowed(Uri uri) => (uri.Scheme == "http" || uri.Scheme == "https") && string.IsNullOrEmpty(uri.UserInfo);
    public void Dispose() => client.Dispose();
}
