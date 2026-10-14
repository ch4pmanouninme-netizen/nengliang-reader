#nullable enable

namespace Reader.Core;

public static class DataPaths
{
    public static string Default => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "NengliangReader")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NengliangReader");

    // An installed package kept data/ beside the executable; a source checkout keeps it at the root.
    // Once the code moves to its own directory neither is the running assembly's folder, so the
    // entry shell passes its own root in and we prefer whichever layout actually holds a library.
    // A null legacyRoot keeps the pre-refactor behaviour: inspect the running assembly's directory.
    public static string ResolveLegacyRoot(string? legacyRoot, string baseDirectory)
    {
        string root = legacyRoot ?? baseDirectory;
        string nested = Path.Combine(root, "data");
        return File.Exists(Path.Combine(nested, "state.xml")) ? nested : root;
    }

    // Copy only; never overwrite a newer library or remove the original.
    public static void MigrateLegacy(string source, string destination)
    {
        string state = Path.Combine(source, "state.xml");
        if (!File.Exists(state) || File.Exists(Path.Combine(destination, "state.xml"))) return;
        Directory.CreateDirectory(Path.Combine(destination, "books"));
        string books = Path.Combine(source, "books");
        if (Directory.Exists(books))
            foreach (string file in Directory.EnumerateFiles(books, "*.txt"))
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), "^[a-f0-9]{64}\\.txt$"))
                    File.Copy(file, Path.Combine(destination, "books", Path.GetFileName(file)), true);
        File.Copy(state, Path.Combine(destination, "state.legacy-backup.xml"), true);
        if (File.Exists(state + ".bak")) File.Copy(state + ".bak", Path.Combine(destination, "state.xml.bak"), true);
        File.Copy(state, Path.Combine(destination, "state.xml"), false);
    }
}
