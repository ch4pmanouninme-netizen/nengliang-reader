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
    await LargeEpub();
    Chapters();
    EpubToc();
    TruncatedEdge();
    // A library of its own, so the derived pipe name cannot collide with an installed copy of the
    // reader running on the same machine.
    string controlRoot = Path.Combine(root, "control");
    using (var control = new CancellationTokenSource())
    {
        var listening = new TaskCompletionSource();
        int served = 0;
        _ = ControlChannel.ServeAsync(controlRoot, _ => { served++; return Task.FromResult(served == 1 ? ControlChannel.Accepted : ControlChannel.Refused); }, control.Token, () => listening.TrySetResult());
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(await ControlChannel.TrySendAsync(controlRoot, ControlChannel.ShutdownCommand, TimeSpan.FromSeconds(10)) == ControlChannel.Accepted, "control channel reports a stopped copy");
        Check(await ControlChannel.TrySendAsync(controlRoot, ControlChannel.ShutdownCommand, TimeSpan.FromSeconds(10)) == ControlChannel.Refused, "control channel carries a refusal back to the caller");
        Check(served == 2, "control channel serves repeated requests");
        control.Cancel();
    }
    Check(await ControlChannel.TrySendAsync(controlRoot, ControlChannel.ShutdownCommand, TimeSpan.FromSeconds(2)) == null, "control channel reports silence once the server is gone");
    // A copy too old to serve the channel still holds the lock. That is the only way the launcher
    // can tell "nothing is running" apart from "something is running but will not answer".
    Directory.CreateDirectory(controlRoot);
    Check(!ControlChannel.LockHeld(controlRoot), "an unlocked library is reported as free");
    using (var held = new FileStream(Path.Combine(controlRoot, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        Check(ControlChannel.LockHeld(controlRoot), "a held library is reported as busy");
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
    string elsewhere = Path.Combine(root, "elsewhere");
    string packagedLegacy = Path.Combine(root, "legacy-pack");
    Directory.CreateDirectory(Path.Combine(packagedLegacy, "data"));
    File.WriteAllText(Path.Combine(packagedLegacy, "data", "state.xml"), "<preferences/>");
    string resolvedPackage = DataPaths.ResolveLegacyRoot(packagedLegacy, elsewhere);
    Check(resolvedPackage == Path.Combine(packagedLegacy, "data"), "entry shell resolves the packaged data directory");
    string fromPackage = Path.Combine(root, "migrated-from-package");
    DataPaths.MigrateLegacy(resolvedPackage, fromPackage);
    Check(File.Exists(Path.Combine(fromPackage, "state.xml")) && File.Exists(Path.Combine(fromPackage, "state.legacy-backup.xml")), "entry shell legacy root migrates the packaged library");
    string flatLegacy = Path.Combine(root, "legacy-flat");
    Directory.CreateDirectory(flatLegacy);
    File.WriteAllText(Path.Combine(flatLegacy, "state.xml"), "<preferences/>");
    Check(DataPaths.ResolveLegacyRoot(flatLegacy, elsewhere) == flatLegacy, "legacy root resolves a flat data directory");
    string emptyLegacy = Path.Combine(root, "legacy-empty");
    Directory.CreateDirectory(emptyLegacy);
    Check(DataPaths.ResolveLegacyRoot(emptyLegacy, elsewhere) == emptyLegacy, "absent legacy library falls back to the given root");
    Check(DataPaths.ResolveLegacyRoot(null, packagedLegacy) == Path.Combine(packagedLegacy, "data"), "unspecified legacy root still inspects the assembly directory");
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

static void Chapters()
{
    string text = "前言文字\n第一章 初见\n正文一\n第一回合 他输了\n第一节课 开始了\n第2章 重逢\n正文二\nChapter 3 Again\n正文三\n番外 后来\n正文四";
    var found = ChapterIndex.Find(text);
    Check(found.Select(chapter => chapter.Title).SequenceEqual(new[] { "开头", "第一章 初见", "第2章 重逢", "Chapter 3 Again", "番外 后来" }), "chapter headings are detected in order without false positives");
    Check(text.Substring(found[1].Position).StartsWith("第一章 初见", StringComparison.Ordinal), "chapter position points at the heading");
    Check(ChapterIndex.Find("作品相关\n作者的话\n完本感言").Select(chapter => chapter.Title).SequenceEqual(new[] { "作品相关", "完本感言" }), "section headings are detected");
    Check(ChapterIndex.Find("第一章 A\n第一章 A\n正文\n第二章 B").Count == 2, "adjacent duplicate headings keep one entry");
    Check(ChapterIndex.Find("第3章 请假一天，顺便小聊几句。\n正文\n第4章 归来").Select(chapter => chapter.Title).SequenceEqual(new[] { "第3章 请假一天，顺便小聊几句。", "第4章 归来" }), "a title ending with a period is kept when the heading has a separator");
    Check(ChapterIndex.Find("第一章 开始\n正文\n第一章的内容很精彩。\n第二章 续").Select(chapter => chapter.Title).SequenceEqual(new[] { "第一章 开始", "第二章 续" }), "chapter-like sentences are not headings");
    Check(ChapterIndex.Find("只有正文\n没有任何章节标题").Count == 0, "a book without headings has no chapter index");
}

static void TruncatedEdge()
{
    string root = Path.Combine(Path.GetTempPath(), "epub-edge-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        string path = Path.Combine(root, "edge.epub");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Entry(string name, string body) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(body); }
            Entry("META-INF/container.xml", "<container><rootfile full-path='content.opf'/></container>");
            Entry("content.opf", "<package><metadata><title>边界</title></metadata><manifest><item id='a' href='a.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='a'/></spine></package>");
            // 79 个 ASCII 字符后紧跟一个 emoji（UTF-16 代理对）；80 字符截断正好会切成半个。
            Entry("a.xhtml", "<html><body><p>" + new string('a', 79) + "😊tail</p></body></html>");
        }
        var book = Importer.Read(path);
        Check(book.Chapters != null && book.Chapters.Count == 1, "surrogate edge fixture yields one chapter");
        string title = book.Chapters[0].Title;
        Check(title.Length == 79 && !Char.IsHighSurrogate(title[^1]), "truncated chapter title never ends on a lone high surrogate");
    }
    finally { Directory.Delete(root, true); }
}

static void EpubToc()
{
    string root = Path.Combine(Path.GetTempPath(), "epub-toc-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        // EPUB2：toc.ncx。目录标题故意与正文标题不同，证明用的是书内目录而不是首行猜测。
        string ncxPath = Path.Combine(root, "ncx.epub");
        using (var zip = ZipFile.Open(ncxPath, ZipArchiveMode.Create))
        {
            void Entry(string name, string body) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(body); }
            Entry("META-INF/container.xml", "<container><rootfile full-path='OEBPS/content.opf'/></container>");
            Entry("OEBPS/content.opf", "<package><metadata><title>NCX 测试</title></metadata><manifest>" +
                "<item id='ncx' href='toc.ncx' media-type='application/x-dtbncx+xml'/>" +
                "<item id='a' href='Text/a.xhtml' media-type='application/xhtml+xml'/>" +
                "<item id='b' href='Text/b.xhtml' media-type='application/xhtml+xml'/>" +
                "</manifest><spine toc='ncx'><itemref idref='a'/><itemref idref='b'/></spine></package>");
            Entry("OEBPS/toc.ncx", "<ncx xmlns='http://www.daisy.org/z3986/2005/ncx/'><navMap>" +
                "<navPoint><navLabel><text>目录甲</text></navLabel><content src='Text/a.xhtml'/></navPoint>" +
                "<navPoint><navLabel><text>目录乙</text></navLabel><content src='Text/b.xhtml'/></navPoint>" +
                "</navMap></ncx>");
            Entry("OEBPS/Text/a.xhtml", "<html><body><h2>第一章 起</h2><p>正文一</p></body></html>");
            Entry("OEBPS/Text/b.xhtml", "<html><body><h2>第二章 承</h2><p>正文二</p></body></html>");
        }
        var ncx = Importer.Read(ncxPath);
        Check(ncx.Chapters != null && ncx.Chapters.Count == 2, "EPUB NCX produces a chapter index");
        Check(ncx.Chapters[0].Title == "目录甲" && ncx.Chapters[1].Title == "目录乙", "EPUB NCX titles are used");
        Check(ncx.Text.Substring(ncx.Chapters[1].Position).StartsWith("第二章 承", StringComparison.Ordinal), "EPUB NCX position points at the chapter");

        // EPUB3：nav.xhtml。
        string navPath = Path.Combine(root, "nav.epub");
        using (var zip = ZipFile.Open(navPath, ZipArchiveMode.Create))
        {
            void Entry(string name, string body) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(body); }
            Entry("META-INF/container.xml", "<container><rootfile full-path='OEBPS/content.opf'/></container>");
            Entry("OEBPS/content.opf", "<package><metadata><title>NAV 测试</title></metadata><manifest>" +
                "<item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>" +
                "<item id='a' href='Text/a.xhtml' media-type='application/xhtml+xml'/>" +
                "<item id='b' href='Text/b.xhtml' media-type='application/xhtml+xml'/>" +
                "</manifest><spine><itemref idref='a'/><itemref idref='b'/></spine></package>");
            Entry("OEBPS/nav.xhtml", "<html xmlns:epub='http://www.idpf.org/2007/ops'><body><nav epub:type='toc'><ol>" +
                "<li><a href='Text/a.xhtml'>导航一</a></li><li><a href='Text/b.xhtml'>导航二</a></li>" +
                "</ol></nav></body></html>");
            Entry("OEBPS/Text/a.xhtml", "<html><body><h2>第一章 起</h2><p>正文一</p></body></html>");
            Entry("OEBPS/Text/b.xhtml", "<html><body><h2>第二章 承</h2><p>正文二</p></body></html>");
        }
        var nav = Importer.Read(navPath);
        Check(nav.Chapters != null && nav.Chapters.Count == 2, "EPUB nav produces a chapter index");
        Check(nav.Chapters[0].Title == "导航一" && nav.Chapters[1].Title == "导航二", "EPUB nav titles are used");

        // sidecar 持久化与书库备份往返。
        string libraryRoot = Path.Combine(root, "library");
        var store = new Store(libraryRoot);
        store.Add("带目录的书", "第一章\n正文\n第二章\n正文", "EPUB", new[] { new Chapter("第一章", 0), new Chapter("第二章", "第一章\n正文\n".Length) });
        store.Save();
        var reopened = new Store(libraryRoot);
        var stored = reopened.ChaptersFor(reopened.Books[0]);
        Check(stored != null && stored.Count == 2 && stored[1].Title == "第二章", "chapter sidecar survives restart");
        string backup = Path.Combine(root, "chapters.zip");
        LibraryArchive.Export(reopened, backup);
        var destination = new Store(Path.Combine(root, "restored"));
        Check(LibraryArchive.Import(destination, backup) == 1, "archive imports a book with a chapter index");
        var restoredChapters = destination.ChaptersFor(destination.Books[0]);
        Check(restoredChapters != null && restoredChapters.Count == 2, "archive preserves the chapter index");
    }
    finally { Directory.Delete(root, true); }
}

