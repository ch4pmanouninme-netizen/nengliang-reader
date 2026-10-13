using System.IO.Compression;
using System.Net;
using System.Text;
using Reader.Core;
using Reader.Core.Tests;

string root = Path.Combine(Path.GetTempPath(), "reader-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    if (LegacyTests.Run([Path.Combine(root, "legacy")]) != 0) return 1;
    await Downloads();
    var original = new Store(Path.Combine(root, "original"));
    var book = original.Add("跨平台测试", "第一段😊\n第二段内容", "TXT");
    book.Position = 3; book.Bookmark = 6;
    original.Pref.HasPosition = true; original.Pref.X = -1280; original.Pref.Y = -150;
    original.Pref.ChatHost = "127.0.0.1"; original.Pref.ChatPort = 38417; original.Pref.ChatNick = "测试昵称";
    original.Save();
    var restoredPosition = new Store(original.Root);
    Check(restoredPosition.Pref.HasPosition && restoredPosition.Pref.X == -1280, "negative monitor coordinates survive restart");
    Check(restoredPosition.Pref.ChatHost == "127.0.0.1" && restoredPosition.Pref.ChatNick == "测试昵称" && restoredPosition.Pref.ChatPort == 38417, "upstream chat preferences survive restart");
    string archive = Path.Combine(root, "backup.zip"); LibraryArchive.Export(original, archive);
    var destination = new Store(Path.Combine(root, "restored"));
    Check(LibraryArchive.Import(destination, archive) == 1, "library archive imports book");
    Check(destination.Books[0].Position == 3 && destination.Books[0].Bookmark == 6, "archive retains progress and bookmark");
    destination.Books[0].Position = 1;
    Check(LibraryArchive.Import(destination, archive) == 0 && destination.Books[0].Position == 1, "archive merge preserves current progress of duplicate book");
    string malicious = Path.Combine(root, "bad.zip");
    using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create)) zip.CreateEntry("../outside.txt");
    Reject(() => LibraryArchive.Import(destination, malicious), "archive path traversal rejected");
    string partial = Path.Combine(root, "partial");
    Directory.CreateDirectory(Path.Combine(partial, "books"));
    File.Copy(Path.Combine(original.Root, "books", book.Id + ".txt"), Path.Combine(partial, "books", book.Id + ".txt"));
    DataPaths.MigrateLegacy(original.Root, partial);
    Check(new Store(partial).Books.Count == 1, "interrupted legacy migration can be retried");
    string brokenPrimary = Path.Combine(root, "broken-primary");
    var recoverable = new Store(brokenPrimary);
    var recoveryBook = recoverable.Add("恢复书籍", "一二三四五六七八九", "TXT");
    recoveryBook.Position = 3; recoverable.Save(); recoveryBook.Position = 5; recoverable.Save();
    File.WriteAllText(Path.Combine(brokenPrimary, "state.xml"), "broken");
    string migratedRecovery = Path.Combine(root, "migrated-recovery");
    DataPaths.MigrateLegacy(brokenPrimary, migratedRecovery);
    var recovery = new Store(migratedRecovery);
    Check(recovery.Books[0].Title == "恢复书籍" && recovery.Books[0].Position == 3, "legacy migration retains recoverable backup progress");
    string incomplete = Path.Combine(root, "incomplete.zip");
    using (var zip = ZipFile.Open(incomplete, ZipArchiveMode.Create)) zip.CreateEntryFromFile(Path.Combine(original.Root, "state.xml"), "state.xml");
    int beforeCount = destination.Books.Count;
    Reject(() => LibraryArchive.Import(destination, incomplete), "incomplete archive rejected before merging");
    Check(destination.Books.Count == beforeCount && destination.Books[0].Position == 1, "failed archive validation leaves destination unchanged");
    var excessive = new Store(Path.Combine(root, "excessive"));
    excessive.Books.AddRange(Enumerable.Repeat(book, 5000));
    Reject(() => LibraryArchive.Export(excessive, Path.Combine(root, "excessive.zip")), "export obeys importer entry limit");
    Check(!File.Exists(Path.Combine(root, "excessive.zip")), "rejected export does not create unusable archive");
    book.Position = 2;
    Task firstSave = original.SaveAsync();
    book.Position = 5;
    await firstSave;
    Check(new Store(original.Root).Books[0].Position == 2, "async persistence captures state before yielding");
    Task nextSave = original.SaveAsync(); book.Position = 6; Task lastSave = original.SaveAsync();
    await Task.WhenAll(nextSave, lastSave);
    Check(new Store(original.Root).Books[0].Position == 6, "serialized async writes retain newest progress");
    await BookLibraryTests.Run(Path.Combine(root, "book-library"));
    Console.WriteLine("All core and download regression tests passed.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally { Directory.Delete(root, true); }

static void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
}
static void Reject(Action action, string label)
{
    try { action(); } catch (InvalidDataException) { Check(true, label); return; }
    throw new Exception("FAIL: " + label);
}
static async Task RejectDownload(DownloadService service, string address, string label)
{
    try { await service.DownloadAsync(address, null, CancellationToken.None); }
    catch (InvalidDataException) { Check(true, label); return; }
    throw new Exception("FAIL: " + label);
}
static async Task Downloads()
{
    using var valid = new DownloadService(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("第一章\n测试正文😊", Encoding.UTF8, "text/plain") })));
    var imported = await valid.DownloadAsync("https://example.test/novel.txt", null, CancellationToken.None);
    Check(imported.Text == "第一章\n测试正文😊" && imported.Title == "novel", "TXT download validates and imports content");
    await RejectDownload(valid, "file:///etc/passwd", "non-HTTP download rejected");
    using var html = new DownloadService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Login</html>", Encoding.UTF8, "text/plain") })));
    await RejectDownload(html, "https://example.test/fake.txt", "HTML disguised as text rejected");
    int attempts = 0;
    using var redirect = new DownloadService(new Handler((_, _) =>
    {
        attempts++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("/loop", UriKind.Relative); return Task.FromResult(response);
    }));
    await RejectDownload(redirect, "https://example.test/loop", "redirect loop bounded");
    Check(attempts == 6, "only five redirects followed");
    using var large = new DownloadService(new Handler((_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = 101L * 1024 * 1024; return Task.FromResult(response);
    }));
    await RejectDownload(large, "https://example.test/large.txt", "oversized response rejected before reading");
    using var unknownLength = new DownloadService(new Handler((_, _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new RepeatedStream(101L * 1024 * 1024)) })));
    await RejectDownload(unknownLength, "https://example.test/large.txt", "unknown-length response is bounded while streaming");
    using var truncated = new DownloadService(new Handler((_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("正文", Encoding.UTF8, "text/plain") };
        response.Content.Headers.ContentLength = 100; return Task.FromResult(response);
    }));
    await RejectDownload(truncated, "https://example.test/book.txt", "incomplete content length is rejected");
    using var epubBytes = new MemoryStream();
    using (var zip = new ZipArchive(epubBytes, ZipArchiveMode.Create, true))
    {
        void Entry(string name, string body) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(body); }
        Entry("META-INF/container.xml", "<container><rootfile full-path='book.opf'/></container>");
        Entry("book.opf", "<package><metadata><title>下载 EPUB</title></metadata><manifest><item id='a' href='a.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='a'/></spine></package>");
        Entry("a.xhtml", "<html><body><p>EPUB 正文</p></body></html>");
    }
    using var epubDownload = new DownloadService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(epubBytes.ToArray()) })));
    var downloadedEpub = await epubDownload.DownloadAsync("https://example.test/download?id=1", null, CancellationToken.None);
    Check(downloadedEpub.Title == "下载 EPUB" && downloadedEpub.Text == "EPUB 正文", "EPUB without filename extension identified and imported");
    using var blocked = new DownloadService(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(HttpStatusCode.OK); }));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    try { await blocked.DownloadAsync("https://example.test/book.txt", null, cancellation.Token); throw new Exception("cancel failed"); }
    catch (OperationCanceledException) { Check(true, "download cancellation propagates"); }
}

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
}

sealed class RepeatedStream(long remaining) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int length = (int)Math.Min(count, remaining); Array.Fill(buffer, (byte)'a', offset, length); remaining -= length; return length;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); int length = (int)Math.Min(buffer.Length, remaining); buffer.Span[..length].Fill((byte)'a'); remaining -= length; return ValueTask.FromResult(length);
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
