using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Reader.Core;

/// <summary>
/// 书库备份的导出与导入：把 state.xml 和各书正文/章节索引打包成 ZIP，
/// 导入时先完整解压并校验到临时目录，再逐本合并进现有书库。
/// 条目数、单文件大小和总解压大小都有限制，防止恶意备份耗尽磁盘。
/// </summary>
public static class LibraryArchive
{
    // 条目数含 state.xml 本身；单文件上限 64 MB，整个备份展开后上限 256 MB。
    private const int EntryLimit = 5000;
    private const long FileLimit = 64L * 1024 * 1024;
    private const long ArchiveLimit = 256L * 1024 * 1024;

    /// <summary>把当前书库导出为单个 ZIP 备份；同名目标文件会被原子替换。</summary>
    /// <param name="store">要导出的书库；导出前会先保存索引。</param>
    /// <param name="destination">备份文件路径。</param>
    /// <exception cref="InvalidDataException">条目数或体积超过备份上限。</exception>
    public static void Export(Store store, string destination)
    {
        // 先落盘，保证 ZIP 中的 state.xml 与内存状态一致。
        store.Save();
        // 每本书最多两个条目：正文 + 可选章节索引（EPUB 目录 sidecar）。
        var files = new List<(string Source, string Entry)>
        {
            (Path.Combine(store.Root, "state.xml"), "state.xml"),
        };
        foreach (Book book in store.Books)
        {
            files.Add((Path.Combine(store.Root, "books", book.Id + ".txt"), "books/" + book.Id + ".txt"));
            string chapters = Path.Combine(store.Root, "books", book.Id + ".chapters.tsv");
            if (File.Exists(chapters)) files.Add((chapters, "books/" + book.Id + ".chapters.tsv"));
        }
        // 条目数包含 state.xml，因此可导出的书籍数上限是 EntryLimit - 1。
        if (files.Count > EntryLimit) throw new InvalidDataException($"书库超过 {EntryLimit - 1} 个文件，无法导出单个备份。");
        long total = 0;
        // 打包前先按文件系统大小预检，避免写到一半才发现超限。
        foreach (var file in files)
        {
            long size = new FileInfo(file.Source).Length;
            if (size > FileLimit || (total += size) > ArchiveLimit) throw new InvalidDataException("书库超过备份容量上限（单文件 64 MB，总计 256 MB）。");
        }

        // 先写同目录临时文件再替换，导出失败不会留下半个备份。
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in files) zip.CreateEntryFromFile(file.Source, file.Entry);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>从 ZIP 备份合并书籍到现有书库；重复正文的书会被跳过。</summary>
    /// <param name="store">目标书库。</param>
    /// <param name="source">备份 ZIP 路径。</param>
    /// <returns>实际新增的书籍数量；同正文已存在的书不计入。</returns>
    /// <remarks>
    /// 先解压到临时目录并完成全部校验后才改动目标书库；改动前会保存 state.before-import.xml。
    /// 备份中的分组只导入目的地仍有书引用的部分。
    /// </remarks>
    /// <exception cref="InvalidDataException">条目路径、大小、索引或正文校验失败。</exception>
    public static int Import(Store store, string source)
    {
        // 先解压到独立临时目录，校验通过前不碰目标书库。
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
                    // 只接受固定布局，防止 ../ 路径穿越把文件写到临时目录之外。
                    if (entry.FullName != "state.xml" && !Regex.IsMatch(entry.FullName, "^books/[a-f0-9]{64}\\.(txt|chapters\\.tsv)$"))
                        throw new InvalidDataException("备份包含不支持的文件路径。");
                    // 同时限制单条声明大小与累计大小；下面还会按实际读到的字节复核。
                    if ((total += entry.Length) > ArchiveLimit || entry.Length > FileLimit)
                        throw new InvalidDataException("备份展开后过大（单文件上限 64 MB，总计 256 MB）。");
                    // 章节索引只应是几 KB～几 MB；超限直接拒绝，避免把超大文件落盘。
                    if (entry.FullName.EndsWith(".chapters.tsv", StringComparison.Ordinal) && entry.Length > 4L * 1024 * 1024)
                        throw new InvalidDataException("备份中的章节索引过大。");
                    using var input = entry.Open();
                    using var output = new FileStream(Path.Combine(staging, entry.FullName), FileMode.CreateNew);
                    byte[] buffer = new byte[81920]; int count; long actual = 0;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        actual += count;
                        // 目录声明值可能被伪造：实际解压字节超过声明长度就立即中止。
                        if (actual > entry.Length) throw new InvalidDataException("备份条目长度异常。");
                        output.Write(buffer, 0, count);
                    }
                }
            }
            // state.xml 是备份的入口，缺失则无法判断应恢复哪些书。
            if (!File.Exists(Path.Combine(staging, "state.xml"))) throw new InvalidDataException("备份缺少书库索引。");
            var index = Importer.ReadXml(File.ReadAllText(Path.Combine(staging, "state.xml"))).Root;
            if (index == null || index.Name != "reader") throw new InvalidDataException("备份索引无效。");
            var ids = new HashSet<string>();
            // 先在临时目录验证每本书的 ID 合法、不重复且正文存在，再开始合并。
            foreach (var entry in index.Elements("book"))
            {
                string id = (string)entry.Attribute("id") ?? "";
                if (!Store.IsBookId(id) || !ids.Add(id) || !File.Exists(Path.Combine(staging, "books", id + ".txt")))
                    throw new InvalidDataException("备份存在缺失、重复或无效的书籍。未导入任何书籍。");
            }
            // Validate all texts before changing the destination library.
            var restored = new Store(staging);
            if (restored.RecoveryNotice.Length != 0) throw new InvalidDataException("备份中的书库索引损坏。");
            // 先逐本校验，再逐本合并；同一时间只保留一本书的正文，避免大备份一次性驻留内存。
            foreach (var backupBook in restored.Books)
                if (string.IsNullOrWhiteSpace(restored.TextFor(backupBook))) throw new InvalidDataException("备份包含空白书籍。");
            // 改动目标书库前先保存当前状态，并把索引另存为 state.before-import.xml，便于人工回滚。
            store.Save();
            File.Copy(Path.Combine(store.Root, "state.xml"), Path.Combine(store.Root, "state.before-import.xml"), true);
            int added = 0;
            foreach (var backupBook in restored.Books)
            {
                string text = restored.TextFor(backupBook);
                int before = store.Books.Count;
                var book = store.Add(backupBook.Title, text, backupBook.Format, restored.ChaptersFor(backupBook));
                // Add 遇到相同正文会返回已有书且数量不变；此时保留目的地原有的组织信息。
                if (store.Books.Count == before) continue;
                // 备份里的位置可能超出当前正文长度（旧版本或手工修改），入库前夹到合法范围。
                book.Position = Store.Clamp(backupBook.Position, 0, Math.Max(0, text.Length - 1));
                book.Bookmark = backupBook.Bookmark < 0 ? -1 : Store.Clamp(backupBook.Bookmark, 0, Math.Max(0, text.Length - 1));
                book.Completed = backupBook.Completed; book.IsPinned = backupBook.IsPinned; book.Group = backupBook.Group; added++;
            }
            // 分组清单也要跟着合并。只带入至少有一本书引用到的分组：空分组不跨库导入，
            // 与上面“逐本合并、重复书保留目的地组织”的语义保持一致。顺序沿用备份里的清单顺序。
            // 只登记目的地书库里确实有书引用的分组；备份里的重复书已被跳过，不能再算数。
            store.RegisterGroups(restored.GroupNames.Where(name =>
                store.Books.Any(book => string.Equals(book.Group, name, StringComparison.OrdinalIgnoreCase))));
            // 所有书合并完后统一落盘，避免其他实例读到只导入了一半的状态。
            store.Save(); return added;
        }
        finally { Directory.Delete(staging, true); }
    }
}