static async Task LargeEpub()
{
    string path = Path.Combine(Path.GetTempPath(), "large-" + Guid.NewGuid().ToString("N") + ".epub");
    try
    {
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Entry(string name, string body) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(body); }
            Entry("META-INF/container.xml", "<container><rootfile full-path='book.opf'/></container>");
            Entry("book.opf", "<package><metadata><title>大文件</title></metadata><manifest><item id='a' href='a.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='a'/></spine></package>");
            // 25 MB 的标签属性：解压后超过旧的 24 MB 上限，但去掉标记后正文只有一句话。
            // 这正是一本正常长篇小说的形态——XHTML 标记占体积，正文长度远低于上限。
            // 用属性而不是注释块，是为了让正则走线性的字符类匹配，而不是 25 MB 的惰性回溯。
            using (var writer = new StreamWriter(zip.CreateEntry("a.xhtml").Open(), new UTF8Encoding(false)))
            {
                writer.Write("<html><body><div data-filler=\"");
                char[] chunk = new char[8192];
                Array.Fill(chunk, 'x');
                long written = 0;
                while (written < 25L * 1024 * 1024) { writer.Write(chunk); written += chunk.Length; }
                writer.Write("\"><p>正文</p></div></body></html>");
            }
        }
        var reports = new List<ImportProgress>();
        var imported = await Task.Run(() => Importer.Read(path, new SyncProgress<ImportProgress>(reports.Add)));
        Check(imported.Text == "正文", "EPUB expanded content above the old 24 MB limit imports");
        Check(reports.Count == 1 && reports[0].Completed == 1 && reports[0].Total == 1, "EPUB import reports spine progress");
    }
    finally { File.Delete(path); }
}

sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
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
