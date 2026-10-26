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
    public sealed partial class Store
    {
        private readonly SemaphoreSlim writes = new(1, 1);
        public readonly string Root;
        public readonly List<Book> Books = new List<Book>();
        // 分组清单：文档顺序就是显示顺序。分组是独立实体，所以空分组能存活，
        // 而书只持有分组名（见 Book.Group）。
        private readonly List<string> groups = new List<string>();
        public readonly Preferences Pref = new Preferences();
        public string LastId = "";
        public string RecoveryNotice = "";

        public Store(string root)
        {
            Root = root;
            Directory.CreateDirectory(Path.Combine(root, "books"));
            RecoverPendingRemovals();
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
                    string id = Path.GetFileNameWithoutExtension(file);
                    if (!IsBookId(id)) continue;
                    string content = File.ReadAllText(file, Encoding.UTF8);
                    Books.Add(new Book { Id = id, Title = "恢复的书籍 " + (Books.Count + 1), Format = "TXT", Length = content.Length });
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
                if (!IsBookId(id)) continue;
                if (!File.Exists(Path.Combine(Root, "books", id + ".txt"))) continue;
                loaded.Add(new Book { Id = id, Title = (string)b.Attribute("title") ?? "未命名", Format = (string)b.Attribute("format") ?? "TXT", Position = Number(b, "position", 0), Bookmark = Number(b, "bookmark", -1), Length = Number(b, "length", 0), Completed = (string)b.Attribute("completed") == "true", IsPinned = (string)b.Attribute("pinned") == "true", Group = ReadGroup((string)b.Attribute("group")) });
            }
            XElement p = root.Element("preferences");
            if (p != null)
            {
                Pref.Width = Clamp(Number(p, "width", 560), 240, 1800);
                Pref.X = Number(p, "x", -1); Pref.Y = Number(p, "y", -1);
                Pref.HasPosition = (string)p.Attribute("hasPosition") == "true" || Pref.X != -1 || Pref.Y != -1;
                Pref.Seconds = Clamp(Number(p, "seconds", 6), 2, 60);
                Pref.LineCount = Clamp(Number(p, "lineCount", 1), 1, 10);
                Pref.Opacity = Clamp(Number(p, "opacity", 100), 30, 100);
                Pref.Theme = Clamp(Number(p, "theme", 4), 0, 11);
                Pref.FontSize = Clamp(Number(p, "fontSize", 11), 9, 32);
                Pref.FontName = (string)p.Attribute("fontName") ?? "Microsoft YaHei UI";
                Pref.TopMost = (string)p.Attribute("topMost") != "false";
                Pref.Stealth = (string)p.Attribute("stealth") != "false";
                Pref.ClickThrough = (string)p.Attribute("clickThrough") == "true";
                Pref.UiDark = (string)p.Attribute("uiDark") == null ? Pref.Theme is 1 or 5 or >= 7 : (string)p.Attribute("uiDark") == "true";
                Pref.ChatHost = (string)p.Attribute("chatHost") ?? "";
                Pref.UiTheme = (string)p.Attribute("uiTheme") ?? "";
                Pref.ReadingThemeId = (string)p.Attribute("readingThemeId") ?? "";
                Pref.ChatNick = (string)p.Attribute("chatNick") ?? "";
                Pref.ChatPort = Clamp(Number(p, "chatPort", 38417), 1, 65535);
                Pref.ExitMode = Clamp(Number(p, "exitMode", Preferences.ExitUnset), Preferences.ExitUnset, Preferences.ExitProgram);
            }
            LoadGroups(root, loaded);
            Books.Clear(); Books.AddRange(loaded);
            LastId = (string)root.Attribute("last") ?? "";
        }

        private void LoadGroups(XElement root, List<Book> loaded)
        {
            groups.Clear();
            XElement list = root.Element("groups");
            if (list != null)
            {
                foreach (XElement entry in list.Elements("group"))
                {
                    string name = ReadGroup((string)entry.Attribute("name"));
                    if (name.Length > 0 && IndexOf(name) < 0) groups.Add(name);
                }
            }
            else
            {
                // 旧版没有分组清单：按旧界面用的顺序（当前区域文化的字母序）推导，
                // 否则升级后下拉框里的分组会莫名其妙地重新排队。
                foreach (string name in loaded.Select(book => book.Group).Where(name => name.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.CurrentCulture))
                    groups.Add(name);
            }
            // 自愈：书引用了但清单里没有的分组（文件被手工改过，或旧版写入的新名字）补到末尾。
            foreach (string name in loaded.Select(book => book.Group).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                if (IndexOf(name) < 0) groups.Add(name);
        }

        private static int Number(XElement e, string name, int fallback)
        {
            int value;
            return int.TryParse((string)e.Attribute(name), out value) ? value : fallback;
        }
        public static int Clamp(int n, int min, int max) { return Math.Max(min, Math.Min(max, n)); }

        // 书籍 ID 是正文 SHA-256 的小写十六进制；集中校验，避免各处正则写法漂移。
        private static readonly Regex BookIdPattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled);
        internal static bool IsBookId(string id) => !String.IsNullOrEmpty(id) && BookIdPattern.IsMatch(id);

        public string TextFor(Book b) { return File.ReadAllText(Path.Combine(Root, "books", b.Id + ".txt"), Encoding.UTF8); }

        private const long ChapterIndexLimit = 4L * 1024 * 1024;

        private string ChaptersPath(string id) => Path.Combine(Root, "books", id + ".chapters.tsv");

        /// <summary>读取随书保存的章节索引；没有、损坏或超限时返回 null，阅读端会回退到标题识别。</summary>
        public IReadOnlyList<Chapter> ChaptersFor(Book book)
        {
            try
            {
                string path = ChaptersPath(book.Id);
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length > ChapterIndexLimit) return null;
                List<Chapter> chapters = new List<Chapter>();
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || !Int32.TryParse(line.AsSpan(0, tab), out int position) || position < 0) continue;
                    string title = line[(tab + 1)..].Trim();
                    if (title.Length > 0) chapters.Add(new Chapter(title, position));
                }
                // sidecar 正常由导入器按位置写出；这里再排一次，防御手工修改或旧版本文件。
                chapters.Sort((left, right) => left.Position.CompareTo(right.Position));
                return chapters.Count == 0 ? null : chapters;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>写入章节索引。失败不影响正文导入，阅读端会回退到启发式识别。</summary>
        public void WriteChapters(Book book, IReadOnlyList<Chapter> chapters)
        {
            if (chapters == null || chapters.Count == 0) return;
            string target = ChaptersPath(book.Id), temporary = target + ".tmp";
            StringBuilder text = new StringBuilder();
            foreach (Chapter chapter in chapters)
                text.Append(chapter.Position).Append('\t').Append(chapter.Title.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
            try
            {
                File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
                File.Move(temporary, target, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// 加入一本书。EPUB 调用方可以同时传入书内目录生成的章节索引；
        /// 索引写成与正文同名的 sidecar（books/&lt;id&gt;.chapters.tsv），不进入 state.xml。
        /// </summary>
        public Book Add(string title, string text, string format, IReadOnlyList<Chapter> chapters = null)
        {
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidDataException("文件中没有可阅读的正文。");
            string id;
            using (SHA256 hash = SHA256.Create()) id = String.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(x => x.ToString("x2")));
            // 先落盘再判断重复：已存在的书也可能因为旧版本的半截写入而需要修复。
            WriteText(id, text);
            Book existing = Books.FirstOrDefault(b => b.Id == id);
            if (existing != null) { WriteChapters(existing, chapters); return existing; }
            Book book = new Book { Id = id, Title = title, Format = format, Length = text.Length };
            Books.Insert(0, book);
            WriteChapters(book, chapters);
            return book;
        }

        /// <summary>原子写入正文；已存在文件的长度不一致时重写，修复旧版本留下的半截文件。</summary>
        private void WriteText(string id, string text)
        {
            string path = Path.Combine(Root, "books", id + ".txt");
            if (File.Exists(path) && new FileInfo(path).Length == Encoding.UTF8.GetByteCount(text)) return;
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }

        public void Save()
        {
            XElement snapshot = Snapshot();
            writes.Wait();
            try { WriteSnapshot(snapshot); }
            finally { writes.Release(); }
        }

        public async Task SaveAsync()
        {
            // Capture mutable reading state on its owner thread before yielding.
            XElement snapshot = Snapshot();
            await writes.WaitAsync().ConfigureAwait(false);
            try { await Task.Run(() => WriteSnapshot(snapshot)).ConfigureAwait(false); }
            finally { writes.Release(); }
        }

        private XElement Snapshot()
        {
            XElement root = new XElement("reader", new XAttribute("version", "1"), new XAttribute("last", LastId),
                // 空清单也要写出，否则空分组活不过一次重启。
                new XElement("groups", groups.Select(name => new XElement("group", new XAttribute("name", name)))),
                new XElement("preferences", new XAttribute("width", Pref.Width), new XAttribute("x", Pref.X), new XAttribute("y", Pref.Y), new XAttribute("hasPosition", Pref.HasPosition),
                    new XAttribute("seconds", Pref.Seconds), new XAttribute("lineCount", Pref.LineCount), new XAttribute("opacity", Pref.Opacity), new XAttribute("theme", Pref.Theme),
                    new XAttribute("fontSize", (int)Pref.FontSize), new XAttribute("fontName", Pref.FontName), new XAttribute("topMost", Pref.TopMost ? "true" : "false"),
                    new XAttribute("chatHost", Pref.ChatHost), new XAttribute("chatNick", Pref.ChatNick), new XAttribute("chatPort", Pref.ChatPort), new XAttribute("uiTheme", Pref.UiTheme), new XAttribute("readingThemeId", Pref.ReadingThemeId), new XAttribute("exitMode", Pref.ExitMode),
                    new XAttribute("stealth", Pref.Stealth ? "true" : "false"), new XAttribute("clickThrough", Pref.ClickThrough ? "true" : "false"), new XAttribute("uiDark", Pref.UiDark ? "true" : "false")));
            foreach (Book b in Books) root.Add(new XElement("book", new XAttribute("id", b.Id), new XAttribute("title", b.Title), new XAttribute("format", b.Format), new XAttribute("position", b.Position), new XAttribute("bookmark", b.Bookmark), new XAttribute("length", b.Length), new XAttribute("completed", b.Completed ? "true" : "false"), new XAttribute("pinned", b.IsPinned ? "true" : "false"), new XAttribute("group", b.Group)));
            return root;
        }

        private void WriteSnapshot(XElement root)
        {
            string target = Path.Combine(Root, "state.xml"), temp = target + ".tmp";
            File.WriteAllText(temp, root.ToString(), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temp, target, target + ".bak", true);
            else File.Move(temp, target);
        }
    }


