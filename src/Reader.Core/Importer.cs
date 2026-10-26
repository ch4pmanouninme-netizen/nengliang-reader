using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
namespace Reader.Core;

/// <summary>
/// TXT / EPUB 导入。所有体积与长度限制都在这里统一判断，调用方拿到的
/// <see cref="ImportedBook.Text"/> 已经是规范化后的纯文本。
/// </summary>
public static class Importer
{
    static Importer() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

    // TXT 上限按文件字节数计。中文 UTF-8 约 3 字节/字，24 MB 约合 800 万字。
    // TXT 必须整文件读入才能可靠识别编码，所以限制比 EPUB 的正文上限更紧。
    private const int TxtFileLimit = 24 * 1024 * 1024;

    // EPUB 压缩包本身的体积上限，防止选择/下载阶段就占满磁盘。
    private const int EpubFileLimit = 100 * 1024 * 1024;

    // EPUB 解压后允许读取的总字节数（含 XHTML 标签、container.xml、OPF 等）。
    // 这是 zip bomb 防护，不是正文长度限制：章节逐章读取、读完即弃，
    // 峰值内存不随这个总量线性增长。中文长篇（1200 余章）解压后约 40 MB，
    // 旧的 24 MB 上限会误伤这类正常书籍，因此放宽到 128 MB。
    private const long EpubExpandedLimit = 128L * 1024 * 1024;

    // 最终正文上限，单位是 UTF-16 字符数（不是字节数）。ReadingSession 会把
    // 整本书放进内存参与分页，所以这个值同时也是内存约束。
    private const int TextCharLimit = 24 * 1024 * 1024;

    // container.xml / OPF 等索引文件通常只有几十 KB；单独设限，避免误用正文上限。
    private const int XmlLimit = 4 * 1024 * 1024;

