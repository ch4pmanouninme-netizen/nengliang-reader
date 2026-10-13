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
    public static class Importer
    {
        static Importer() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        private const int TextLimit = 24 * 1024 * 1024;
        public static ImportedBook Read(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".epub") return ReadEpub(path);
            if (extension != ".txt") throw new InvalidDataException("目前支持 .txt 和 .epub 文件。");
            if (new FileInfo(path).Length > TextLimit) throw new InvalidDataException("TXT 文件超过 24 MB，请先拆分文件。");
            byte[] bytes = File.ReadAllBytes(path);
            string text;
            if (bytes.Length >= 2 && ((bytes[0] == 255 && bytes[1] == 254) || (bytes[0] == 254 && bytes[1] == 255)))
                text = (bytes[0] == 255 ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes, 2, bytes.Length - 2);
            else
            {
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { text = Encoding.GetEncoding(54936).GetString(bytes); }
            }
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("文件包含二进制数据，无法按 TXT 阅读。");
            return new ImportedBook { Title = Path.GetFileNameWithoutExtension(path), Text = Normalize(text), Format = "TXT" };
        }

        public static string Normalize(string text)
        {
            text = text.TrimStart('\ufeff').Replace("\r\n", "\n").Replace('\r', '\n');
            return String.Join("\n", text.Split('\n').Select(line => Regex.Replace(line, "[\\t \\u00a0\\u3000]+", " ").Trim()).Where(line => line.Length > 0));
        }

        public static XDocument ReadXml(string text)
        {
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = TextLimit };
            using (StringReader sr = new StringReader(text))
            using (XmlReader reader = XmlReader.Create(sr, settings)) return XDocument.Load(reader);
        }

        private static string EntryText(ZipArchive archive, string path, ref long total)
        {
            ZipArchiveEntry entry = archive.GetEntry(path);
            if (entry == null) throw new InvalidDataException("EPUB 内缺少文件：" + path);
            if (entry.Length > TextLimit || (total += entry.Length) > TextLimit) throw new InvalidDataException("EPUB 正文展开后超过 24 MB。");
            using (Stream stream = entry.Open())
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] bytes = new byte[8192]; int count;
                while ((count = stream.Read(bytes, 0, bytes.Length)) > 0)
                {
                    if (buffer.Length + count > TextLimit) throw new InvalidDataException("EPUB 单个文件过大。");
                    buffer.Write(bytes, 0, count);
                }
                byte[] data = buffer.ToArray();
                using (StreamReader reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, true)) return reader.ReadToEnd();
            }
        }

        public static string ResolveEntry(string basePath, string href)
        {
            Uri absolute;
            if (String.IsNullOrWhiteSpace(href) || Uri.TryCreate(href, UriKind.Absolute, out absolute) || href.StartsWith("//")) throw new InvalidDataException("EPUB 包含不支持的外部章节地址。");
            Uri resolved = new Uri(new Uri("https://epub.invalid/" + basePath), href);
            if (resolved.Host != "epub.invalid") throw new InvalidDataException("无效的章节路径。");
            return Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
        }

        private static ImportedBook ReadEpub(string path)
        {
            if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new InvalidDataException("EPUB 文件超过 100 MB。");
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
                Dictionary<string, string> manifest = new Dictionary<string, string>();
                foreach (XElement item in opf.Descendants().Where(e => e.Name.LocalName == "item"))
                {
                    string id = (string)item.Attribute("id"), href = (string)item.Attribute("href");
                    string type = (string)item.Attribute("media-type") ?? "";
                    if (id != null && href != null && (type.Contains("html") || type == "text/plain")) manifest[id] = ResolveEntry(opfPath, href);
                }
                StringBuilder result = new StringBuilder();
                foreach (XElement itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
                {
                    if ((string)itemref.Attribute("linear") == "no") continue;
                    string idref = (string)itemref.Attribute("idref") ?? "", chapter;
                    if (!manifest.TryGetValue(idref, out chapter)) continue;
                    string html = EntryText(zip, chapter, ref total);
                    // Extract text only. No scripts, images, external resources or embedded web view.
                    html = Regex.Replace(html, @"<(script|style|head|svg)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(3));
                    html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline, TimeSpan.FromSeconds(3));
                    html = Regex.Replace(html, @"<(br|hr)\b[^>]*>|</(p|div|h[1-6]|li|section|article|blockquote|tr)\s*>", "\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(3));
                    html = Regex.Replace(html, @"<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(3));
                    result.AppendLine(Normalize(WebUtility.HtmlDecode(html)));
                    if (result.Length > TextLimit) throw new InvalidDataException("EPUB 正文过大。");
                }
                string text = Normalize(result.ToString());
                if (text.Length == 0) throw new InvalidDataException("EPUB 没有可读取的正文，可能是加密书籍或扫描图片版。");
                return new ImportedBook { Title = title == null ? Path.GetFileNameWithoutExtension(path) : title.Value.Trim(), Text = text, Format = "EPUB" };
            }
        }
    }


