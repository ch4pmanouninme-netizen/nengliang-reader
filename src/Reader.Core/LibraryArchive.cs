using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Reader.Core;

public static class LibraryArchive
{
    private const int EntryLimit = 5000;
    private const long FileLimit = 64L * 1024 * 1024;
    private const long ArchiveLimit = 256L * 1024 * 1024;

    public static void Export(Store store, string destination)
    {
        if (store.Books.Count + 1 > EntryLimit) throw new InvalidDataException("书库超过 4999 本，无法导出单个备份。");
        store.Save();
        long total = 0;
        foreach (string file in store.Books.Select(book => Path.Combine(store.Root, "books", book.Id + ".txt")).Prepend(Path.Combine(store.Root, "state.xml")))
        {
            long size = new FileInfo(file).Length;
            if (size > FileLimit || (total += size) > ArchiveLimit) throw new InvalidDataException("书库超过备份容量上限（单文件 64 MB，总计 256 MB）。");
        }
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(Path.Combine(store.Root, "state.xml"), "state.xml");
                foreach (var book in store.Books)
                    zip.CreateEntryFromFile(Path.Combine(store.Root, "books", book.Id + ".txt"), "books/" + book.Id + ".txt");
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static int Import(Store store, string source)
    {
        string staging = Path.Combine(Path.GetTempPath(), "NengliangReader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "books"));
        try
        {
            using (var zip = ZipFile.OpenRead(source))
            {
                long total = 0;
                if (zip.Entries.Count > EntryLimit) throw new InvalidDataException("备份条目过多。");
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName != "state.xml" && !Regex.IsMatch(entry.FullName, "^books/[a-f0-9]{64}\\.txt$"))
                        throw new InvalidDataException("备份包含不支持的文件路径。");
                    if ((total += entry.Length) > ArchiveLimit || entry.Length > FileLimit)
                        throw new InvalidDataException("备份展开后过大（单文件上限 64 MB，总计 256 MB）。");
                    using var input = entry.Open();
                    using var output = new FileStream(Path.Combine(staging, entry.FullName), FileMode.CreateNew);
                    byte[] buffer = new byte[81920]; int count; long actual = 0;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        actual += count;
                        if (actual > entry.Length) throw new InvalidDataException("备份条目长度异常。");
                        output.Write(buffer, 0, count);
                    }
                }
            }
            if (!File.Exists(Path.Combine(staging, "state.xml"))) throw new InvalidDataException("备份缺少书库索引。");
            var index = Importer.ReadXml(File.ReadAllText(Path.Combine(staging, "state.xml"))).Root;
            if (index == null || index.Name != "reader") throw new InvalidDataException("备份索引无效。");
            var ids = new HashSet<string>();
            foreach (var entry in index.Elements("book"))
            {
                string id = (string)entry.Attribute("id") ?? "";
                if (!Regex.IsMatch(id, "^[a-f0-9]{64}$") || !ids.Add(id) || !File.Exists(Path.Combine(staging, "books", id + ".txt")))
                    throw new InvalidDataException("备份存在缺失、重复或无效的书籍。未导入任何书籍。");
            }
            // Validate all texts before changing the destination library.
            var restored = new Store(staging);
            if (restored.RecoveryNotice.Length != 0) throw new InvalidDataException("备份中的书库索引损坏。");
            var imported = restored.Books.Select(book => (Book: book, Text: restored.TextFor(book))).ToArray();
            if (imported.Any(item => string.IsNullOrWhiteSpace(item.Text))) throw new InvalidDataException("备份包含空白书籍。");
            store.Save();
            File.Copy(Path.Combine(store.Root, "state.xml"), Path.Combine(store.Root, "state.before-import.xml"), true);
            int added = 0;
            foreach (var item in imported)
            {
                int before = store.Books.Count;
                var book = store.Add(item.Book.Title, item.Text, item.Book.Format);
                if (store.Books.Count == before) continue;
                book.Position = Store.Clamp(item.Book.Position, 0, Math.Max(0, item.Text.Length - 1));
                book.Bookmark = item.Book.Bookmark < 0 ? -1 : Store.Clamp(item.Book.Bookmark, 0, Math.Max(0, item.Text.Length - 1));
                book.Completed = item.Book.Completed; added++;
            }
            store.Save(); return added;
        }
        finally { Directory.Delete(staging, true); }
    }
}
