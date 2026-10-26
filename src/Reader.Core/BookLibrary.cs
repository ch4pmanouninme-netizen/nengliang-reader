#nullable enable
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Reader.Core;

/// <summary>
/// <see cref="Store"/> 的书库组织部分：分组清单、置顶、批量分组/置顶、删书，
/// 以及同时改动“清单 + 多本书”时的事务式回滚。
/// </summary>
public sealed partial class Store
{
    /// <summary>分组名允许的最大字符数。</summary>
    public const int GroupNameLimit = 40;
    // 分组清单本身是状态，按存储顺序返回；不再从书里现算，空分组因此能存活。
    /// <summary>按存储顺序返回全部分组名，包括空分组。</summary>
    public string[] GroupNames => groups.ToArray();
    // 按名称忽略大小写查找分组下标；不存在返回 -1。
    private int IndexOf(string name) => groups.FindIndex(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>返回书库中的书；<paramref name="group"/> 为 null 时返回全部，否则按分组名忽略大小写过滤。</summary>
    /// <param name="group">目标分组名；null 表示不过滤。</param>
    /// <returns>置顶书籍在前、其余保持原顺序的数组。</returns>
    public Book[] GetBooks(string? group = null) => Books.Where(book => group == null || string.Equals(book.Group, group, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(book => book.IsPinned).ToArray();

    /// <summary>校验并规范化分组名：去除首尾空白，空字符串表示未分组。</summary>
    /// <param name="group">用户输入的分组名，可为 null。</param>
    /// <returns>去除首尾空白后的名称。</returns>
    /// <exception cref="ArgumentException">名称超长、含控制字符或占用保留名称。</exception>
    /// <exception cref="XmlException">名称包含 XML 不允许的字符。</exception>
    public static string NormalizeGroup(string? group)
    {
        string result = (group ?? "").Trim();
        if (result.Length > GroupNameLimit || result.Any(char.IsControl) || result is "全部书籍" or "未分组")
            throw new ArgumentException($"分组名称最多 {GroupNameLimit} 个字符，不能包含换行，也不能使用“全部书籍”或“未分组”。");
        XmlConvert.VerifyXmlChars(result);
        return result;
    }

    // 读取旧数据时的容错：非法或过长的分组名降级为未分组，而不是让整个书库加载失败。
    private static string ReadGroup(string? group)
    {
        try { return NormalizeGroup(group); }
        catch (Exception ex) when (ex is ArgumentException or XmlException) { return ""; }
    }

    // As with Add(), callers own the mutable library state. UI callers await pending saves and disable edits.
    /// <summary>设置单本书的置顶状态。</summary>
    /// <param name="book">目标书籍；必须仍在书库中。</param>
    /// <param name="pinned">true 置顶，false 取消置顶。</param>
    public Task SetPinnedAsync(Book book, bool pinned) => SetPinnedAsync([book], pinned);

    /// <summary>批量设置置顶状态；重复的书只处理一次。</summary>
    /// <param name="books">目标书籍集合。</param>
    /// <param name="pinned">true 置顶，false 取消置顶。</param>
    /// <remarks>任一本书已不在书库中时，整批修改回滚并抛出 <see cref="InvalidOperationException"/>。</remarks>
    public async Task SetPinnedAsync(IEnumerable<Book> books, bool pinned)
    {
        Book[] targets = books.Distinct().ToArray();
        await MutateLibraryAsync(() =>
        {
            foreach (Book book in targets) { RequireBook(book); book.IsPinned = pinned; }
            return true;
        });
    }

    /// <summary>把单本书移动到指定分组；分组不存在时自动登记。</summary>
    /// <param name="book">目标书籍。</param>
    /// <param name="group">分组名；null 或空字符串表示移出分组。</param>
    public Task SetGroupAsync(Book book, string? group) => SetGroupAsync([book], group);

    /// <summary>批量设置分组；重复的书只处理一次，分组名统一复用已有写法。</summary>
    /// <param name="books">目标书籍集合。</param>
    /// <param name="group">分组名；null 或空字符串表示移出分组。</param>
    public async Task SetGroupAsync(IEnumerable<Book> books, string? group)
    {
        string name = NormalizeGroup(group);
        // 忽略大小写复用清单里已有的写法，避免“Test”和“test”并存。
        name = groups.FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        Book[] targets = books.Distinct().ToArray();
        await MutateLibraryAsync(() =>
        {
            foreach (Book book in targets) { RequireBook(book); book.Group = name; }
            // 新名字顺带登记成分组，等同旧版“设了分组就有这个分组”。
            if (name.Length > 0 && IndexOf(name) < 0) groups.Add(name);
            return true;
        });
    }

    // 分组管理。返回 null 表示成功，否则是可直接显示给用户的原因（名称非法或重名）。
    /// <summary>新建空分组。</summary>
    /// <param name="name">分组名，按 <see cref="NormalizeGroup"/> 校验。</param>
    /// <returns>成功返回 null，失败返回可直接展示给用户的原因。</returns>
    public Task<string?> CreateGroupAsync(string? name) => Guard(() => MutateLibraryAsync(() =>
    {
        string normalized = NormalizeGroup(name);
        if (normalized.Length == 0) throw new ArgumentException("分组名称不能为空。");
        if (IndexOf(normalized) >= 0) throw new ArgumentException($"已存在分组“{normalized}”。");
        groups.Add(normalized);
        return (string?)null;
    }));

    /// <summary>重命名分组，并同步更新所有引用该分组的书。</summary>
    /// <param name="from">原分组名。</param>
    /// <param name="to">新分组名；与已有分组重名时拒绝。</param>
    /// <returns>成功返回 null，失败返回可直接展示给用户的原因。</returns>
    public Task<string?> RenameGroupAsync(string from, string to) => Guard(() => MutateLibraryAsync(() =>
    {
        int index = IndexOf(from);
        if (index < 0) throw new ArgumentException("这个分组已不存在，请刷新后重试。");
        string normalized = NormalizeGroup(to);
        if (normalized.Length == 0) throw new ArgumentException("分组名称不能为空。");
        int existing = IndexOf(normalized);
        // 撞名拒绝而不是合并：合并会不可逆地把两个分组混在一起。
        if (existing >= 0 && existing != index) throw new ArgumentException($"已存在分组“{normalized}”。");
        string previous = groups[index];
        groups[index] = normalized;
        foreach (Book book in Books)
            if (string.Equals(book.Group, previous, StringComparison.OrdinalIgnoreCase)) book.Group = normalized;
        return (string?)null;
    }));

    /// <summary>删除分组；组内书籍改为未分组。</summary>
    /// <param name="name">要删除的分组名。</param>
    /// <returns>成功返回 null，失败返回可直接展示给用户的原因。</returns>
    public Task<string?> RemoveGroupAsync(string name) => Guard(() => MutateLibraryAsync(() =>
    {
        int index = IndexOf(name);
        if (index < 0) throw new ArgumentException("这个分组已不存在，请刷新后重试。");
        groups.RemoveAt(index);
        foreach (Book book in Books)
            if (string.Equals(book.Group, name, StringComparison.OrdinalIgnoreCase)) book.Group = "";
        return (string?)null;
    }));

    /// <summary>按 <paramref name="step"/> 的符号把分组上移或下移一位。</summary>
    /// <param name="name">要移动的分组名。</param>
    /// <param name="step">正数下移，负数上移；只使用符号。</param>
    /// <returns>正常返回 null；分组不存在或已越界时不改动任何状态。</returns>
    public Task<string?> MoveGroupAsync(string name, int step) => Guard(() => MutateLibraryAsync(() =>
    {
        int index = IndexOf(name);
        int target = index + Math.Sign(step);
        if (index < 0 || target < 0 || target >= groups.Count) return (string?)null;
        (groups[index], groups[target]) = (groups[target], groups[index]);
        return (string?)null;
    }));

    // 备份导入专用：把备份里出现过的分组按给定顺序登记到目的地清单，已存在的跳过。
    // 导入流程是“改完内存再统一 store.Save()”，所以这里不单独落盘；名字来自已校验过的备份索引。
    /// <summary>把备份中的分组名按给定顺序登记到当前清单，已存在的跳过（忽略大小写）。</summary>
    /// <param name="names">备份索引中的分组名；空名称会被忽略。</param>
    internal void RegisterGroups(IEnumerable<string> names)
    {
        foreach (string name in names.Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            if (IndexOf(name) < 0) groups.Add(name);
    }

    // 把可预期的校验失败转成界面可直接展示的文本，其余异常继续上抛。
    private static async Task<string?> Guard(Func<Task<string?>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return ex.Message; }
    }

    // 批量操作会同时改清单和多本书，逐本回滚不成立；这里整体备份“分组清单 + 每本书的分组和置顶”
    // 再统一还原 —— 这些操作只碰这几处状态。
    /// <summary>在写锁内执行一次“改内存 + 落盘”，失败时回滚分组清单与每本书的分组/置顶。</summary>
    /// <param name="mutate">修改内存状态的委托；应只改动分组或置顶字段。</param>
    /// <returns>委托的返回值。</returns>
    /// <remarks>写锁保证落盘顺序；快照在修改后生成，写入的是本次事务的最终状态。</remarks>
    private async Task<T> MutateLibraryAsync<T>(Func<T> mutate)
    {
        await writes.WaitAsync();
        // 这两份快照就是全部可回滚状态；正文与章节索引不参与分组操作。
        string[] savedGroups = groups.ToArray();
        (Book Book, string Group, bool Pinned)[] savedBooks = Books.Select(book => (book, book.Group, book.IsPinned)).ToArray();
        try
        {
            T result = mutate();
            // 快照必须在锁内、修改后生成；放进 Task.Run 可能读到其他线程改动中的字段。
            XElement snapshot = Snapshot();
            await Task.Run(() => WriteSnapshot(snapshot));
            return result;
        }
        catch
        {
            groups.Clear(); groups.AddRange(savedGroups);
            foreach ((Book book, string group, bool pinned) in savedBooks) { book.Group = group; book.IsPinned = pinned; }
            throw;
        }
        finally { writes.Release(); }
    }

    /// <summary>从书库删除一本书：先暂存正文文件、提交索引，再清理正文与章节索引。</summary>
    /// <param name="book">要删除的书籍；必须仍在书库中。</param>
    /// <returns>成功返回 true。</returns>
    /// <remarks>索引提交后，附属文件清理失败不视为删除失败；残留文件在下次启动时回收。</remarks>
    public async Task<bool> RemoveBookAsync(Book book)
    {
        await writes.WaitAsync();
        string? staged = null; string? original = null;
        int index = -1; string last = LastId; bool committed = false;
        try
        {
            index = RequireBook(book);
            original = Path.Combine(Root, "books", book.Id + ".txt");
            if (File.Exists(original))
            {
                // 先把正文改名而非直接删除：索引写入失败时还能把文件挪回去。
                staged = original + ".delete-" + Guid.NewGuid().ToString("N");
                File.Move(original, staged);
            }
            Books.RemoveAt(index);
            if (LastId == book.Id) LastId = GetBooks().FirstOrDefault()?.Id ?? "";
            XElement snapshot = Snapshot();
            await Task.Run(() => WriteSnapshot(snapshot)); committed = true;
            if (staged != null)
            {
                // 索引已经提交；清理失败只意味着下次启动还要再删一次，不能把删除报成失败。
                try { File.Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            // 章节索引是附属文件；删除失败不影响书籍移除，下次同名导入会重新生成。
            try { File.Delete(Path.Combine(Root, "books", book.Id + ".chapters.tsv")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return true;
        }
        catch
        {
            // 只有索引尚未提交才回滚；已提交时改动已经生效，不能再把书插回内存。
            if (!committed && index >= 0)
            {
                if (!Books.Contains(book)) Books.Insert(Math.Min(index, Books.Count), book);
                LastId = last;
                if (staged != null && File.Exists(staged) && original != null) File.Move(staged, original);
            }
            throw;
        }
        finally { writes.Release(); }
    }

    // 书籍对象必须仍属于当前书库，防止陈旧界面引用误改已删除的书。
    private int RequireBook(Book book)
    {
        int index = Books.IndexOf(book);
        if (index < 0 || !IsBookId(book.Id)) throw new InvalidOperationException("这本书已不在当前书库中，请刷新后重试。");
        return index;
    }

    // 删除操作先把正文改名为 *.delete-* 再写索引；启动时据此判断该文件应恢复还是丢弃。
    private void RecoverPendingRemovals()
    {
        string[] pending = Directory.GetFiles(Path.Combine(Root, "books"), "*.delete-*");
        if (pending.Length == 0) return;
        HashSet<string>? indexed = null;
        // 优先读主索引，损坏时退回 .bak；两者都读不到时 indexed 为 null，表示无法判断。
        foreach (string index in new[] { Path.Combine(Root, "state.xml"), Path.Combine(Root, "state.xml.bak") })
        {
            try
            {
                if (!File.Exists(index)) continue;
                XElement? root = Importer.ReadXml(File.ReadAllText(index)).Root;
                if (root?.Name != "reader") continue;
                indexed = root.Elements("book").Select(book => (string?)book.Attribute("id") ?? "").ToHashSet(); break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { }
        }
        foreach (string staged in pending)
        {
            Match match = Regex.Match(Path.GetFileName(staged), "^([a-f0-9]{64})\\.txt\\.delete-[a-f0-9]{32}$");
            if (!match.Success) continue;
            try
            {
                string id = match.Groups[1].Value, original = Path.Combine(Root, "books", id + ".txt");
                // 索引不可用或仍引用、且原文件缺失：删除未提交，恢复文件；否则说明删除已生效，清掉临时文件。
                if ((indexed == null || indexed.Contains(id)) && !File.Exists(original)) File.Move(staged, original);
                else File.Delete(staged);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecoveryNotice = "部分书籍删除临时文件未能清理，请检查书库目录权限后重启。";
            }
        }
    }
}
