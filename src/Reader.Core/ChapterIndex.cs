using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Reader.Core;

/// <summary>小说中的一个章节标题及其在正文中的起始位置。</summary>
/// <param name="Title">章节标题，已去除首尾空白。</param>
/// <param name="Position">标题行首字符在正文中的 UTF-16 下标。</param>
public sealed record Chapter(string Title, int Position);

/// <summary>
/// 从纯文本正文中识别章节标题。TXT 和 EPUB 导入后都只保留纯文本，章节边界已经丢失，
/// 因此这里按常见标题格式做启发式识别。识别结果只用于“选择章节”跳转，不改变书库数据。
/// </summary>
public static class ChapterIndex
{
    // 行首允许的标题格式：
    //   第X章 / 第X节 / 第X回 / 第X卷 / 第X部 / 第X集 / 第X篇（数字支持中文、阿拉伯、全角）
    //   卷一 / 卷1 这类不带“第”的卷目标题
    //   序章、楔子、引子、前言、尾声、后记、番外、附录
    //   Chapter 1 / Part IV
    // “回”后拒绝“合”（第一回合），“节”后拒绝“课”（第一节课），压掉最常见的整句误判。
    private static readonly Regex Heading = new(
        @"^(?:正文\s*)?(?:" +
        @"第\s*[0-9０-９零〇一二三四五六七八九十百千万两廿卅]+\s*(?:章|节(?!课)|回(?!合)|卷|部|集|篇)" +
        @"|[卷部集篇]\s*[0-9０-９零〇一二三四五六七八九十百千万两廿卅]+" +
        @"|(?:作品相关|完本感言|序章|序言|楔子|引子|前言|尾声|后记|番外|附录)(?=\s|$|[:：、.．])" +
        @"|(?:chapter|part)\s+[0-9ivxlcdm]+(?=\s|$|[:：.])" +
        @")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 扫描整本书，按出现顺序返回章节标题。第一个标题之前如果有内容，
    /// 会补一条“开头”（位置 0），方便从任意位置跳回卷首。
    /// </summary>
    /// <param name="text">整本书的纯文本，通常是 <see cref="Importer.Normalize"/> 的结果。</param>
    /// <returns>按正文位置升序排列的章节；识别不到标题时返回空列表。</returns>
    public static IReadOnlyList<Chapter> Find(string text)
    {
        if (String.IsNullOrEmpty(text)) return Array.Empty<Chapter>();
        List<Chapter> chapters = new List<Chapter>();
        int position = 0;
        while (position < text.Length)
        {
            int end = text.IndexOf('\n', position);
            if (end < 0) end = text.Length;
            // 只为标题行创建字符串；正文行用 Span 判断后就丢掉，避免给上百万字正文分配临时字符串。
            int start = position;
            while (start < end && Char.IsWhiteSpace(text[start])) start++;
            ReadOnlySpan<char> line = text.AsSpan(start, end - start).TrimEnd();
            // 章节标题通常很短；过长的行几乎都是正文，先按长度挡掉，避免逐行跑正则。
            if (line.Length is > 0 and <= 60 && IsHeading(line))
            {
                string title = line.ToString();
                // 有些 EPUB 会同时保留 <title> 与 <h1>/<h2>，产生相邻重复行；只保留第一条。
                if (chapters.Count == 0
                    || !String.Equals(chapters[^1].Title, title, StringComparison.Ordinal)
                    || start - chapters[^1].Position > 200)
                    chapters.Add(new Chapter(title, start));
            }
            position = end + 1;
        }
        if (chapters.Count > 0 && chapters[0].Position > 0) chapters.Insert(0, new Chapter("开头", 0));
        return chapters;
    }

    private static bool IsHeading(ReadOnlySpan<char> line)
    {
        // 标题几乎不会以逗号、分号、顿号收尾，直接排除。
        char last = line[^1];
        if (last is '，' or '；' or '、') return false;
        var matches = Heading.EnumerateMatches(line);
        if (!matches.MoveNext()) return false;
        if (last != '。') return true;
        // 以句号结尾时放宽一格：只有“第X章 ”后面带分隔符的才当标题。
        // 这样能收下“第3章 请假一天，顺便小聊几句。”，又不会把“第一章的内容很精彩。”当标题。
        int after = matches.Current.Index + matches.Current.Length;
        return after >= line.Length
            || Char.IsWhiteSpace(line[after])
            || line[after] is ':' or '：' or '、' or '.' or '．' or '-' or '—' or '（' or '(';
    }
}