    /// <summary>
    /// 读取一本书。<paramref name="progress"/> 为 null 时不做进度回报。
    /// EPUB 按 spine 顺序逐章回报（单位：章），TXT 只有开始/结束两拍（单位：1）。
    /// 回调可能发生在后台线程，桌面端用 <c>Progress&lt;T&gt;</c> 切回 UI 线程。
    /// </summary>
    public static ImportedBook Read(string path, IProgress<ImportProgress> progress = null)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".epub") return ReadEpub(path, progress);
        if (extension != ".txt") throw new InvalidDataException("目前支持 .txt 和 .epub 文件。");
        if (new FileInfo(path).Length > TxtFileLimit) throw new InvalidDataException("TXT 文件超过 24 MB，请先拆分文件。");
        progress?.Report(new ImportProgress(0, 1));
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        // 优先按 BOM 识别 UTF-16；没有 BOM 时先严格按 UTF-8 解码，
        // 失败再退回 GB18030（覆盖 GBK/GB2312 的常见中文文本）。
        if (bytes.Length >= 2 && ((bytes[0] == 255 && bytes[1] == 254) || (bytes[0] == 254 && bytes[1] == 255)))
            text = (bytes[0] == 255 ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes, 2, bytes.Length - 2);
        else
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { text = Encoding.GetEncoding(54936).GetString(bytes); }
        }
        if (text.IndexOf('\0') >= 0) throw new InvalidDataException("文件包含二进制数据，无法按 TXT 阅读。");
        ImportedBook book = new ImportedBook { Title = Path.GetFileNameWithoutExtension(path), Text = Normalize(text), Format = "TXT" };
        progress?.Report(new ImportProgress(1, 1));
        return book;
    }

    /// <summary>
    /// 统一换行、合并行内空白、去掉空行。该函数是幂等的，因此 EPUB 可以
    /// 逐章规范化后再拼接，不需要对整本书再做第二遍。
    /// </summary>
    public static string Normalize(string text)
    {
        text = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
        return String.Join("\n", text.Split('\n').Select(line => Regex.Replace(line, "[\\t \\u00a0\\u3000]+", " ").Trim()).Where(line => line.Length > 0));
    }

    /// <summary>解析 EPUB 内的 XML 索引；禁用 DTD 与外部解析器，避免 XXE 与外部实体。</summary>
    public static XDocument ReadXml(string text)
    {
        XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = XmlLimit };
        using (StringReader sr = new StringReader(text))
        using (XmlReader reader = XmlReader.Create(sr, settings)) return XDocument.Load(reader);
    }

    /// <summary>
    /// 读取并解压一个 ZIP 条目。<paramref name="total"/> 在读取前先累加该条目
    /// 声明的解压大小，超限时不会把半个条目留在内存里。
    /// </summary>
    private static string EntryText(ZipArchive archive, string path, ref long total)
    {
        ZipArchiveEntry entry = archive.GetEntry(path);
        if (entry == null) throw new InvalidDataException("EPUB 内缺少文件：" + path);
        if (entry.Length > EpubExpandedLimit || (total += entry.Length) > EpubExpandedLimit)
            throw new InvalidDataException("EPUB 解压后超过 128 MB，无法导入。");
        using (Stream stream = entry.Open())
        using (MemoryStream buffer = new MemoryStream())
        {
            byte[] bytes = new byte[8192]; int count;
            while ((count = stream.Read(bytes, 0, bytes.Length)) > 0)
            {
                // ZIP 目录里的 Length 理论上可信，这里仍按实际读到的字节兜底，
                // 防止伪造/损坏的压缩包声明的大小与实际内容不一致。
                if (buffer.Length + count > EpubExpandedLimit) throw new InvalidDataException("EPUB 解压后超过 128 MB，无法导入。");
                buffer.Write(bytes, 0, count);
            }
            buffer.Position = 0;
            // 打开 BOM 探测：XHTML 通常是无 BOM 的 UTF-8，同时兼容 UTF-16。
            using (StreamReader reader = new StreamReader(buffer, Encoding.UTF8, true)) return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// 把 OPF 中的相对 href 解析成压缩包内路径。绝对地址、协议相对地址（//host）
    /// 以及逃逸出根目录的路径一律拒绝，避免读取压缩包之外的内容。
    /// </summary>
    public static string ResolveEntry(string basePath, string href)
    {
        Uri absolute;
        if (String.IsNullOrWhiteSpace(href) || Uri.TryCreate(href, UriKind.Absolute, out absolute) || href.StartsWith("//")) throw new InvalidDataException("EPUB 包含不支持的外部章节地址。");
        Uri resolved = new Uri(new Uri("https://epub.invalid/" + basePath), href);
        if (resolved.Host != "epub.invalid") throw new InvalidDataException("无效的章节路径。");
        return Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
    }

    /// <summary>
    /// 按 OPF spine 顺序提取正文。只保留纯文本：script/style/图片/外部资源都不会
    /// 进入正文，也不会创建内嵌浏览器。整本书最后才组装成一次字符串。
    /// </summary>
    private static ImportedBook ReadEpub(string path, IProgress<ImportProgress> progress)
    {
        if (new FileInfo(path).Length > EpubFileLimit) throw new InvalidDataException("EPUB 文件超过 100 MB。");
        using (ZipArchive zip = ZipFile.OpenRead(path))
        {
            if (zip.Entries.Count > 12000) throw new InvalidDataException("EPUB 内文件数量过多。");
            long total = 0;
            XDocument container = ReadXml(EntryText(zip, "META-INF/container.xml", ref total));
            XElement rootfile = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile");
            string opfPath = rootfile == null ? null : (string)rootfile.Attribute("full-path");
            if (String.IsNullOrWhiteSpace(opfPath)) throw new InvalidDataException("EPUB 缺少书籍索引。");
            XDocument opf = ReadXml(EntryText(zip, opfPath, ref total));
            XElement title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title");

            // manifest：id → 压缩包内路径。只收 HTML / 纯文本，图片、字体、CSS 不参与正文。
            Dictionary<string, string> manifest = new Dictionary<string, string>();
            foreach (XElement item in opf.Descendants().Where(e => e.Name.LocalName == "item"))
            {
                string id = (string)item.Attribute("id"), href = (string)item.Attribute("href");
                string type = (string)item.Attribute("media-type") ?? "";
                if (id != null && href != null && (type.Contains("html") || type == "text/plain")) manifest[id] = ResolveEntry(opfPath, href);
            }

            // 目录优先于标题启发式：书内自带 nav.xhtml（EPUB3）或 toc.ncx（EPUB2）时，
            // 直接按官方目录建章节索引，不再依赖“第X章”这种排版习惯。
            // 目录文件单独按 XmlLimit 限长，不占用正文的解压预算。
            string ReadTocFile(string entryPath)
            {
                ZipArchiveEntry entry = zip.GetEntry(entryPath);
                if (entry == null) throw new InvalidDataException("EPUB 内缺少文件：" + entryPath);
                if (entry.Length > XmlLimit) throw new InvalidDataException("EPUB 目录文件过大。");
                using (Stream stream = entry.Open())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
            }
            List<EpubChapters.TocEntry> toc = EpubChapters.Read(opf, opfPath, ReadTocFile, ResolveEntry);

            // spine 决定阅读顺序；linear="no" 的条目（封面、目录页等）不进入正文。
            List<string> chapters = new List<string>();
            foreach (XElement itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
            {
                if ((string)itemref.Attribute("linear") == "no") continue;
                string idref = (string)itemref.Attribute("idref") ?? "";
                string chapter;
                if (manifest.TryGetValue(idref, out chapter)) chapters.Add(chapter);
            }

            List<EpubChapters.SpineText> spineTexts = new List<EpubChapters.SpineText>();
            StringBuilder result = new StringBuilder();
            for (int index = 0; index < chapters.Count; index++)
            {
                string html = EntryText(zip, chapters[index], ref total);
                // 提取纯文本。每个正则都带 3 秒超时，防止畸形章节把导入拖死。
                html = Regex.Replace(html, @"<(script|style|head|svg)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(3));
                html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline, TimeSpan.FromSeconds(3));
                html = Regex.Replace(html, @"<(br|hr)\b[^>]*>|</(p|div|h[1-6]|li|section|article|blockquote|tr)\s*>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(3));
                html = Regex.Replace(html, @"<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(3));
                string text = Normalize(WebUtility.HtmlDecode(html));
                // 每章已经 Normalize 过，直接用单个换行拼接即可。若对全书再 Normalize 一次，
                // 等于把上千万字符重新拆行再拼回去，纯属浪费。
                if (text.Length > 0)
                {
                    if (result.Length > 0) result.Append('\n');
                    int textStart = result.Length;
                    result.Append(text);
                    // 记录该 spine 文件在最终正文里的范围，供目录项映射偏移。
                    string firstLine = text;
                    int newline = firstLine.IndexOf('\n');
                    if (newline >= 0) firstLine = firstLine[..newline];
                    if (firstLine.Length > 80) firstLine = firstLine[..80];
                    spineTexts.Add(new EpubChapters.SpineText(chapters[index], textStart, text.Length, firstLine));
                }
                if (result.Length > TextCharLimit) throw new InvalidDataException("EPUB 正文超过 2400 万字符，请先拆分文件。");
                progress?.Report(new ImportProgress(index + 1, chapters.Count));
            }

            string body = result.ToString();
            if (body.Length == 0) throw new InvalidDataException("EPUB 没有可读取的正文，可能是加密书籍或扫描图片版。");
            IReadOnlyList<Chapter> chapterIndex = EpubChapters.Build(toc, spineTexts, body);
            return new ImportedBook { Title = title == null ? Path.GetFileNameWithoutExtension(path) : title.Value.Trim(), Text = body, Format = "EPUB", Chapters = chapterIndex };
        }
    }
}

/// <summary>导入进度。<see cref="Completed"/> / <see cref="Total"/> 对 EPUB 是章节数，对 TXT 固定为 0/1 → 1/1。</summary>
public record ImportProgress(int Completed, int Total);
