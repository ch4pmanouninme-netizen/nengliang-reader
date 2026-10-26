using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Reader.Core;

/// <summary>
/// 从 EPUB 自带的目录读取章节标题与目标位置，并映射成导入后纯文本里的字符偏移。
/// EPUB3 优先用 nav.xhtml，EPUB2 退回 toc.ncx；两者都不可用时按 spine 文件边界兜底。
/// 这是“结构优先”的索引：不猜标题格式，所以不受作者排版习惯影响。
/// </summary>
internal static class EpubChapters
{
    internal sealed record TocEntry(string Title, string Path, string Fragment);

    /// <summary>一个 spine 文件在最终纯文本中的起始位置、长度和首行（作为标题兜底）。</summary>
    internal sealed record SpineText(string Path, int Start, int Length, string FirstLine);

    /// <summary>
    /// 读取 nav.xhtml 或 toc.ncx。返回空列表表示书内没有可用目录，调用方应退回 spine 兜底。
    /// <paramref name="readEntry"/> 负责从压缩包读出路径对应的 XML 文本。
    /// </summary>
    internal static List<TocEntry> Read(XDocument opf, string opfPath, Func<string, string> readEntry, Func<string, string> resolve)
    {
        // EPUB3：manifest 里 properties 含 nav 的那一项。
        XElement navItem = opf.Descendants().FirstOrDefault(item =>
            item.Name.LocalName == "item" &&
            ((string)item.Attribute("properties") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav"));
        if (navItem != null)
        {
            string href = (string)navItem.Attribute("href") ?? "";
            string path = TryResolve(resolve, opfPath, href);
            if (path != null)
            {
                string xml = ReadOrNull(readEntry, path);
                if (xml != null)
                {
                    // 目录里的 href 相对目录文件自身解析，而不是相对 OPF。
                    List<TocEntry> entries = ReadNav(xml, path, resolve);
                    if (entries.Count > 0) return entries;
                }
            }
        }

        // EPUB2：media-type 为 application/x-dtbncx+xml 的 NCX。
        XElement ncxItem = opf.Descendants().FirstOrDefault(item =>
            item.Name.LocalName == "item" &&
            String.Equals((string)item.Attribute("media-type"), "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase));
        if (ncxItem != null)
        {
            string href = (string)ncxItem.Attribute("href") ?? "";
            string path = TryResolve(resolve, opfPath, href);
            if (path != null)
            {
                string xml = ReadOrNull(readEntry, path);
                if (xml != null) return ReadNcx(xml, path, resolve);
            }
        }
        return new List<TocEntry>();
    }

    /// <summary>把目录项映射到正文偏移；目录缺失或不完整时按 spine 文件边界生成。</summary>
    internal static IReadOnlyList<Chapter> Build(IReadOnlyList<TocEntry> toc, IReadOnlyList<SpineText> spine, string body)
    {
        Dictionary<string, SpineText> byPath = new Dictionary<string, SpineText>(StringComparer.OrdinalIgnoreCase);
        foreach (SpineText item in spine) byPath.TryAdd(item.Path, item);

        List<Chapter> chapters = new List<Chapter>();
        HashSet<string> covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TocEntry entry in toc)
        {
            if (!byPath.TryGetValue(entry.Path, out SpineText item)) continue;
            covered.Add(entry.Path);
            int position = item.Start;
            // 一个 XHTML 里包含多章时，目录会带 #fragment。用目录标题在章节正文里找行首，
            // 找到就是精确偏移；找不到退回该文件开头，至少不会跳错文件。
            if (entry.Fragment.Length > 0 && item.Length > 0)
            {
                int found = FindHeading(body.AsSpan(item.Start, item.Length), entry.Title);
                if (found >= 0) position = item.Start + found;
            }
            string title = entry.Title.Length > 0 ? entry.Title : item.FirstLine;
            if (title.Length == 0) title = Path.GetFileNameWithoutExtension(entry.Path);
            chapters.Add(new Chapter(title, position));
        }

        // 目录只覆盖了部分 spine（或书里根本没有目录）时，用文件边界补齐其余章节。
        // 位置仍然来自书的结构，标题退化为每个文件的首行。
        foreach (SpineText item in spine)
        {
            if (covered.Contains(item.Path)) continue;
            string title = item.FirstLine.Length > 0 ? item.FirstLine : Path.GetFileNameWithoutExtension(item.Path);
            chapters.Add(new Chapter(title, item.Start));
        }

        // 按位置排序并去重。同一位置保留标题更长的一条（卷标题与章标题共用一个锚点时更有信息量）。
        List<Chapter> unique = new List<Chapter>();
        foreach (Chapter chapter in chapters.OrderBy(chapter => chapter.Position))
        {
            if (unique.Count == 0 || chapter.Position > unique[^1].Position) unique.Add(chapter);
            else if (chapter.Position == unique[^1].Position && chapter.Title.Length > unique[^1].Title.Length) unique[^1] = chapter;
        }
        // 目录从封面/版权页之后才开始，或第一个文件没有正文时，给一个回到卷首的入口。
        if (unique.Count > 0 && unique[0].Position > 0) unique.Insert(0, new Chapter("开头", 0));
        return unique;
    }

    private static string TryResolve(Func<string, string> resolve, string basePath, string href)
    {
        if (String.IsNullOrWhiteSpace(href)) return null;
        try { return resolve(basePath, href); }
        catch (Exception ex) when (ex is InvalidDataException or UriFormatException or ArgumentException) { return null; }
    }

    private static string ReadOrNull(Func<string, string> readEntry, string path)
    {
        try { return readEntry(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException) { return null; }
    }

    private static List<TocEntry> ReadNav(string xml, string basePath, Func<string, string> resolve)
    {
        XDocument document;
        try { document = ReadTolerantXml(xml); }
        catch (XmlException) { return new List<TocEntry>(); }
        // 优先取 epub:type="toc" 的 nav；不依赖前缀名，按属性本地名判断。
        XElement nav = document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "nav" &&
                element.Attributes().Any(attribute => attribute.Name.LocalName == "type" &&
                    attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("toc")))
            ?? document.Descendants().FirstOrDefault(element => element.Name.LocalName == "nav");
        if (nav == null) return new List<TocEntry>();

        List<TocEntry> entries = new List<TocEntry>();
        foreach (XElement anchor in nav.Descendants().Where(element => element.Name.LocalName == "a"))
        {
            string href = (string)anchor.Attribute("href") ?? "";
            if (href.Length > 0) AddEntry(entries, CleanTitle(anchor.Value), href, basePath, resolve);
        }
        return entries;
    }

    private static List<TocEntry> ReadNcx(string xml, string basePath, Func<string, string> resolve)
    {
        XDocument document;
        try { document = ReadTolerantXml(xml); }
        catch (XmlException) { return new List<TocEntry>(); }
        List<TocEntry> entries = new List<TocEntry>();
        foreach (XElement point in document.Descendants().Where(element => element.Name.LocalName == "navPoint"))
        {
            // 只取 navPoint 的直接子节点，避免把嵌套子章节的标题当成当前标题。
            XElement label = point.Elements().FirstOrDefault(element => element.Name.LocalName == "navLabel");
            string title = CleanTitle((string)label?.Elements().FirstOrDefault(element => element.Name.LocalName == "text") ?? "");
            string src = (string)point.Elements().FirstOrDefault(element => element.Name.LocalName == "content")?.Attribute("src") ?? "";
            if (src.Length > 0) AddEntry(entries, title, src, basePath, resolve);
        }
        return entries;
    }

    private static void AddEntry(List<TocEntry> entries, string title, string href, string basePath, Func<string, string> resolve)
    {
        int hash = href.IndexOf('#');
        string file = hash < 0 ? href : href[..hash];
        string fragment = hash < 0 ? "" : Uri.UnescapeDataString(href[(hash + 1)..]);
        if (file.Length == 0) return;
        string path = TryResolve(resolve, basePath, file);
        if (path != null) entries.Add(new TocEntry(title, path, fragment));
    }

    private static string CleanTitle(string value)
    {
        string clean = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return clean.Length > 100 ? clean[..100] : clean;
    }

    /// <summary>在章节正文里找以标题开头的行；返回相对章节起点的字符偏移。</summary>
    private static int FindHeading(ReadOnlySpan<char> chapter, string title)
    {
        if (title.Length == 0) return -1;
        int position = 0;
        while (position < chapter.Length)
        {
            int end = chapter[position..].IndexOf('\n');
            if (end < 0) end = chapter.Length;
            else end += position;
            ReadOnlySpan<char> line = chapter[position..end].Trim();
            if (line.Length >= title.Length && line[..title.Length].SequenceEqual(title.AsSpan())) return position;
            position = end + 1;
        }
        return -1;
    }

    private static XDocument ReadTolerantXml(string text)
    {
        XmlReaderSettings settings = new XmlReaderSettings
        {
            // nav.xhtml 常带 HTML DOCTYPE；忽略 DTD，也不解析外部实体。
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024,
        };
        using (StringReader reader = new StringReader(text))
        using (XmlReader xml = XmlReader.Create(reader, settings)) return XDocument.Load(xml);
    }
}
