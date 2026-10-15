#nullable enable
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Reader.Core;

public sealed partial class Store
{
    public const int GroupNameLimit = 40;
    // 分组清单本身是状态，按存储顺序返回；不再从书里现算，空分组因此能存活。
    public string[] GroupNames => groups.ToArray();
    private int IndexOf(string name) => groups.FindIndex(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    public Book[] GetBooks(string? group = null) => Books.Where(book => group == null || string.Equals(book.Group, group, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(book => book.IsPinned).ToArray();

    public static string NormalizeGroup(string? group)
    {
        string result = (group ?? "").Trim();
        if (result.Length > GroupNameLimit || result.Any(char.IsControl) || result is "全部书籍" or "未分组")
            throw new ArgumentException($"分组名称最多 {GroupNameLimit} 个字符，不能包含换行，也不能使用“全部书籍”或“未分组”。");
        XmlConvert.VerifyXmlChars(result);
        return result;
    }

    private static string ReadGroup(string? group)
    {
        try { return NormalizeGroup(group); }
        catch (Exception ex) when (ex is ArgumentException or XmlException) { return ""; }
    }

    // As with Add(), callers own the mutable library state. UI callers await pending saves and disable edits.
    public Task SetPinnedAsync(Book book, bool pinned) => SetPinnedAsync([book], pinned);

    public async Task SetPinnedAsync(IEnumerable<Book> books, bool pinned)
    {
        Book[] targets = books.Distinct().ToArray();
        await MutateLibraryAsync(() =>
        {
            foreach (Book book in targets) { RequireBook(book); book.IsPinned = pinned; }
            return true;
        });
    }

    public Task SetGroupAsync(Book book, string? group) => SetGroupAsync([book], group);

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
    public Task<string?> CreateGroupAsync(string? name) => Guard(() => MutateLibraryAsync(() =>
    {
        string normalized = NormalizeGroup(name);
        if (IndexOf(normalized) >= 0) throw new ArgumentException($"已存在分组“{normalized}”。");
        groups.Add(normalized);
        return (string?)null;
    }));

    public Task<string?> RenameGroupAsync(string from, string to) => Guard(() => MutateLibraryAsync(() =>
    {
        int index = IndexOf(from);
        if (index < 0) throw new ArgumentException("这个分组已不存在，请刷新后重试。");
        string normalized = NormalizeGroup(to);
        int existing = IndexOf(normalized);
        // 撞名拒绝而不是合并：合并会不可逆地把两个分组混在一起。
        if (existing >= 0 && existing != index) throw new ArgumentException($"已存在分组“{normalized}”。");
        string previous = groups[index];
        groups[index] = normalized;
        foreach (Book book in Books)
            if (string.Equals(book.Group, previous, StringComparison.OrdinalIgnoreCase)) book.Group = normalized;
        return (string?)null;
    }));

    public Task<string?> RemoveGroupAsync(string name) => Guard(() => MutateLibraryAsync(() =>
    {
        int index = IndexOf(name);
        if (index < 0) throw new ArgumentException("这个分组已不存在，请刷新后重试。");
        groups.RemoveAt(index);
        foreach (Book book in Books)
            if (string.Equals(book.Group, name, StringComparison.OrdinalIgnoreCase)) book.Group = "";
        return (string?)null;
    }));

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
    internal void RegisterGroups(IEnumerable<string> names)
    {
        foreach (string name in names.Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            if (IndexOf(name) < 0) groups.Add(name);
    }

    private static async Task<string?> Guard(Func<Task<string?>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return ex.Message; }
    }

    // 批量操作会同时改清单和多本书，逐本回滚不成立；这里整体备份“分组清单 + 每本书的分组和置顶”
    // 再统一还原 —— 这些操作只碰这几处状态。
    private async Task<T> MutateLibraryAsync<T>(Func<T> mutate)
    {
        await writes.WaitAsync();
        string[] savedGroups = groups.ToArray();
        (Book Book, string Group, bool Pinned)[] savedBooks = Books.Select(book => (book, book.Group, book.IsPinned)).ToArray();
        try
        {
            T result = mutate();
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
                staged = original + ".delete-" + Guid.NewGuid().ToString("N");
                File.Move(original, staged);
            }
            Books.RemoveAt(index);
            if (LastId == book.Id) LastId = GetBooks().FirstOrDefault()?.Id ?? "";
            XElement snapshot = Snapshot();
            await Task.Run(() => WriteSnapshot(snapshot)); committed = true;
            if (staged != null)
            {
                try { File.Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            }
            return true;
        }
        catch
        {
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

    private int RequireBook(Book book)
    {
        int index = Books.IndexOf(book);
        if (index < 0 || !Regex.IsMatch(book.Id, "^[a-f0-9]{64}$")) throw new InvalidOperationException("这本书已不在当前书库中，请刷新后重试。");
        return index;
    }

    private void RecoverPendingRemovals()
    {
        string[] pending = Directory.GetFiles(Path.Combine(Root, "books"), "*.delete-*");
        if (pending.Length == 0) return;
        HashSet<string>? indexed = null;
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
