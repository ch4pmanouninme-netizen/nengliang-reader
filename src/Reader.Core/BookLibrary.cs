#nullable enable
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Reader.Core;

public sealed partial class Store
{
    public const int GroupNameLimit = 40;
    public string[] GroupNames => Books.Select(book => book.Group).Where(group => group.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(group => group, StringComparer.CurrentCulture).ToArray();

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
    public Task SetPinnedAsync(Book book, bool pinned) => UpdateBookAsync(book, () => book.IsPinned = pinned);
    public Task SetGroupAsync(Book book, string? group)
    {
        string name = NormalizeGroup(group);
        name = GroupNames.FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) ?? name;
        return UpdateBookAsync(book, () => book.Group = name);
    }

    private async Task UpdateBookAsync(Book book, Action update)
    {
        await writes.WaitAsync();
        bool pinned = book.IsPinned; string group = book.Group;
        try
        {
            RequireBook(book); update(); XElement snapshot = Snapshot();
            try { await Task.Run(() => WriteSnapshot(snapshot)); }
            catch { book.IsPinned = pinned; book.Group = group; throw; }
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
