using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Reader.Core;
using Reader.Desktop;
using System.Reflection;
using System.Text.Json;
class Test
{
    [STAThread]
    static int Main()
    {
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        var themes = typeof(App).Assembly.GetType("Reader.Desktop.UiThemes")!;
        var apply = themes.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var stream = typeof(App).Assembly.GetManifestResourceStream("Reader.Desktop.OfficialThemes.json")!;
        var catalog = JsonSerializer.Deserialize<JsonElement[]>(stream)!;
        if (catalog.Length != 27 || catalog.Count(x => x.GetProperty("Group").GetString() == "JetBrains") != 8)
            throw new Exception("Incomplete official catalog");
        var button = new Button { Content = "测试" };
        var input = new TextBox { Text = "测试输入" };
        var heading = new TextBlock { Text = "Header" };
        var header = (Border)themes.GetMethod("Header", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { heading })!;
        var window = new Window { Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false, Content = new StackPanel { Children = { header, button, input } } };
        string data = Path.Combine(Path.GetTempPath(), "reader-theme-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            window.Show();
            var store = new Store(data);
            foreach (var theme in catalog)
            {
                string Value(string key) => theme.GetProperty(key).GetString()!;
                store.Pref.UiTheme = Value("Id"); store.Pref.ReadingThemeId = Value("Id");
                store.Save(); var loaded = new Store(data);
                if (loaded.Pref.UiTheme != Value("Id") || loaded.Pref.ReadingThemeId != Value("Id")) throw new Exception("Persistence failed");
                apply.Invoke(null, new object[] { loaded.Pref }); window.UpdateLayout();
                Check(window.Background, Value("Background"), Value("Id") + " window");
                Check(button.Background, Value("Button"), Value("Id") + " button");
                Check(input.Background, Value("Surface"), Value("Id") + " input");
                Check(input.Foreground, Value("InputForeground"), Value("Id") + " input text");
                Check(header.Background, Value("Header"), Value("Id") + " header");
                Check(heading.Foreground, Value("HeaderForeground"), Value("Id") + " header text");
            }
            foreach (var pair in new[] { ("vscode-2026", "#121314"), ("jetbrains-islands-dark", "#191A1C"), ("jetbrains-dark", "#1E1F22"), ("darcula", "#2B2B2B"), ("vscode", "#1E1E1E"), ("light", "#F5F7FA"), ("unknown-future-id", "#F5F7FA") })
            {
                apply.Invoke(null, new object[] { new Preferences { UiTheme = pair.Item1 } }); window.UpdateLayout();
                Check(window.Background, pair.Item2, pair.Item1);
            }
            store.Pref.UsePreset(5);
            if (store.Pref.ReadingThemeId != "") throw new Exception("Preset must clear explicit reading theme");
            var legacy = System.Xml.Linq.XDocument.Load(Path.Combine(data, "state.xml"));
            legacy.Root!.Element("preferences")!.Attribute("readingThemeId")!.Remove(); legacy.Save(Path.Combine(data, "state.xml"));
            if (new Store(data).Pref.ReadingThemeId != "") throw new Exception("Legacy compatibility failed");
            Console.WriteLine("PASS: 27 official themes update live window, button, input and header brushes; preferences persist; presets and unknown/legacy IDs remain compatible.");
            return 0;
        }
        finally
        {
            window.Close();
            try { Directory.Delete(data, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    static void Check(IBrush? brush, string expected, string name)
    {
        if (brush is not ISolidColorBrush solid || solid.Color != Color.Parse(expected)) throw new Exception(name + " expected " + expected + ", actual " + brush);
    }
}
