using Reader.Core;
using System.Xml.Linq;

namespace Reader.Core.Tests;

internal static class BookLibraryTests
{
    private static int checks;
    private static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
    public static async Task Run(string root)
    {
        var store = new Store(root);
        string original = Path.Combine(root, "original-import.txt"); File.WriteAllText(original, "原始文件保留");
        var first = store.Add("第一本", "原始文件保留", "TXT");
        var second = store.Add("第二本", "另一本的正文与进度", "TXT");
        var third = store.Add("第三本", "第三本的正文", "TXT");
        first.Position = 2; first.Bookmark = 3; store.LastId = first.Id; store.Save();
        string firstCache = Path.Combine(root, "books", first.Id + ".txt");
        await store.SetPinnedAsync(first, true);
        Check(store.GetBooks().First() == first && store.Books.First() == third, "pinning changes view order without changing import order");
        await store.SetGroupAsync(first, "  小说 & 阅读  "); await store.SetGroupAsync(second, "小说 & 阅读");
        Check(store.GroupNames.SequenceEqual(new[] { "小说 & 阅读" }) && store.GetBooks("小说 & 阅读").First() == first, "groups trim names and retain pin order inside filter");
        Check(store.GetBooks("").Single() == third && store.GetBooks().Length == 3, "ungrouped and all-book filters are distinct");
        var restored = new Store(root); var restoredFirst = restored.Books.Single(book => book.Id == first.Id);
        Check(restoredFirst.IsPinned && restoredFirst.Group == "小说 & 阅读" && restoredFirst.Position == 2 && restoredFirst.Bookmark == 3, "restart preserves pin group progress and bookmark");
        await store.SetPinnedAsync(first, false);
        Check(store.GetBooks().First() == third && !new Store(root).Books.Single(book => book.Id == first.Id).IsPinned, "unpin restores stable import order and persists");
        await store.SetGroupAsync(third, "Test"); await store.SetGroupAsync(second, "test");
        Check(second.Group == "Test" && store.GroupNames.Count(group => group.Equals("test", StringComparison.OrdinalIgnoreCase)) == 1, "case variants reuse an existing group");
        await store.SetGroupAsync(second, "");
        Check(store.GetBooks("").Single() == second, "moving a book back to ungrouped persists");
        bool invalid = false; try { await store.SetGroupAsync(first, new string('组', 41)); } catch (ArgumentException) { invalid = true; }
        Check(invalid && first.Group == "小说 & 阅读", "invalid group name does not alter book metadata");

        // 分组是独立实体：有自己的顺序、能空着、能改名、能删。
        Check(store.GroupNames.SequenceEqual(new[] { "小说 & 阅读", "Test" }), "groups keep their own order instead of being derived from books");
        Check(await store.CreateGroupAsync("空分组") == null && store.GroupNames.Contains("空分组"), "an empty group can be created");
        Check(new Store(root).GroupNames.Contains("空分组"), "an empty group survives a restart");
        Check(await store.CreateGroupAsync("空分组") is { Length: > 0 } repeatReason, "a duplicate group name is refused with a reason");
        Check(await store.CreateGroupAsync("未分组") is { Length: > 0 } reservedReason, "a reserved group name is refused");

        await store.MoveGroupAsync("空分组", -1);
        Check(store.GroupNames.SequenceEqual(new[] { "小说 & 阅读", "空分组", "Test" }), "moving a group swaps it with its neighbour");
        Check(new Store(root).GroupNames.SequenceEqual(store.GroupNames), "group order persists across restart");
        await store.MoveGroupAsync("小说 & 阅读", -1);
        Check(store.GroupNames[0] == "小说 & 阅读", "moving the first group up is a no-op");

        await store.RenameGroupAsync("Test", "测试组");
        Check(store.GroupNames.Contains("测试组") && third.Group == "测试组" && new Store(root).GroupNames.Contains("测试组"), "renaming a group renames it on every book and persists");
        Check(await store.RenameGroupAsync("测试组", "空分组") is { Length: > 0 } clashReason, "renaming onto an existing name is refused");
        Check(store.GroupNames.Contains("测试组") && store.GroupNames.Contains("空分组"), "a refused rename changes nothing");

        await store.SetGroupAsync(new[] { first, third }, "合并后");
        Check(first.Group == "合并后" && third.Group == "合并后" && store.GetBooks("合并后").Length == 2, "batch grouping moves every target at once");

        await store.RemoveGroupAsync("Test");
        Check(!store.GroupNames.Contains("Test"), "a group can be removed");
        await store.RemoveGroupAsync("合并后");
        Check(first.Group == "" && third.Group == "" && !store.GroupNames.Contains("合并后"), "removing a group moves its books back to ungrouped");

        string backup = Path.Combine(root, "library.zip"); await store.SetPinnedAsync(first, true); await store.SetGroupAsync(first, "备份分组");
        LibraryArchive.Export(store, backup);
        var imported = new Store(Path.Combine(root, "imported")); LibraryArchive.Import(imported, backup);
        Check(imported.Books.Single(book => book.Id == first.Id).IsPinned && imported.Books.Single(book => book.Id == first.Id).Group == first.Group, "library backup transfers pin and group metadata");
        Check(imported.GroupNames.Contains("备份分组"), "library backup brings the group along so the imported book still has somewhere to live");
        var duplicate = imported.Books.Single(book => book.Id == first.Id); await imported.SetPinnedAsync(duplicate, false); await imported.SetGroupAsync(duplicate, "本机分组");
        LibraryArchive.Import(imported, backup);
        Check(!duplicate.IsPinned && duplicate.Group == "本机分组", "backup merge preserves destination organization for duplicate books");

        string temporary = Path.Combine(root, "state.xml.tmp"); Directory.CreateDirectory(temporary);
        bool failed = false; try { await store.SetPinnedAsync(second, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed && !second.IsPinned, "failed metadata persistence rolls back pin state");
        failed = false;
        try { await store.RenameGroupAsync("小说 & 阅读", "不该生效"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed && store.GroupNames.Contains("小说 & 阅读") && !store.GroupNames.Contains("不该生效"), "failed group write rolls the whole list back");
        failed = false; try { await store.RemoveBookAsync(first); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed && store.Books.Contains(first) && store.LastId == first.Id && File.ReadAllText(firstCache) == "原始文件保留", "failed delete persistence restores index active book and cached text");
        Check(Directory.GetFiles(Path.Combine(root, "books"), "*.delete-*").Length == 0, "rolled-back deletion leaves no staged file");
        Directory.Delete(temporary);
        await store.RemoveBookAsync(second);
        Check(store.LastId == first.Id && first.Position == 2 && first.Bookmark == 3, "deleting another book does not change active book or progress");
        await store.RemoveBookAsync(first);
        Check(!File.Exists(firstCache) && File.ReadAllText(original) == "原始文件保留" && store.LastId == third.Id, "delete removes only cached copy and selects a remaining book");
        Check(new Store(root).Books.All(book => book.Id != first.Id && book.Id != second.Id), "deleted books stay removed after restart");
        await store.RemoveBookAsync(third);
        Check(store.Books.Count == 0 && store.LastId == "" && new Store(root).Books.Count == 0, "deleting final book persists an empty library");
        File.WriteAllText(Path.Combine(root, "state.xml"), "damaged");
        Check(new Store(root).Books.Count == 0, "backup recovery cannot resurrect deleted cached books");

        var legacy = new Store(Path.Combine(root, "legacy")); var legacyBook = legacy.Add("旧书", "旧版兼容", "TXT"); legacy.Save();
        string statePath = Path.Combine(legacy.Root, "state.xml"); var state = XDocument.Load(statePath);
        state.Root!.Element("book")!.Attribute("pinned")!.Remove(); state.Root.Element("book")!.Attribute("group")!.Remove(); state.Save(statePath);
        var oldBook = new Store(legacy.Root).Books.Single(); Check(!oldBook.IsPinned && oldBook.Group == "", "old indexes without organization fields remain compatible");

        // 旧数据根本没有分组清单：升级后要按旧界面用的顺序（区域文化字母序）推导出来，顺序不能跳。
        var upgrade = new Store(Path.Combine(root, "upgrade"));
        var moverA = upgrade.Add("迁移甲", "分组迁移测试甲。", "TXT");
        var moverB = upgrade.Add("迁移乙", "分组迁移测试乙。", "TXT");
        await upgrade.SetGroupAsync(moverA, "b-group"); await upgrade.SetGroupAsync(moverB, "a-group");
        upgrade.Save();
        string upgradeState = Path.Combine(upgrade.Root, "state.xml");
        var upgradeDocument = XDocument.Load(upgradeState);
        upgradeDocument.Root!.Element("groups")!.Remove();
        upgradeDocument.Save(upgradeState);
        Check(new Store(upgrade.Root).GroupNames.SequenceEqual(new[] { "a-group", "b-group" }), "an index without a group list derives one in the old display order");
        string cache = Path.Combine(legacy.Root, "books", legacyBook.Id + ".txt"); string staged = cache + ".delete-" + Guid.NewGuid().ToString("N"); File.Move(cache, staged);
        Check(new Store(legacy.Root).Books.Count == 1 && File.Exists(cache) && !File.Exists(staged), "interrupted deletion before index commit restores cached book");
        staged = cache + ".delete-" + Guid.NewGuid().ToString("N"); File.Move(cache, staged);
        state.Root!.Element("book")!.Remove(); state.Root.SetAttributeValue("last", ""); state.Save(statePath);
        Check(new Store(legacy.Root).Books.Count == 0 && !File.Exists(staged), "interrupted deletion after index commit cleans staged book");
        Console.WriteLine($"SUCCESS: {checks} book library management checks passed.");
    }
}
