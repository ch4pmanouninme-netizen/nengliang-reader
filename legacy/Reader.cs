using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

[assembly: System.Reflection.AssemblyTitle("良子阅读器")]
[assembly: System.Reflection.AssemblyProduct("良子阅读器")]
[assembly: System.Reflection.AssemblyVersion("1.3.1.0")]

namespace OneLineReader
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NengliangReader");
            int dataArg = Array.IndexOf(args, "--data");
            if (dataArg >= 0 && dataArg + 1 < args.Length) data = Path.GetFullPath(args[dataArg + 1]);
            try
            {
                using (FileStream instance = new FileStream(Path.Combine(EnsureDirectory(data), "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    if (dataArg < 0) MigrateLegacyData(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"), data);
                    Store store = new Store(data);
                    Application.Run(new LibraryForm(store));
                }
                return 0;
            }
            catch (IOException ex)
            {
                MessageBox.Show("无法打开阅读器的数据目录。请确认没有重复运行，且本机应用数据目录可以写入。\n\n" + ex.Message, "良子阅读器 · 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message, "良子阅读器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }

        private static string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }

        internal static void MigrateLegacyData(string legacy, string destination)
        {
            string state = Path.Combine(legacy, "state.xml");
            if (!File.Exists(state) || File.Exists(Path.Combine(destination, "state.xml"))) return;
            Directory.CreateDirectory(Path.Combine(destination, "books"));
            string oldBooks = Path.Combine(legacy, "books");
            if (Directory.Exists(oldBooks))
                foreach (string file in Directory.GetFiles(oldBooks, "*.txt"))
                    if (Regex.IsMatch(Path.GetFileName(file), "^[a-f0-9]{64}\\.txt$")) File.Copy(file, Path.Combine(destination, "books", Path.GetFileName(file)), true);
            if (File.Exists(state + ".bak")) File.Copy(state + ".bak", Path.Combine(destination, "state.xml.bak"), true);
            File.Copy(state, Path.Combine(destination, "state.xml"), false);
        }
    }

    internal sealed class Book
    {
        public string Id, Title, Format;
        public int Position, Bookmark = -1, Length;
        public bool Completed;
        public override string ToString() { return Title; }
    }

    internal sealed class Preferences
    {
        public int Width = 560, X = -1, Y = -1, Seconds = 6, Opacity = 100, Theme = 4, LineCount = 1;
        public float FontSize = 11;
        public string FontName = "Microsoft YaHei UI";
        public bool TopMost = true, Stealth = true, ClickThrough, UiDark;
        public void UsePreset(int theme)
        {
            Theme = theme; FontSize = theme == 5 ? 10 : 11; Opacity = 100; Stealth = true;
            if (theme == 4 || theme == 5) UiDark = theme == 5;
        }
    }

    internal sealed class Store
    {
        public readonly string Root;
        public readonly List<Book> Books = new List<Book>();
        public readonly Preferences Pref = new Preferences();
        public string LastId = "";
        public string RecoveryNotice = "";

        public Store(string root)
        {
            Root = root;
            Directory.CreateDirectory(Path.Combine(root, "books"));
            string state = Path.Combine(root, "state.xml");
            if (!File.Exists(state)) return;
            try { Load(state); }
            catch (Exception ex)
            {
                if (!(ex is XmlException || ex is IOException || ex is FormatException || ex is OverflowException)) throw;
                string backup = state + ".bak";
                if (File.Exists(backup))
                {
                    try { Load(backup); RecoveryNotice = "上次数据文件未完整写入，已恢复备份。"; return; }
                    catch (Exception backupError)
                    {
                        if (!(backupError is XmlException || backupError is IOException || backupError is FormatException || backupError is OverflowException)) throw;
                    }
                }
                // Keep damaged state files for recovery; restore imported book texts independently.
                Books.Clear();
                foreach (string file in Directory.GetFiles(Path.Combine(root, "books"), "*.txt"))
                {
                    string content = File.ReadAllText(file, Encoding.UTF8);
                    Books.Add(new Book { Id = Path.GetFileNameWithoutExtension(file), Title = "恢复的书籍 " + (Books.Count + 1), Format = "TXT", Length = content.Length });
                }
                RecoveryNotice = "进度文件损坏，已保留原文件并恢复本地书籍；阅读位置需要重新选择。";
                File.Copy(state, Path.Combine(root, "state.damaged-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".xml"), true);
            }
        }

        private void Load(string path)
        {
            XElement root = Importer.ReadXml(File.ReadAllText(path, Encoding.UTF8)).Root;
            if (root == null || root.Name != "reader") throw new XmlException("无效的进度文件。");
            List<Book> loaded = new List<Book>();
            foreach (XElement b in root.Elements("book"))
            {
                string id = (string)b.Attribute("id") ?? "";
                if (!Regex.IsMatch(id, "^[a-f0-9]{64}$")) continue;
                if (!File.Exists(Path.Combine(Root, "books", id + ".txt"))) continue;
                loaded.Add(new Book { Id = id, Title = (string)b.Attribute("title") ?? "未命名", Format = (string)b.Attribute("format") ?? "TXT", Position = Number(b, "position", 0), Bookmark = Number(b, "bookmark", -1), Length = Number(b, "length", 0), Completed = (string)b.Attribute("completed") == "true" });
            }
            XElement p = root.Element("preferences");
            if (p != null)
            {
                Pref.Width = Clamp(Number(p, "width", 560), 240, 1800);
                Pref.X = Number(p, "x", -1); Pref.Y = Number(p, "y", -1);
                Pref.Seconds = Clamp(Number(p, "seconds", 6), 2, 60);
                Pref.LineCount = Clamp(Number(p, "lineCount", 1), 1, 10);
                Pref.Opacity = Clamp(Number(p, "opacity", 100), 30, 100);
                Pref.Theme = Clamp(Number(p, "theme", 4), 0, 6);
                Pref.FontSize = Clamp(Number(p, "fontSize", 11), 9, 32);
                Pref.FontName = (string)p.Attribute("fontName") ?? "Microsoft YaHei UI";
                Pref.TopMost = (string)p.Attribute("topMost") != "false";
                Pref.Stealth = (string)p.Attribute("stealth") != "false";
                Pref.ClickThrough = (string)p.Attribute("clickThrough") == "true";
                Pref.UiDark = (string)p.Attribute("uiDark") == null ? Pref.Theme == 1 || Pref.Theme == 5 : (string)p.Attribute("uiDark") == "true";
            }
            Books.Clear(); Books.AddRange(loaded);
            LastId = (string)root.Attribute("last") ?? "";
        }

        private static int Number(XElement e, string name, int fallback)
        {
            int value;
            return int.TryParse((string)e.Attribute(name), out value) ? value : fallback;
        }
        public static int Clamp(int n, int min, int max) { return Math.Max(min, Math.Min(max, n)); }
        public string TextFor(Book b) { return File.ReadAllText(Path.Combine(Root, "books", b.Id + ".txt"), Encoding.UTF8); }

        public Book Add(string title, string text, string format)
        {
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidDataException("文件中没有可阅读的正文。");
            string id;
            using (SHA256 hash = SHA256.Create()) id = String.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(x => x.ToString("x2")));
            Book existing = Books.FirstOrDefault(b => b.Id == id);
            if (existing != null) return existing;
            string path = Path.Combine(Root, "books", id + ".txt");
            if (!File.Exists(path)) File.WriteAllText(path, text, new UTF8Encoding(false));
            Book book = new Book { Id = id, Title = title, Format = format, Length = text.Length };
            Books.Insert(0, book);
            return book;
        }

        public void Save()
        {
            XElement root = new XElement("reader", new XAttribute("version", "1"), new XAttribute("last", LastId),
                new XElement("preferences", new XAttribute("width", Pref.Width), new XAttribute("x", Pref.X), new XAttribute("y", Pref.Y),
                    new XAttribute("seconds", Pref.Seconds), new XAttribute("lineCount", Pref.LineCount), new XAttribute("opacity", Pref.Opacity), new XAttribute("theme", Pref.Theme),
                    new XAttribute("fontSize", (int)Pref.FontSize), new XAttribute("fontName", Pref.FontName), new XAttribute("topMost", Pref.TopMost ? "true" : "false"),
                    new XAttribute("stealth", Pref.Stealth ? "true" : "false"), new XAttribute("clickThrough", Pref.ClickThrough ? "true" : "false"), new XAttribute("uiDark", Pref.UiDark ? "true" : "false")));
            foreach (Book b in Books) root.Add(new XElement("book", new XAttribute("id", b.Id), new XAttribute("title", b.Title), new XAttribute("format", b.Format), new XAttribute("position", b.Position), new XAttribute("bookmark", b.Bookmark), new XAttribute("length", b.Length), new XAttribute("completed", b.Completed ? "true" : "false")));
            string target = Path.Combine(Root, "state.xml"), temp = target + ".tmp";
            File.WriteAllText(temp, root.ToString(), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temp, target, target + ".bak", true);
            else File.Move(temp, target);
        }
    }

    internal sealed class ImportedBook
    {
        public string Title, Text, Format;
    }

    internal static class Importer
    {
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

    internal sealed class ReadingPage
    {
        public readonly List<string> Lines = new List<string>();
        public int End;
    }

    internal sealed class ReadingSession
    {
        public readonly Book Book;
        public readonly string Text;
        public int Position { get { return Book.Position; } }
        private readonly Stack<int> history = new Stack<int>();
        public ReadingSession(Book book, string text)
        {
            if (String.IsNullOrEmpty(text)) throw new InvalidDataException("书籍正文为空，请重新导入。");
            Book = book; Text = text; Book.Length = text.Length; bool completed = book.Completed; Seek(book.Position); book.Completed = completed;
        }
        public void Seek(int position)
        {
            Book.Position = Store.Clamp(position, 0, Math.Max(0, Text.Length - 1));
            if (Book.Position > 0 && Char.IsLowSurrogate(Text[Book.Position])) Book.Position--;
            while (Book.Position < Text.Length - 1 && Text[Book.Position] == '\n') Book.Position++;
            history.Clear(); Book.Completed = false;
        }
        public int End(Func<string, bool> fits)
        {
            return EndAt(Position, fits);
        }
        private int EndAt(int position, Func<string, bool> fits)
        {
            int paragraphEnd = Text.IndexOf('\n', position);
            if (paragraphEnd < 0) paragraphEnd = Text.Length;
            int low = 1, high = Math.Min(paragraphEnd - position, 2048), best = 1;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (fits(Text.Substring(position, mid))) { best = mid; low = mid + 1; }
                else high = mid - 1;
            }
            int end = Math.Min(paragraphEnd, position + best);
            if (end < Text.Length && end > position && Char.IsHighSurrogate(Text[end - 1]) && Char.IsLowSurrogate(Text[end])) end--;
            if (end == position) end = Math.Min(Text.Length, position + 2);
            return end;
        }
        public ReadingPage Layout(Func<string, bool> fits, int lineCount)
        {
            ReadingPage page = new ReadingPage();
            int position = Position;
            for (int row = 0; row < Store.Clamp(lineCount, 1, 10) && position < Text.Length; row++)
            {
                while (position < Text.Length && Text[position] == '\n') position++;
                if (position >= Text.Length) break;
                page.End = EndAt(position, fits);
                page.Lines.Add(Text.Substring(position, page.End - position));
                position = page.End;
            }
            return page;
        }
        public void ClearHistory() { history.Clear(); }
        public bool Next(int end)
        {
            while (end < Text.Length && Text[end] == '\n') end++;
            if (end >= Text.Length) { Book.Completed = true; return false; }
            if (end <= Position) return false;
            Book.Completed = false;
            history.Push(Position); Book.Position = end; return true;
        }
        public void Previous(Func<string, bool> fits)
        {
            Previous(fits, 1);
        }
        public void Previous(Func<string, bool> fits, int lineCount)
        {
            Book.Completed = false;
            if (history.Count > 0) { Book.Position = history.Pop(); return; }
            for (int row = 0; row < Store.Clamp(lineCount, 1, 10) && Position > 0; row++) PreviousLine(fits);
        }
        private void PreviousLine(Func<string, bool> fits)
        {
            if (Position == 0) return;
            int end = Position;
            while (end > 0 && Text[end - 1] == '\n') end--;
            int paragraphStart = Text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
            int low = Math.Max(paragraphStart, end - 2048), high = end - 1, best = high;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (fits(Text.Substring(mid, end - mid))) { best = mid; high = mid - 1; }
                else low = mid + 1;
            }
            Book.Position = best;
            if (Book.Position > 0 && Char.IsLowSurrogate(Text[Book.Position])) Book.Position--;
        }
        public int Percent { get { return Book.Completed ? 100 : (Text.Length <= 1 ? 0 : (int)(100.0 * Position / (Text.Length - 1))); } }
    }

    internal static class Ui
    {
        public static readonly Color Ink = Color.FromArgb(37, 47, 43), Muted = Color.FromArgb(117, 126, 119), Green = Color.FromArgb(41, 91, 70), Paper = Color.FromArgb(247, 248, 244), Line = Color.FromArgb(224, 229, 221);
        public static Font Font(float size, FontStyle style = FontStyle.Regular) { return new Font("Microsoft YaHei UI", size, style); }
        public static Label Label(string text, int x, int y, int w, int h, float size, Color color, FontStyle style = FontStyle.Regular)
        {
            return new Label { Text = text, Bounds = new Rectangle(x, y, w, h), Font = Font(size, style), ForeColor = color, BackColor = Color.Transparent, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        }
        public static Button Button(string text, int x, int y, int w, int h, bool primary)
        {
            Button b = new Button { Text = text, Bounds = new Rectangle(x, y, w, h), FlatStyle = FlatStyle.Flat, Font = Font(10), BackColor = primary ? Green : Color.White, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = primary ? Green : Line; b.FlatAppearance.BorderSize = primary ? 0 : 1;
            return b;
        }
        public static Color Surface(bool dark) { return dark ? Color.FromArgb(25, 26, 28) : Color.FromArgb(245, 247, 250); }
        public static Color TextColor(bool dark) { return dark ? Color.FromArgb(182, 186, 194) : Color.FromArgb(64, 69, 76); }
        public static Color MutedColor(bool dark) { return dark ? Color.FromArgb(127, 133, 141) : Color.FromArgb(138, 144, 153); }
        public static void Skin(Control root, bool dark)
        {
            root.ForeColor = TextColor(dark);
            root.BackColor = root.Name == "header" ? (dark ? Color.FromArgb(37, 40, 44) : Color.FromArgb(237, 240, 244))
                : root.Name == "side" ? (dark ? Color.FromArgb(32, 33, 36) : Color.FromArgb(235, 238, 242))
                : root.Name == "preview" ? (dark ? Surface(true) : Color.White) : root.Parent == null ? Surface(dark) : root.Parent.BackColor;
            Label label = root as Label;
            if (label != null) { root.BackColor = Color.Transparent; if (root.Font.Size <= 9) root.ForeColor = MutedColor(dark); }
            Button button = root as Button;
            if (button != null)
            {
                button.BackColor = dark ? Color.FromArgb(43, 45, 48) : Color.FromArgb(248, 249, 251);
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = dark ? Color.FromArgb(54, 57, 61) : Color.FromArgb(217, 222, 228);
                button.FlatAppearance.MouseOverBackColor = dark ? Color.FromArgb(54, 57, 61) : Color.FromArgb(231, 235, 241);
            }
            ComboBox combo = root as ComboBox;
            if (combo != null) { combo.FlatStyle = FlatStyle.Flat; combo.BackColor = dark ? Color.FromArgb(43, 45, 48) : Color.FromArgb(248, 249, 251); }
            foreach (Control child in root.Controls) Skin(child, dark);
        }
    }

    internal sealed class QuietTrackBar : Control
    {
        private int value;
        public int Minimum { get; set; }
        public int Maximum { get; set; }
        public int Value { get { return value; } set { this.value = Store.Clamp(value, Minimum, Maximum); Invalidate(); } }
        public QuietTrackBar() { Maximum = 1000; TabStop = true; Cursor = Cursors.Hand; SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); bool dark = BackColor.GetBrightness() < 0.4f;
            int x = 3 + (Maximum <= Minimum ? 0 : (int)((long)(Width - 6) * (Value - Minimum) / (Maximum - Minimum)));
            using (Pen track = new Pen(dark ? Color.FromArgb(59, 62, 67) : Color.FromArgb(217, 222, 228), 2)) e.Graphics.DrawLine(track, 3, Height / 2, Width - 3, Height / 2);
            using (Brush thumb = new SolidBrush(dark ? Color.FromArgb(127, 133, 141) : Color.FromArgb(138, 144, 153))) e.Graphics.FillEllipse(thumb, x - 3, Height / 2 - 3, 6, 6);
        }
        private void SetFromX(int x) { Value = Minimum + (int)((long)Store.Clamp(x - 3, 0, Math.Max(1, Width - 6)) * (Maximum - Minimum) / Math.Max(1, Width - 6)); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); Capture = true; SetFromX(e.X); } base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (Capture && e.Button == MouseButtons.Left) SetFromX(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { SetFromX(e.X); Capture = false; } base.OnMouseUp(e); }
        protected override bool IsInputKey(Keys keyData) { Keys key = keyData & Keys.KeyCode; return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value -= 10;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value += 10;
            else if (e.KeyCode == Keys.PageDown) Value += 100;
            else if (e.KeyCode == Keys.PageUp) Value -= 100;
            else if (e.KeyCode == Keys.Home) Value = Minimum;
            else if (e.KeyCode == Keys.End) Value = Maximum;
            base.OnKeyDown(e);
        }
    }

    internal sealed class LibraryForm : Form
    {
        private readonly Store store;
        private readonly ListBox books = new ListBox();
        private readonly Label title, metadata, progress, status;
        private readonly Label preview, hotkeyInfo;
        private readonly Button start;
        private readonly Button appearance;
        private readonly QuietTrackBar jump;
        private readonly NotifyIcon tray;
        private readonly Timer saveTimer = new Timer();
        private ReaderForm reader;
        private ReadingSession session;
        private bool dirty, busy, allowExit, saveWarning;
        private string hideShortcut = "Ctrl + Alt + H", mouseShortcut = "Ctrl + Alt + M";
        private string HotkeyCaption { get { return (hideShortcut.Length > 0 ? hideShortcut + " 隐藏 / 恢复" : "Esc 隐藏 / 托盘恢复") + "   ·   " + (mouseShortcut.Length > 0 ? mouseShortcut + " 穿透开关" : "托盘切换穿透"); } }
        private readonly List<int> registeredKeys = new List<int>();

        public LibraryForm(Store store)
        {
            this.store = store;
            Text = "文档"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; ClientSize = new Size(620, 360); MinimumSize = Size; MaximumSize = Size;
            StartPosition = FormStartPosition.CenterScreen; Font = Ui.Font(9); AutoScaleMode = AutoScaleMode.None; AllowDrop = true; Icon = MakeIcon(); DoubleBuffered = true;
            Panel header = new Panel { Name = "header", Bounds = new Rectangle(1, 1, 618, 34) }; Controls.Add(header);
            Label caption = Ui.Label("文档", 12, 5, 140, 24, 9, Ui.Ink); header.Controls.Add(caption);
            MouseEventHandler drag = (s, e) => { if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
            header.MouseDown += drag; caption.MouseDown += drag;
            appearance = Ui.Button("企业微信 · 浅色 ⇄", 402, 4, 140, 25, false); appearance.Font = Ui.Font(9); header.Controls.Add(appearance);
            appearance.Click += (s, e) => UsePreset(store.Pref.UiDark ? 4 : 5);
            Button hide = Ui.Button("—", 552, 4, 26, 25, false); hide.Click += (s, e) => Hide(); header.Controls.Add(hide);
            Button close = Ui.Button("×", 584, 4, 26, 25, false); close.Click += (s, e) => ExitApp(); header.Controls.Add(close);
            Panel side = new Panel { Name = "side", Bounds = new Rectangle(12, 48, 158, 232) };
            Controls.Add(side);
            side.Controls.Add(Ui.Label("最近文档", 10, 5, 138, 23, 9, Ui.Muted));
            books.Bounds = new Rectangle(0, 32, 158, 200); books.BorderStyle = BorderStyle.None;
            books.DrawMode = DrawMode.OwnerDrawFixed; books.ItemHeight = 44; books.Font = Ui.Font(9);
            books.DrawItem += DrawBook; books.SelectedIndexChanged += (s, e) => SelectBook(); side.Controls.Add(books);
            Panel card = new Panel { Name = "preview", Bounds = new Rectangle(182, 48, 426, 232) };
            Controls.Add(card);
            title = Ui.Label("未打开文档", 12, 8, 402, 24, 10, Ui.Ink); card.Controls.Add(title);
            metadata = Ui.Label("TXT / EPUB", 12, 37, 402, 20, 8, Ui.Muted); card.Controls.Add(metadata);
            preview = Ui.Label("", 12, 70, 402, 95, 10, Ui.Ink); preview.TextAlign = ContentAlignment.TopLeft;
            card.Controls.Add(preview);
            progress = Ui.Label("0%", 12, 185, 110, 24, 8, Ui.Muted); card.Controls.Add(progress);
            jump = new QuietTrackBar { Bounds = new Rectangle(122, 182, 292, 30), Minimum = 0, Maximum = 1000 };
            jump.MouseUp += (s, e) => JumpFromSlider(); jump.KeyUp += (s, e) => JumpFromSlider(); card.Controls.Add(jump);
            Button import = Ui.Button("打开", 12, 293, 76, 28, false); Controls.Add(import); import.Click += async (s, e) => await ImportDialog();
            start = Ui.Button("显示", 98, 293, 76, 28, false); start.Click += (s, e) => StartReading(); Controls.Add(start);
            Button settings = Ui.Button("设置", 184, 293, 76, 28, false); settings.Click += (s, e) => OpenSettings(); Controls.Add(settings);
            Button demo = Ui.Button("示例", 270, 293, 76, 28, false); demo.Click += (s, e) => AddDemo(); Controls.Add(demo);
            hotkeyInfo = Ui.Label(HotkeyCaption, 12, 330, 596, 20, 8, Ui.Muted); Controls.Add(hotkeyInfo);
            status = Ui.Label("可拖入 TXT / EPUB", 358, 293, 250, 28, 8, Ui.Muted); Controls.Add(status);
            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("文档", null, (s, e) => ShowLibrary());
            trayMenu.Items.Add("隐藏 / 恢复阅读条", null, (s, e) => ToggleReader());
            ToolStripMenuItem passThrough = new ToolStripMenuItem("鼠标穿透", null, (s, e) => ToggleClickThrough());
            trayMenu.Items.Add(passThrough); trayMenu.Opening += (s, e) => passThrough.Checked = store.Pref.ClickThrough;
            trayMenu.Items.Add("企业微信浅色样式", null, (s, e) => UsePreset(4));
            trayMenu.Items.Add("IDEA 深色样式", null, (s, e) => UsePreset(5));
            trayMenu.Items.Add("阅读设置", null, (s, e) => OpenSettings());
            trayMenu.Items.Add("退出", null, (s, e) => ExitApp());
            tray = new NotifyIcon { Icon = Icon, Text = "文档", Visible = true, ContextMenuStrip = trayMenu };
            tray.DoubleClick += (s, e) => ShowLibrary();
            DragEnter += (s, e) => { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += async (s, e) => await ImportFiles((string[])e.Data.GetData(DataFormats.FileDrop));
            saveTimer.Interval = 2000; saveTimer.Tick += (s, e) => { if (dirty) SaveNow(); }; saveTimer.Start();
            RefreshBooks(store.LastId);
            ApplyTheme();
            Shown += (s, e) =>
            {
                hotkeyInfo.Text = HotkeyCaption;
                if (hideShortcut.Length == 0) status.Text = "隐藏热键被占用，可用 Esc 隐藏、托盘恢复。";
                if (store.RecoveryNotice.Length > 0) MessageBox.Show(this, store.RecoveryNotice, "数据恢复", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
        }

        private static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (Pen p = new Pen(Color.FromArgb(208, 211, 216), 3)) { g.Clear(Color.FromArgb(83, 88, 96)); g.DrawLine(p, 7, 16, 25, 16); }
                IntPtr handle = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(handle).Clone(); Native.DestroyIcon(handle); return icon;
            }
        }
        private void DrawBook(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Book b = (Book)books.Items[e.Index]; bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush bg = new SolidBrush(selected ? (store.Pref.UiDark ? Color.FromArgb(43, 45, 48) : Color.FromArgb(220, 229, 241)) : books.BackColor)) e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, b.Title, books.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 5, e.Bounds.Width - 16, 20), Ui.TextColor(store.Pref.UiDark), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            using (Font small = Ui.Font(8)) TextRenderer.DrawText(e.Graphics, b.Format + "  ·  " + (b.Completed ? 100 : b.Length <= 1 ? 0 : (int)(100.0 * b.Position / (b.Length - 1))) + "%", small, new Point(e.Bounds.X + 9, e.Bounds.Y + 25), Ui.MutedColor(store.Pref.UiDark));
        }
        private void RefreshBooks(string selectedId)
        {
            books.BeginUpdate(); books.Items.Clear(); foreach (Book b in store.Books) books.Items.Add(b); books.EndUpdate();
            if (store.Books.Count > 0) books.SelectedIndex = Math.Max(0, store.Books.FindIndex(b => b.Id == selectedId));
            else { session = null; start.Enabled = false; jump.Enabled = false; preview.Text = "打开 TXT / EPUB，或拖入文件。"; }
        }
        private void SelectBook()
        {
            Book b = books.SelectedItem as Book; if (b == null) return;
            try
            {
                if (reader != null) { reader.Dispose(); reader = null; }
                session = new ReadingSession(b, store.TextFor(b)); store.LastId = b.Id; dirty = true;
                start.Enabled = true; jump.Enabled = true; RefreshPreview();
            }
            catch (Exception ex) { ShowError("无法读取书籍", ex); }
        }
        public void Changed()
        {
            dirty = true; RefreshPreview(); books.Invalidate();
        }
        private void RefreshPreview()
        {
            if (session == null) return;
            title.Text = session.Book.Title;
            metadata.Text = session.Book.Format + "   /   " + session.Text.Length.ToString("N0") + " 字符   /   进度自动保存";
            preview.Text = session.Text.Substring(session.Position, Math.Min(160, session.Text.Length - session.Position)).Replace("\n", "\r\n");
            progress.Text = session.Percent + "%";
            jump.Value = session.Book.Completed ? 1000 : session.Text.Length <= 1 ? 0 : (int)(1000L * session.Position / (session.Text.Length - 1));
        }
        private void JumpFromSlider()
        {
            if (session == null) return;
            session.Seek((int)((long)jump.Value * (session.Text.Length - 1) / 1000)); Changed(); if (reader != null) reader.RefreshLine();
        }
        private async Task ImportDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Title = "打开文档", Filter = "文档 (*.txt;*.epub)|*.txt;*.epub", Multiselect = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) await ImportFiles(dialog.FileNames);
        }
        private async Task ImportFiles(string[] paths)
        {
            if (busy) return; busy = true; Enabled = false;
            string last = null; int count = 0; List<string> failures = new List<string>();
            try
            {
                foreach (string path in paths)
                {
                    status.Text = "正在导入 " + Path.GetFileName(path) + "…";
                    try
                    {
                        ImportedBook imported = await Task.Run(() => Importer.Read(path));
                        Book b = store.Add(imported.Title, imported.Text, imported.Format); last = b.Id; count++; dirty = true;
                    }
                    catch (Exception ex) { failures.Add(Path.GetFileName(path) + "：" + ex.Message); }
                }
            }
            finally { busy = false; Enabled = true; }
            RefreshBooks(last ?? store.LastId); SaveNow(); status.Text = "已打开 " + count + " 个文件";
            if (failures.Count > 0) MessageBox.Show(this, String.Join("\n\n", failures), "部分文件未能打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        private void AddDemo()
        {
            string text = "一行之间\n午后的阳光落在窗边，给桌上的杯子镶了一圈温柔的金边。你把故事打开，整个世界便安静了一点。\n阅读不一定需要一整块时间。有时，一小段空隙，一行文字，就足够让心走到很远的地方。\n默认只显示一行正文，没有边框、圆点和进度数字。阅读设置里可以选择企业微信浅色、IDEA 深色或透明背景。\n按 Ctrl + Alt + 方向键翻页，Ctrl + Alt + 空格开始或暂停自动阅读。全局隐藏和鼠标穿透快捷键显示在书架底部。\n先拖动阅读条放到合适的位置，再开启鼠标穿透；开启后点击文字会操作下面的窗口，可以用热键或系统托盘解除。\n正文与进度保存在本机应用数据目录，程序文件夹只需保留 EXE 和两份说明。旧版旁边有 data 文件夹时，会在首次运行时复制旧进度。\n故事未完，下一本由你来选。";
            Book b = store.Add("示例.txt", Importer.Normalize(text), "示例"); RefreshBooks(b.Id); SaveNow();
        }
        private void StartReading()
        {
            ShowReader(true);
        }
        private void ShowReader(bool activate)
        {
            if (session == null) return;
            if (reader == null || reader.IsDisposed) reader = new ReaderForm(this, store, session);
            Hide(); reader.Show(); if (activate && !store.Pref.ClickThrough) reader.Activate();
        }
        public void ShowLibrary()
        {
            if (reader != null) { reader.Pause(); reader.Hide(); }
            RefreshPreview(); Show(); WindowState = FormWindowState.Normal; Activate();
        }
        private void ToggleReader()
        {
            if (Application.OpenForms.Cast<Form>().Any(f => f.Modal)) return;
            if (reader != null && !reader.IsDisposed && reader.Visible) { reader.Pause(); reader.Hide(); SaveNow(); }
            else if (session != null) ShowReader(false);
            else { if (Visible) Hide(); else ShowLibrary(); }
        }
        public void OpenSettings()
        {
            bool visible = reader != null && reader.Visible;
            if (reader != null) { reader.Pause(); reader.TopMost = false; reader.SetInputMode(false); }
            using (SettingsForm dialog = new SettingsForm(store.Pref)) dialog.ShowDialog(visible ? (IWin32Window)reader : this);
            if (reader != null) reader.ApplyPreferences(); ApplyTheme(); dirty = true; SaveNow();
        }
        public void ToggleClickThrough()
        {
            store.Pref.ClickThrough = !store.Pref.ClickThrough;
            if (reader != null) reader.SetInputMode(store.Pref.ClickThrough);
            dirty = true; SaveNow();
            status.Text = store.Pref.ClickThrough ? "鼠标穿透已开启，用快捷键或托盘解除。" : "鼠标穿透已关闭，可拖动、右键或滚轮操作阅读条。";
        }
        public void UsePreset(int theme)
        {
            store.Pref.UsePreset(theme); if (reader != null) reader.ApplyPreferences(); ApplyTheme(); dirty = true; SaveNow();
        }
        private void ApplyTheme()
        {
            Ui.Skin(this, store.Pref.UiDark);
            appearance.Text = store.Pref.UiDark ? "IDEA · 深色 ⇄" : "企业微信 · 浅色 ⇄";
            books.Invalidate(); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen border = new Pen(store.Pref.UiDark ? Color.FromArgb(54, 57, 61) : Color.FromArgb(217, 222, 228))) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys key)
        {
            if (key == Keys.Escape) { Hide(); SaveNow(); return true; }
            return base.ProcessCmdKey(ref msg, key);
        }
        public void SaveNow()
        {
            try { store.Save(); dirty = false; saveWarning = false; }
            catch (Exception ex)
            {
                dirty = true;
                if (!saveWarning) { saveWarning = true; MessageBox.Show("无法保存进度，请检查文件夹是否可写或磁盘是否已满。\n\n" + ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }
        public void ExitApp() { if (busy) return; allowExit = true; Close(); }
        private void ShowError(string message, Exception ex) { MessageBox.Show(this, message + "：" + ex.Message, "良子阅读器", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hideShortcut = "";
            foreach (Keys key in new Keys[] { Keys.H, Keys.F8, Keys.F9 })
            {
                if (!Native.RegisterHotKey(Handle, 1, 0x4003, (uint)key)) continue;
                registeredKeys.Add(1); hideShortcut = "Ctrl + Alt + " + key; break;
            }
            if (Native.RegisterHotKey(Handle, 2, 0x4003, (uint)Keys.Right)) registeredKeys.Add(2);
            if (Native.RegisterHotKey(Handle, 3, 0x4003, (uint)Keys.Left)) registeredKeys.Add(3);
            mouseShortcut = "";
            foreach (Keys key in new Keys[] { Keys.M, Keys.F10 })
            {
                if (!Native.RegisterHotKey(Handle, 4, 0x4003, (uint)key)) continue;
                registeredKeys.Add(4); mouseShortcut = "Ctrl + Alt + " + key; break;
            }
            if (Native.RegisterHotKey(Handle, 5, 0x4003, (uint)Keys.Space)) registeredKeys.Add(5);
            if (hotkeyInfo != null) hotkeyInfo.Text = HotkeyCaption;
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            foreach (int id in registeredKeys) Native.UnregisterHotKey(Handle, id);
            registeredKeys.Clear(); base.OnHandleDestroyed(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                int id = m.WParam.ToInt32();
                if (id == 1) ToggleReader();
                else if (id == 4 && !Application.OpenForms.Cast<Form>().Any(f => f.Modal)) ToggleClickThrough();
                else if (reader != null && reader.CanAdvance)
                {
                    if (id == 2 || id == 3) reader.MoveLine(id == 2);
                    else if (id == 5) reader.ToggleAuto();
                }
            }
            base.WndProc(ref m);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            if (!allowExit && e.CloseReason == CloseReason.UserClosing && reader != null && reader.Visible) { e.Cancel = true; Hide(); return; }
            SaveNow(); saveTimer.Stop();
            tray.Visible = false; tray.Dispose(); if (reader != null) reader.Dispose();
            base.OnFormClosing(e);
        }
    }

    internal sealed class ReaderForm : Form
    {
        private readonly LibraryForm library;
        private readonly Store store;
        private readonly ReadingSession session;
        private readonly Timer auto = new Timer();
        private Font readingFont;
        private int end;
        private int lineHeight;
        private ReadingPage page;
        private bool finished;
        private Color ink, background;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private ToolStripMenuItem autoItem;
        private readonly ToolTip tip = new ToolTip();
        public bool CanAdvance { get { return Visible && !menu.Visible && !Application.OpenForms.Cast<Form>().Any(f => f.Modal); } }
        public bool ClickThrough { get; private set; }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x80; if (ClickThrough) p.ExStyle |= 0x80000 | 0x20 | 0x08000000; return p; }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); SetInputMode(ClickThrough);
        }
        private int TextLeft { get { return store.Pref.Stealth ? 8 : 27; } }
        private int TextWidth { get { return Math.Max(1, ClientSize.Width - (store.Pref.Stealth ? 16 : 78)); } }

        public ReaderForm(LibraryForm library, Store store, ReadingSession session)
        {
            this.library = library; this.store = store; this.session = session; finished = session.Book.Completed;
            Text = "良子阅读器 · 悬浮阅读"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true; KeyPreview = true;
            MinimumSize = new Size(240, 22); MaximumSize = new Size(1800, 2000);
            Rectangle screen = Screen.FromControl(library).WorkingArea;
            Bounds = new Rectangle(store.Pref.X < 0 ? screen.Left + (screen.Width - store.Pref.Width) / 2 : store.Pref.X, store.Pref.Y < 0 ? screen.Bottom - 42 : store.Pref.Y, store.Pref.Width, 28);
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Bounds))) Location = new Point(screen.Left + 40, screen.Top + 80);
            menu.Font = Ui.Font(10);
            menu.Items.Add("下一页        → / 滚轮向下", null, (s, e) => MoveLine(true));
            menu.Items.Add("上一页        ← / 滚轮向上", null, (s, e) => MoveLine(false));
            autoItem = new ToolStripMenuItem("自动阅读      空格", null, (s, e) => ToggleAuto()); menu.Items.Add(autoItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("添加 / 更新书签       Ctrl+B", null, (s, e) => Bookmark());
            menu.Items.Add("回到书签               Ctrl+J", null, (s, e) => GoBookmark());
            menu.Items.Add("查找文字               Ctrl+F", null, (s, e) => Find());
            menu.Items.Add("跳转进度               Ctrl+G", null, (s, e) => Jump());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("阅读设置 / 行数 / 字体 / 透明度", null, (s, e) => library.OpenSettings());
            menu.Items.Add("鼠标穿透开关", null, (s, e) => library.ToggleClickThrough());
            menu.Items.Add("企业微信浅色样式", null, (s, e) => library.UsePreset(4));
            menu.Items.Add("IDEA 深色样式", null, (s, e) => library.UsePreset(5));
            menu.Items.Add("返回书架       双击阅读条", null, (s, e) => library.ShowLibrary());
            menu.Items.Add("隐藏阅读条     Esc", null, (s, e) => HideReader());
            menu.Items.Add("退出阅读器", null, (s, e) => library.ExitApp());
            ContextMenuStrip = menu;
            menu.Opening += (s, e) => Pause();
            auto.Tick += (s, e) => MoveLine(true);
            MouseWheel += (s, e) => MoveLine(e.Delta < 0);
            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && e.Clicks == 1) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
            MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) library.ShowLibrary(); };
            Resize += (s, e) => { store.Pref.Width = Width; session.ClearHistory(); RefreshLine(); library.Changed(); };
            LocationChanged += (s, e) => { store.Pref.X = Left; store.Pref.Y = Top; library.Changed(); };
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideReader(); } };
            ApplyPreferences();
        }
        public void ApplyPreferences()
        {
            session.ClearHistory();
            Font next;
            try { next = new Font(store.Pref.FontName, store.Pref.FontSize); }
            catch (ArgumentException) { next = Ui.Font(store.Pref.FontSize); }
            if (readingFont != null) readingFont.Dispose(); readingFont = next;
            Color[] backgrounds = { Color.FromArgb(248, 249, 246), Color.FromArgb(32, 36, 36), Color.FromArgb(247, 239, 218), Color.FromArgb(225, 229, 230), Color.FromArgb(245, 247, 250), Color.FromArgb(25, 26, 28), Color.Fuchsia };
            background = backgrounds[store.Pref.Theme]; ink = store.Pref.Theme == 1 ? Color.FromArgb(224, 231, 225) : store.Pref.Theme == 5 ? Color.FromArgb(135, 141, 150) : Color.FromArgb(112, 118, 121);
            BackColor = background; TransparencyKey = store.Pref.Theme == 6 ? Color.Fuchsia : Color.Empty;
            TopMost = store.Pref.TopMost; Opacity = store.Pref.Opacity / 100.0;
            store.Pref.LineCount = Store.Clamp(store.Pref.LineCount, 1, 10);
            lineHeight = TextRenderer.MeasureText("一行Ag", readingFont).Height;
            Height = Math.Max(22, lineHeight * store.Pref.LineCount + (store.Pref.Stealth ? 6 : 17)); auto.Interval = store.Pref.Seconds * 1000;
            Rectangle screen = Screen.FromControl(this).WorkingArea;
            if (Bottom > screen.Bottom) Top = Math.Max(screen.Top, screen.Bottom - Height);
            tip.Active = !store.Pref.Stealth; SetInputMode(store.Pref.ClickThrough);
            RefreshLine();
        }
        public void SetInputMode(bool passThrough)
        {
            ClickThrough = passThrough;
            if (!IsHandleCreated) return;
            int style = Native.GetWindowLong(Handle, -20);
            int flags = 0x20 | 0x08000000;
            style = passThrough ? style | flags | 0x80000 : style & ~flags;
            Native.SetWindowLong(Handle, -20, style);
            if ((style & 0x80000) != 0)
                Native.SetLayeredWindowAttributes(Handle, 0x00FF00FF, (byte)Math.Round(255 * store.Pref.Opacity / 100.0), store.Pref.Theme == 6 ? 3u : 2u);
        }
        private bool Fits(string value)
        {
            return TextRenderer.MeasureText(value, readingFont, new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= TextWidth;
        }
        public void RefreshLine()
        {
            if (readingFont == null || IsDisposed) return;
            page = session.Layout(Fits, store.Pref.LineCount); end = page.End; Invalidate();
            AccessibleName = "悬浮正文"; AccessibleDescription = session.Text.Substring(session.Position, end - session.Position);
            tip.SetToolTip(this, session.Book.Title + " · " + session.Percent + "%" + (finished ? " · 已读完" : "") + "\n右键菜单 · 拖动移动 · 双击书架");
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (readingFont == null) return;
            if (!store.Pref.Stealth && store.Pref.Theme != 6)
            {
                using (Pen line = new Pen(store.Pref.Theme == 1 ? Color.FromArgb(61, 67, 63) : Color.FromArgb(209, 218, 208))) e.Graphics.DrawRectangle(line, 0, 0, Width - 1, Height - 1);
                using (Brush marker = new SolidBrush(auto.Enabled ? Color.FromArgb(89, 169, 121) : Color.FromArgb(138, 155, 143))) e.Graphics.FillEllipse(marker, 12, Height / 2 - 2, 4, 4);
                using (Font small = Ui.Font(8)) TextRenderer.DrawText(e.Graphics, finished ? "完" : session.Percent + "%", small, new Rectangle(Width - 48, 0, 38, Height), Color.FromArgb(135, 148, 138), TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            if (page == null) return;
            int textTop = (Height - lineHeight * store.Pref.LineCount) / 2;
            for (int row = 0; row < page.Lines.Count; row++)
            {
                string lineText = page.Lines[row];
                Rectangle bounds = new Rectangle(TextLeft, textTop + row * lineHeight, TextWidth, lineHeight);
                if (store.Pref.Theme == 6)
                {
                    e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    using (Brush text = new SolidBrush(ink))
                    using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                    {
                        format.FormatFlags |= StringFormatFlags.NoWrap;
                        e.Graphics.DrawString(lineText, readingFont, text, new PointF(bounds.Left, bounds.Top + (lineHeight - readingFont.GetHeight(e.Graphics)) / 2), format);
                    }
                }
                else TextRenderer.DrawText(e.Graphics, lineText, readingFont, bounds, ink, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
            }
        }
        public void MoveLine(bool next)
        {
            if (next) { if (!session.Next(end)) { finished = true; Pause(); } }
            else { session.Previous(Fits, store.Pref.LineCount); finished = false; }
            RefreshLine(); library.Changed();
        }
        public void ToggleAuto() { auto.Enabled = !auto.Enabled; autoItem.Checked = auto.Enabled; Invalidate(); }
        public void Pause() { auto.Stop(); autoItem.Checked = false; Invalidate(); }
        private void HideReader() { Pause(); Hide(); library.SaveNow(); }
        private void Bookmark() { session.Book.Bookmark = session.Position; library.Changed(); library.SaveNow(); tip.Show("书签已保存", this, 25, Height, 1800); }
        private void GoBookmark()
        {
            if (session.Book.Bookmark < 0) { tip.Show("还没有书签，按 Ctrl+B 添加", this, 25, Height, 2000); return; }
            session.Seek(session.Book.Bookmark); finished = false; RefreshLine(); library.Changed();
        }
        private string Prompt(string title, string label, string initial)
        {
            Pause(); bool top = TopMost; TopMost = false;
            try { using (PromptForm form = new PromptForm(title, label, initial)) return form.ShowDialog(this) == DialogResult.OK ? form.Value : null; }
            finally { TopMost = top; }
        }
        private void Find()
        {
            string value = Prompt("查找文字", "从当前位置向后查找，末尾自动从头继续。", "");
            if (String.IsNullOrWhiteSpace(value)) return;
            int index = session.Text.IndexOf(value, Math.Min(session.Position + 1, session.Text.Length), StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) index = session.Text.IndexOf(value, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) { tip.Show("没有找到这段文字", this, 25, Height, 2200); return; }
            session.Seek(index); finished = false; RefreshLine(); library.Changed();
        }
        private void Jump()
        {
            string value = Prompt("跳转进度", "输入 0 到 100 的百分比（按字符位置估算）。", session.Percent.ToString());
            if (value == null) return;
            double number;
            if (!Double.TryParse(value.Trim().TrimEnd('%'), out number) || Double.IsNaN(number) || number < 0 || number > 100) { tip.Show("请输入 0 到 100 之间的数字", this, 25, Height, 2200); return; }
            session.Seek((int)(number * (session.Text.Length - 1) / 100)); finished = false; RefreshLine(); library.Changed();
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys key)
        {
            if (key == Keys.Right || key == Keys.Down || key == Keys.PageDown) MoveLine(true);
            else if (key == Keys.Left || key == Keys.Up || key == Keys.PageUp) MoveLine(false);
            else if (key == Keys.Space) ToggleAuto();
            else if (key == Keys.Escape) HideReader();
            else if (key == (Keys.Control | Keys.B)) Bookmark();
            else if (key == (Keys.Control | Keys.J)) GoBookmark();
            else if (key == (Keys.Control | Keys.F)) Find();
            else if (key == (Keys.Control | Keys.G)) Jump();
            else if (key == Keys.F1) library.ShowLibrary();
            else return base.ProcessCmdKey(ref msg, key);
            return true;
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0xA3 && m.WParam.ToInt32() == 2) { library.ShowLibrary(); return; }
            base.WndProc(ref m);
            if (m.Msg == 0x84 && (int)m.Result == 1)
            {
                int screenX = unchecked((short)(long)m.LParam);
                int x = PointToClient(new Point(screenX, 0)).X;
                if (x <= 7) m.Result = new IntPtr(10);
                else if (x >= Width - 8) m.Result = new IntPtr(11);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { auto.Dispose(); tip.Dispose(); menu.Dispose(); if (readingFont != null) readingFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class SettingsForm : Form
    {
        public SettingsForm(Preferences pref)
        {
            Text = "阅读设置"; ClientSize = new Size(438, 524); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; BackColor = Ui.Paper; Font = Ui.Font(10); AutoScaleMode = AutoScaleMode.None;
            Controls.Add(Ui.Label("显示设置", 26, 18, 390, 35, 11, Ui.Ink));
            Controls.Add(Ui.Label("外观", 28, 80, 100, 28, 10, Ui.Muted));
            ComboBox theme = new ComboBox { Bounds = new Rectangle(157, 80, 250, 30), DropDownStyle = ComboBoxStyle.DropDownList };
            theme.Items.AddRange(new object[] { "浅色 · 雾白", "深色 · 夜读", "纸色 · 暖页", "灰色 · 低调", "企业微信 · 浅色", "IDEA · 深色", "透明 · 只留文字" }); theme.SelectedIndex = pref.Theme; Controls.Add(theme);
            Controls.Add(Ui.Label("字体", 28, 128, 100, 28, 10, Ui.Muted));
            Button font = Ui.Button(pref.FontName + "  " + pref.FontSize + " pt", 157, 127, 250, 34, false); Controls.Add(font);
            string selectedName = pref.FontName; float selectedSize = pref.FontSize;
            font.Click += (s, e) =>
            {
                using (FontDialog dialog = new FontDialog { Font = new Font(selectedName, selectedSize), MinSize = 9, MaxSize = 32, ShowEffects = false, FontMustExist = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) { selectedName = dialog.Font.Name; selectedSize = (float)Math.Round(dialog.Font.Size); font.Text = selectedName + "  " + selectedSize + " pt"; }
            };
            NumericUpDown lineCount = new NumericUpDown { Name = "LineCount", Bounds = new Rectangle(157, 178, 90, 30), Minimum = 1, Maximum = 10, Value = Store.Clamp(pref.LineCount, 1, 10) };
            Label opacityLabel = Ui.Label("不透明度 " + pref.Opacity + "%", 28, 181, 130, 28, 10, Ui.Muted); Controls.Add(opacityLabel);
            TrackBar opacity = new TrackBar { Bounds = new Rectangle(150, 178, 262, 34), Minimum = 30, Maximum = 100, TickStyle = TickStyle.None, AutoSize = false, Value = pref.Opacity }; Controls.Add(opacity);
            opacity.ValueChanged += (s, e) => opacityLabel.Text = "不透明度 " + opacity.Value + "%";
            Controls.Add(Ui.Label("自动翻页间隔", 28, 230, 130, 28, 10, Ui.Muted));
            NumericUpDown seconds = new NumericUpDown { Bounds = new Rectangle(157, 230, 90, 30), Minimum = 2, Maximum = 60, Value = pref.Seconds }; Controls.Add(seconds);
            Controls.Add(Ui.Label("秒 / 页", 261, 230, 120, 28, 10, Ui.Muted));
            CheckBox top = new CheckBox { Text = "阅读条始终置顶", Bounds = new Rectangle(28, 281, 300, 30), Checked = pref.TopMost }; Controls.Add(top);
            CheckBox stealth = new CheckBox { Text = "极简阅读条：隐藏边框、圆点和进度", Bounds = new Rectangle(28, 316, 380, 30), Checked = pref.Stealth }; Controls.Add(stealth);
            CheckBox pass = new CheckBox { Text = "鼠标穿透：点击文字仍操作下方窗口", Bounds = new Rectangle(28, 351, 380, 30), Checked = pref.ClickThrough }; Controls.Add(pass);
            Controls.Add(Ui.Label("先拖动定位，再开启穿透。用热键或托盘随时解除。", 28, 385, 383, 25, 9, Ui.Muted));
            Button save = Ui.Button("保存设置", 283, 426, 124, 35, true); Controls.Add(save); AcceptButton = save;
            Button cancel = Ui.Button("取消", 157, 426, 110, 35, false); cancel.DialogResult = DialogResult.Cancel; Controls.Add(cancel); CancelButton = cancel;
            save.Click += (s, e) => { pref.Theme = theme.SelectedIndex; if (pref.Theme != 6) pref.UiDark = pref.Theme == 1 || pref.Theme == 5; pref.FontName = selectedName; pref.FontSize = selectedSize; pref.LineCount = (int)lineCount.Value; pref.Opacity = opacity.Value; pref.Seconds = (int)seconds.Value; pref.TopMost = top.Checked; pref.Stealth = stealth.Checked; pref.ClickThrough = pass.Checked; DialogResult = DialogResult.OK; };
            foreach (Control control in Controls) if (control.Top >= 178) control.Top += 48;
            Controls.Add(Ui.Label("显示行数", 28, 179, 100, 28, 10, Ui.Muted)); Controls.Add(lineCount);
            Controls.Add(Ui.Label("行（1～10）", 261, 179, 146, 28, 10, Ui.Muted));
            Ui.Skin(this, pref.UiDark);
        }
    }

    internal sealed class PromptForm : Form
    {
        private readonly TextBox input;
        public string Value { get { return input.Text; } }
        public PromptForm(string title, string description, string initial)
        {
            Text = title; ClientSize = new Size(430, 164); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; BackColor = Ui.Paper; AutoScaleMode = AutoScaleMode.None;
            Controls.Add(Ui.Label(description, 22, 15, 386, 35, 9, Ui.Muted));
            input = new TextBox { Bounds = new Rectangle(23, 58, 383, 28), Text = initial, Font = Ui.Font(11) }; Controls.Add(input);
            Button ok = Ui.Button("确定", 299, 110, 107, 33, true); ok.DialogResult = DialogResult.OK; Controls.Add(ok); AcceptButton = ok;
            Button cancel = Ui.Button("取消", 179, 110, 107, 33, false); cancel.DialogResult = DialogResult.Cancel; Controls.Add(cancel); CancelButton = cancel;
            Shown += (s, e) => { input.Focus(); input.SelectAll(); };
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr window, int msg, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
    }
}
