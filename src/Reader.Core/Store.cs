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
    /// 书库核心：加载/保存 state.xml、维护书籍列表与偏好、原子写入正文和章节索引，
    /// 并用一把写锁串行化所有落盘操作。分组相关成员见 BookLibrary.cs。
    /// </summary>
    public sealed partial class Store
    {
        // 所有落盘操作共用同一把锁：state.xml、正文和章节索引不会互相穿插写入。
        private readonly SemaphoreSlim writes = new(1, 1);
        // 快照版本在 snapshotGate 内递增：版本更大代表内容更新，落盘时据此跳过过期快照。
        private readonly object snapshotGate = new();
        private long snapshotVersion;
        /// <summary>书库数据根目录。</summary>
        public readonly string Root;
        /// <summary>内存中的书籍列表，顺序即书库显示顺序（新书插入最前）。</summary>
        public readonly List<Book> Books = new List<Book>();
        // 分组清单：文档顺序就是显示顺序。分组是独立实体，所以空分组能存活，
        // 而书只持有分组名（见 Book.Group）。
        private readonly List<string> groups = new List<string>();
        /// <summary>界面与阅读偏好。</summary>
        public readonly Preferences Pref = new Preferences();
        /// <summary>最后阅读的书籍 ID；空字符串表示没有，用于启动时恢复上次的书。</summary>
        public string LastId = "";
        /// <summary>加载时的恢复/损坏提示；空字符串表示一切正常，非空时界面应提示用户。</summary>
        public string RecoveryNotice = "";

        /// <summary>打开（必要时创建）指定目录下的书库，并加载 state.xml。</summary>
        /// <param name="root">书库数据根目录；不存在时会创建 books 子目录。</param>
        /// <remarks>索引损坏时依次尝试 .bak 和按正文重建，并通过 <see cref="RecoveryNotice"/> 报告结果。</remarks>
        public Store(string root)
        {
            Root = root;
            Directory.CreateDirectory(Path.Combine(root, "books"));
            // 必须先回收上次删除留下的临时文件，再读索引，否则可能把已提交删除的书误判为缺失。
            RecoverPendingRemovals();
            string state = Path.Combine(root, "state.xml");
            // 首次运行：没有 state.xml 就是空书库，不需要报错。
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

        // 严格按 XML 结构读取；非法书籍 ID 或正文缺失的条目直接跳过，不让坏条目拖垮整库。
        private void Load(string path)
        {
            XElement root = Importer.ReadXml(File.ReadAllText(path, Encoding.UTF8)).Root;
            if (root == null || root.Name != "reader") throw new XmlException("无效的进度文件。");
            List<Book> loaded = new List<Book>();
            foreach (XElement b in root.Elements("book"))
            {
                string id = (string)b.Attribute("id") ?? "";
                if (!IsBookId(id)) continue;
                // 索引里有记录但正文文件不在时同样跳过，避免打开一本不存在的书。
                if (!File.Exists(Path.Combine(Root, "books", id + ".txt"))) continue;
                loaded.Add(new Book { Id = id, Title = (string)b.Attribute("title") ?? "未命名", Format = (string)b.Attribute("format") ?? "TXT", Position = Number(b, "position", 0), Bookmark = Number(b, "bookmark", -1), Length = Number(b, "length", 0), Completed = (string)b.Attribute("completed") == "true", IsPinned = (string)b.Attribute("pinned") == "true", Group = ReadGroup((string)b.Attribute("group")) });
            }
            // 没有 preferences 节点时沿用 Preferences 的默认值。
            XElement p = root.Element("preferences");
            if (p != null)
            {
                // 数值偏好统一夹取到合法范围，兼容旧版本或手工修改过的文件。
                Pref.Width = Clamp(Number(p, "width", 560), 240, 1800);
                Pref.X = Number(p, "x", -1); Pref.Y = Number(p, "y", -1);
                // 旧版本没有 hasPosition 属性：非默认坐标也视为有窗口位置。
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
                // 没有 uiDark 属性时按主题编号推断是否为深色界面。
                Pref.UiDark = (string)p.Attribute("uiDark") == null ? Pref.Theme is 1 or 5 or >= 7 : (string)p.Attribute("uiDark") == "true";
                Pref.ChatHost = (string)p.Attribute("chatHost") ?? "";
                Pref.UiTheme = (string)p.Attribute("uiTheme") ?? "";
                Pref.ReadingThemeId = (string)p.Attribute("readingThemeId") ?? "";
                Pref.ChatNick = (string)p.Attribute("chatNick") ?? "";
                Pref.ChatPort = Clamp(Number(p, "chatPort", 38417), 1, 65535);
                Pref.ExitMode = Clamp(Number(p, "exitMode", Preferences.ExitUnset), Preferences.ExitUnset, Preferences.ExitProgram);
            }
            // 先解析分组清单，再整体替换 Books，保证分组自愈逻辑能看到所有书。
            LoadGroups(root, loaded);
            // 全部解析成功后才替换内存状态，避免加载失败留下半套数据。
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

        // 缺失或非数字的 XML 属性回退到默认值，兼容旧版本与手工编辑。
        private static int Number(XElement e, string name, int fallback)
        {
            int value;
            return int.TryParse((string)e.Attribute(name), out value) ? value : fallback;
        }
        /// <summary>把 <paramref name="n"/> 夹取到 [<paramref name="min"/>, <paramref name="max"/>]。</summary>
        /// <param name="n">待夹取的值。</param>
        /// <param name="min">下界（含）。</param>
        /// <param name="max">上界（含）。</param>
        /// <returns>夹取后的值。</returns>
        public static int Clamp(int n, int min, int max) { return Math.Max(min, Math.Min(max, n)); }

        // 书籍 ID 是正文 SHA-256 的小写十六进制；集中校验，避免各处正则写法漂移。
        private static readonly Regex BookIdPattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled);
        /// <summary>判断字符串是否为合法的书籍 ID（64 位小写十六进制 SHA-256）。</summary>
        /// <param name="id">待校验的字符串。</param>
        /// <returns>合法返回 true；null、空串或格式不符返回 false。</returns>
        internal static bool IsBookId(string id) => !String.IsNullOrEmpty(id) && BookIdPattern.IsMatch(id);

        /// <summary>按 UTF-8 读取书籍正文。</summary>
        /// <param name="b">目标书籍；其 <see cref="Book.Id"/> 必须对应已有正文文件。</param>
        /// <returns>正文全文。</returns>
        public string TextFor(Book b) { return File.ReadAllText(Path.Combine(Root, "books", b.Id + ".txt"), Encoding.UTF8); }

        // 章节索引 sidecar 的体积上限；超过时视为损坏或异常文件，不再读取。
        private const long ChapterIndexLimit = 4L * 1024 * 1024;

        // sidecar 与正文同名，扩展名为 .chapters.tsv。
        private string ChaptersPath(string id) => Path.Combine(Root, "books", id + ".chapters.tsv");

        /// <summary>读取随书保存的章节索引；没有、损坏或超限时返回 null，阅读端会回退到标题识别。</summary>
        /// <param name="book">目标书籍。</param>
        /// <returns>按位置升序的章节；文件不存在、为空、损坏或超过 4 MB 时返回 null。</returns>
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
        /// <param name="book">目标书籍；sidecar 路径由 ID 决定。</param>
        /// <param name="chapters">章节列表；null 或空列表表示不写。</param>
        /// <remarks>写入采用“临时文件 + 原子替换”，失败时忽略并等待阅读端回退。</remarks>
        public void WriteChapters(Book book, IReadOnlyList<Chapter> chapters)
        {
            if (chapters == null || chapters.Count == 0) return;
            string target = ChaptersPath(book.Id), temporary = target + ".tmp";
            StringBuilder text = new StringBuilder();
            // 标题中的制表符和换行会破坏 TSV 行结构，写入前替换为空格。
            foreach (Chapter chapter in chapters)
                text.Append(chapter.Position).Append('\t').Append(chapter.Title.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
            try
            {
                // 先写临时文件再原子替换，避免阅读端读到半截索引。
                File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
                File.Move(temporary, target, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理临时文件失败可以忽略；下次写入会覆盖同名临时文件。
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// 加入一本书。EPUB 调用方可以同时传入书内目录生成的章节索引；
        /// 索引写成与正文同名的 sidecar（books/&lt;id&gt;.chapters.tsv），不进入 state.xml。
        /// </summary>
        /// <param name="title">书名。</param>
        /// <param name="text">规范化后的正文，不能为空。</param>
        /// <param name="format">来源格式，通常为 "TXT" 或 "EPUB"。</param>
        /// <param name="chapters">可选的章节索引，写入 sidecar；null 表示不写。</param>
        /// <returns>已存在或新建的书籍对象。</returns>
        /// <remarks>ID 由正文 SHA-256 推导，同样内容重复导入会返回已有书。</remarks>
        public Book Add(string title, string text, string format, IReadOnlyList<Chapter> chapters = null)
        {
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidDataException("文件中没有可阅读的正文。");
            string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            // 先落盘再判断重复：已存在的书也可能因为旧版本的半截写入而需要修复。
            WriteText(id, text);
            Book existing = Books.FirstOrDefault(b => b.Id == id);
            // 相同正文视为同一本书：只补充章节索引，保留原有书名、进度和组织信息。
            if (existing != null) { WriteChapters(existing, chapters); return existing; }
            Book book = new Book { Id = id, Title = title, Format = format, Length = text.Length };
            // 新书插到列表最前，符合“最近导入”的显示顺序。
            Books.Insert(0, book);
            WriteChapters(book, chapters);
            return book;
        }

        /// <summary>原子写入正文；已存在文件的长度不一致时重写，修复旧版本留下的半截文件。</summary>
        /// <param name="id">书籍 ID。</param>
        /// <param name="text">完整正文。</param>
        private void WriteText(string id, string text)
        {
            string path = Path.Combine(Root, "books", id + ".txt");
            // 长度相同即认为内容一致：ID 来自正文哈希，同名文件理论上就是这份正文；也避免重复导入时整书重写。
            if (File.Exists(path) && new FileInfo(path).Length == Encoding.UTF8.GetByteCount(text)) return;
            // 临时文件与目标同目录，File.Move 才能在同一卷上原子替换。
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写入失败时尽量清理临时文件，但保留原始异常。
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }

        /// <summary>同步保存书库索引；与异步保存共用同一把写锁。</summary>
        /// <remarks>快照在调用线程生成，写锁只保证落盘顺序；已有更新快照排队时会跳过本次。</remarks>
        public void Save()
        {
            (XElement snapshot, long version) = CaptureSnapshot();
            writes.Wait();
            try
            {
                // 并发保存时只让最新的快照落盘，防止旧状态覆盖新状态。
                if (version == Volatile.Read(ref snapshotVersion)) WriteSnapshot(snapshot);
            }
            finally { writes.Release(); }
        }

        /// <summary>异步保存书库索引；快照在调用线程生成，落盘在线程池执行。</summary>
        /// <remarks>与同步保存共享写锁和快照版本，旧快照不会覆盖新快照。</remarks>
        public async Task SaveAsync()
        {
            // Capture mutable reading state on its owner thread before yielding.
            (XElement snapshot, long version) = CaptureSnapshot();
            await writes.WaitAsync().ConfigureAwait(false);
            try
            {
                if (version == Volatile.Read(ref snapshotVersion))
                    await Task.Run(() => WriteSnapshot(snapshot)).ConfigureAwait(false);
            }
            finally { writes.Release(); }
        }

        // 串行化“生成快照 + 分配版本”，保证版本顺序与内容顺序一致；读取仍在调用线程完成。
        private (XElement Snapshot, long Version) CaptureSnapshot()
        {
            lock (snapshotGate)
            {
                return (Snapshot(), ++snapshotVersion);
            }
        }

        // 把当前内存状态序列化成独立 XML 树；落盘时不再访问可变字段，避免并发修改写进文件。
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

        // 先写同目录 .tmp，再原子替换；File.Replace 同时把旧文件保存为 .bak。
        private void WriteSnapshot(XElement root)
        {
            string target = Path.Combine(Root, "state.xml"), temp = target + ".tmp";
            File.WriteAllText(temp, root.ToString(), new UTF8Encoding(false));
            // 首次保存没有旧文件时只能 Move；.tmp 与目标同目录保证重命名不跨卷。
            if (File.Exists(target)) File.Replace(temp, target, target + ".bak", true);
            else File.Move(temp, target);
        }
    }


