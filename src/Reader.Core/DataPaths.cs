#nullable enable

namespace Reader.Core;

/// <summary>
/// 数据目录工具：确定默认书库位置、定位旧版数据根目录，并在不覆盖现有书库的前提下迁移旧数据。
/// </summary>
public static class DataPaths
{
    /// <summary>
    /// 默认数据目录：macOS 为 ~/Library/Application Support/NengliangReader，
    /// 其他平台为 LocalApplicationData（Windows 下即 %LOCALAPPDATA%）/NengliangReader。
    /// </summary>
    public static string Default => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "NengliangReader")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NengliangReader");

    // An installed package kept data/ beside the executable; a source checkout keeps it at the root.
    // Once the code moves to its own directory neither is the running assembly's folder, so the
    // entry shell passes its own root in and we prefer whichever layout actually holds a library.
    // A null legacyRoot keeps the pre-refactor behaviour: inspect the running assembly's directory.
    /// <summary>
    /// 定位旧版数据根：优先使用调用方传入的根，其次使用程序目录；
    /// 若根下存在 data/state.xml 则返回 data，否则返回根本身。
    /// </summary>
    /// <param name="legacyRoot">入口程序传入的安装/源码根目录；null 表示改用 <paramref name="baseDirectory"/>。</param>
    /// <param name="baseDirectory">程序集所在目录，作为 legacyRoot 为空时的回退。</param>
    /// <returns>实际包含书库的目录；目录不存在时返回一个待创建的位置。</returns>
    public static string ResolveLegacyRoot(string? legacyRoot, string baseDirectory)
    {
        string root = legacyRoot ?? baseDirectory;
        string nested = Path.Combine(root, "data");
        // 以 state.xml 是否存在判断实际布局，避免把空目录误当成书库。
        return File.Exists(Path.Combine(nested, "state.xml")) ? nested : root;
    }

    // Copy only; never overwrite a newer library or remove the original.
    /// <summary>
    /// 把旧数据目录复制到新目录：仅当旧目录有 state.xml 且新目录还没有时执行，原目录保持不变。
    /// </summary>
    /// <param name="source">旧数据根目录。</param>
    /// <param name="destination">新数据目录；不存在时创建 books 子目录。</param>
    public static void MigrateLegacy(string source, string destination)
    {
        string state = Path.Combine(source, "state.xml");
        if (!File.Exists(state) || File.Exists(Path.Combine(destination, "state.xml"))) return;
        Directory.CreateDirectory(Path.Combine(destination, "books"));
        string books = Path.Combine(source, "books");
        // 只复制合法书籍 ID 的正文，避免把旧目录里的临时文件或无关文件带进新库。
        if (Directory.Exists(books))
            foreach (string file in Directory.EnumerateFiles(books, "*.txt"))
                if (Store.IsBookId(Path.GetFileNameWithoutExtension(file)))
                    File.Copy(file, Path.Combine(destination, "books", Path.GetFileName(file)), true);
        // 先备份旧索引，最后才写 state.xml：目标出现 state.xml 即代表迁移完成，中途失败可以重试。
        File.Copy(state, Path.Combine(destination, "state.legacy-backup.xml"), true);
        if (File.Exists(state + ".bak")) File.Copy(state + ".bak", Path.Combine(destination, "state.xml.bak"), true);
        File.Copy(state, Path.Combine(destination, "state.xml"), false);
    }
}
