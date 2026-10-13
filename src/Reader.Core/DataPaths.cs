namespace Reader.Core;

public static class DataPaths
{
    public static string Default => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "NengliangReader")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NengliangReader");

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
