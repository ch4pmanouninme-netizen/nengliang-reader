using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace OneLineReader
{
    internal static class ReaderTests
    {
        private static int assertions;
        private static string root;
        private static void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("FAIL: " + label);
            assertions++; Console.WriteLine("PASS: " + label);
        }
        private static void Entry(ZipArchive zip, string name, string text)
        {
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(text);
        }
        [STAThread]
        public static int Main(string[] args)
        {
            root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("work", "reader-tests"));
            Directory.CreateDirectory(root);
            try
            {
                string input = "  你好，世界。\r\n\r\n第二段，保留 & 符号和 emoji 😊。  \n第三段。";
                string expected = "你好，世界。\n第二段，保留 & 符号和 emoji 😊。\n第三段。";
                Check(Importer.Normalize(input) == expected, "normalize paragraphs without losing content");
                string utf8 = Path.Combine(root, "utf8.txt"); File.WriteAllText(utf8, input, new UTF8Encoding(true));
                Check(Importer.Read(utf8).Text == expected, "UTF-8 BOM import");
                string gb = Path.Combine(root, "gb18030.txt"); File.WriteAllText(gb, input, Encoding.GetEncoding(54936));
                Check(Importer.Read(gb).Text == expected, "GB18030 Chinese and emoji import");
                string unicode = Path.Combine(root, "utf16.txt"); File.WriteAllText(unicode, input, Encoding.Unicode);
                Check(Importer.Read(unicode).Text == expected, "UTF-16 import");
                string epub = Path.Combine(root, "示例.epub");
                using (ZipArchive zip = ZipFile.Open(epub, ZipArchiveMode.Create))
                {
                    Entry(zip, "META-INF/container.xml", "<container><rootfiles><rootfile full-path='OPS/book.opf'/></rootfiles></container>");
                    Entry(zip, "OPS/book.opf", "<package xmlns:dc='http://purl.org/dc/elements/1.1/'><metadata><dc:title>中文 EPUB 测试</dc:title></metadata><manifest><item id='second' href='text/second.xhtml' media-type='application/xhtml+xml'/><item id='first' href='text/%E7%AC%AC%E4%B8%80%E7%AB%A0.xhtml#top' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='first'/><itemref idref='second'/></spine></package>");
                    Entry(zip, "OPS/text/第一章.xhtml", "<html><head><title>忽略标题</title><style>忽略样式</style></head><body><h1>第一章</h1><p>书中有 &amp; 和 &nbsp; 空格。</p><script>忽略脚本</script><p>下一段<br/>另一行</p></body></html>");
                    Entry(zip, "OPS/text/second.xhtml", "<html><body><h1>第二章</h1><p>结束。</p></body></html>");
                }
                ImportedBook imported = Importer.Read(epub);
                Check(imported.Title == "中文 EPUB 测试", "EPUB metadata title");
                Check(imported.Text == "第一章\n书中有 & 和 空格。\n下一段\n另一行\n第二章\n结束。", "EPUB spine order, escaped path, blocks and entities");
                bool blocked = false;
                try { Importer.ReadXml("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/nope'>]><x>&e;</x>"); } catch (System.Xml.XmlException) { blocked = true; }
                Check(blocked, "XML external entities disabled");
                blocked = false; try { Importer.ResolveEntry("OPS/book.opf", "https://example.com/chapter"); } catch (InvalidDataException) { blocked = true; }
                Check(blocked, "EPUB external chapter reference rejected");
                Book book = new Book { Title = "测试" }; ReadingSession session = new ReadingSession(book, expected);
                StringBuilder reconstructed = new StringBuilder(); int iterations = 0;
                do
                {
                    int end = session.End(s => s.Length <= 5);
                    string page = session.Text.Substring(session.Position, end - session.Position);
                    Check(!Char.IsLowSurrogate(page[0]) && !Char.IsHighSurrogate(page[page.Length - 1]), "page keeps surrogate pairs " + iterations);
                    reconstructed.Append(page);
                    if (!session.Next(end)) break;
                    if (++iterations > 100) throw new Exception("Pagination loop");
                } while (true);
                Check(reconstructed.ToString() == expected.Replace("\n", ""), "all paginated content reproduced exactly");
                Check(session.Percent == 100 && book.Completed, "completion reaches 100 percent");
                session.Seek(0); int firstEnd = session.End(s => s.Length <= 3); session.Next(firstEnd); session.Previous(s => s.Length <= 3);
                Check(session.Position == 0, "previous restores exact page position");
                session.Seek(expected.IndexOf("😊", StringComparison.Ordinal) + 1);
                Check(!Char.IsLowSurrogate(session.Text[session.Position]), "jump never starts on low surrogate");
                session.Previous(s => s.Length <= 5); Check(session.Position >= 0, "previous works after direct seek");
                ReadingSession multi = new ReadingSession(new Book(), "第一章\n甲乙丙丁戊己庚辛😊壬癸\n短段\n末段没有遗漏。");
                ReadingPage firstPage = multi.Layout(s => s.Length <= 5, 3);
                Check(firstPage.Lines.Count == 3 && firstPage.Lines[0] == "第一章", "multiline page wraps across paragraphs and retains chapter heading");
                multi.Next(firstPage.End); multi.Previous(s => s.Length <= 5, 3);
                Check(multi.Position == 0, "multiline previous restores exact previous page");
                StringBuilder multiText = new StringBuilder(); bool pairsOk = true; int pages = 0;
                do
                {
                    ReadingPage current = multi.Layout(s => s.Length <= 5, 3);
                    foreach (string line in current.Lines) { multiText.Append(line); pairsOk &= !Char.IsLowSurrogate(line[0]) && !Char.IsHighSurrogate(line[line.Length - 1]); }
                    if (!multi.Next(current.End)) break;
                    if (++pages > 100) throw new Exception("Multiline pagination loop");
                } while (true);
                Check(multiText.ToString() == multi.Text.Replace("\n", "") && pairsOk && multi.Book.Completed, "multiline pagination preserves every character, emoji and final partial page");
                multi.Seek(multi.Text.IndexOf("末段", StringComparison.Ordinal)); multi.Previous(s => s.Length <= 5, 3);
                Check(multi.Position < multi.Text.IndexOf("末段", StringComparison.Ordinal) && !Char.IsLowSurrogate(multi.Text[multi.Position]), "multiline previous works after restart or jump without page history");
                string data = Path.Combine(root, "data"); Store store = new Store(data);
                Book added = store.Add("测试书籍", expected, "TXT");
                Check(Object.ReferenceEquals(added, store.Add("重复内容", expected, "TXT")) && store.Books.Count == 1, "duplicate content deduplicates");
                added.Position = 4; added.Bookmark = 9; store.LastId = added.Id; store.Pref.Theme = 2; store.Pref.ClickThrough = true; store.Pref.UiDark = true; store.Pref.LineCount = 3; store.Save();
                added.Position = 6; store.Save();
                Store restored = new Store(data);
                Check(restored.Books[0].Position == 6 && restored.Books[0].Bookmark == 9 && restored.Pref.Theme == 2 && restored.TextFor(restored.Books[0]) == expected, "books, progress, bookmark and settings persist");
                Check(restored.Pref.Stealth && restored.Pref.ClickThrough, "stealth and click-through preferences persist");
                Check(restored.Pref.UiDark, "main window appearance preference persists");
                Check(restored.Pref.LineCount == 3, "display line count persists across restart");
                string legacyRoot = Path.Combine(root, "legacy-preferences"); Directory.CreateDirectory(legacyRoot);
                System.Xml.Linq.XDocument legacyState = System.Xml.Linq.XDocument.Load(Path.Combine(data, "state.xml"));
                legacyState.Root.Element("preferences").Attribute("lineCount").Remove(); legacyState.Save(Path.Combine(legacyRoot, "state.xml"));
                Check(new Store(legacyRoot).Pref.LineCount == 1, "old preferences without line count keep one-line display");
                string migratedData = Path.Combine(root, "migrated-data");
                Program.MigrateLegacyData(data, migratedData);
                Store migrated = new Store(migratedData);
                Check(migrated.Books[0].Position == 6 && migrated.TextFor(migrated.Books[0]) == expected && File.Exists(Path.Combine(data, "state.xml")), "legacy migration copies book and progress while retaining original");
                migrated.Books[0].Position = 12; migrated.Save(); Program.MigrateLegacyData(data, migratedData);
                Check(new Store(migratedData).Books[0].Position == 12, "legacy migration does not overwrite existing new progress");
                File.WriteAllText(Path.Combine(data, "state.xml"), "broken");
                Store recovered = new Store(data);
                Check(recovered.Books[0].Position == 4 && recovered.RecoveryNotice.Length > 0, "damaged state restores atomic backup");
                using (Font font = Ui.Font(14))
                {
                    ReadingSession measured = new ReadingSession(new Book(), String.Concat(Enumerable.Repeat("中文English 😊 & 全文分页测试。", 150)));
                    StringBuilder result = new StringBuilder();
                    do
                    {
                        int end = measured.End(s => TextRenderer.MeasureText(s, font, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= 242);
                        result.Append(measured.Text.Substring(measured.Position, end - measured.Position));
                        if (!measured.Next(end)) break;
                    } while (true);
                    Check(result.ToString() == measured.Text, "real font measurement preserves mixed language content");
                }
                Console.WriteLine("SUCCESS: " + assertions + " assertions passed."); return 0;
            }
            catch (Exception ex) { Console.WriteLine(ex.ToString()); return 1; }
            
        }
    }
}
