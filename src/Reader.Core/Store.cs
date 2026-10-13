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
    public sealed class Store
    {
        private readonly SemaphoreSlim writes = new(1, 1);
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
                Pref.HasPosition = (string)p.Attribute("hasPosition") == "true" || Pref.X != -1 || Pref.Y != -1;
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
                Pref.ChatHost = (string)p.Attribute("chatHost") ?? "";
                Pref.ChatNick = (string)p.Attribute("chatNick") ?? "";
                Pref.ChatPort = Clamp(Number(p, "chatPort", 38417), 1, 65535);
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
                new XElement("preferences", new XAttribute("width", Pref.Width), new XAttribute("x", Pref.X), new XAttribute("y", Pref.Y), new XAttribute("hasPosition", Pref.HasPosition),
                    new XAttribute("seconds", Pref.Seconds), new XAttribute("lineCount", Pref.LineCount), new XAttribute("opacity", Pref.Opacity), new XAttribute("theme", Pref.Theme),
                    new XAttribute("fontSize", (int)Pref.FontSize), new XAttribute("fontName", Pref.FontName), new XAttribute("topMost", Pref.TopMost ? "true" : "false"),
                    new XAttribute("chatHost", Pref.ChatHost), new XAttribute("chatNick", Pref.ChatNick), new XAttribute("chatPort", Pref.ChatPort),
                    new XAttribute("stealth", Pref.Stealth ? "true" : "false"), new XAttribute("clickThrough", Pref.ClickThrough ? "true" : "false"), new XAttribute("uiDark", Pref.UiDark ? "true" : "false")));
            foreach (Book b in Books) root.Add(new XElement("book", new XAttribute("id", b.Id), new XAttribute("title", b.Title), new XAttribute("format", b.Format), new XAttribute("position", b.Position), new XAttribute("bookmark", b.Bookmark), new XAttribute("length", b.Length), new XAttribute("completed", b.Completed ? "true" : "false")));
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


