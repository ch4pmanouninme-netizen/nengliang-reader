using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Reader.Core;
using Reader.Desktop;
using System.Reflection;
using System.Text.Json;
// 官方主题契约：27 个主题逐一应用并检查窗口、按钮、输入框和标题的画刷，
// 同时验证偏好持久化、预设会清空显式阅读主题、未知 ID 与旧索引缺字段的兼容。
class Test
{
    [STAThread]
    static int Main()
    {
        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        // 反射调用内部 UiThemes.Apply，避免为了测试把主题 API 公开。
        var themes = typeof(App).Assembly.GetType("Reader.Desktop.UiThemes")!;
        var apply = themes.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var stream = typeof(App).Assembly.GetManifestResourceStream("Reader.Desktop.OfficialThemes.json")!;
        var catalog = JsonSerializer.Deserialize<JsonElement[]>(stream)!;
        // 目录数量与 JetBrains 分组数量是发布契约，少了就说明资源没打进程序集。
        if (catalog.Length != 27 || catalog.Count(x => x.GetProperty("Group").GetString() == "JetBrains") != 8)
            throw new Exception("Incomplete official catalog");
        // 用最小控件树覆盖窗口背景、按钮、输入框、标题栏四类画刷。
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
            // 每个主题都先保存再重新加载，确保主题 ID 真的落盘而不只是内存状态。
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
            // 已知旧 ID 与未知 ID 都必须落到明确的回退主题。
            foreach (var pair in new[] { ("vscode-2026", "#121314"), ("jetbrains-islands-dark", "#191A1C"), ("jetbrains-dark", "#1E1F22"), ("darcula", "#2B2B2B"), ("vscode", "#1E1E1E"), ("light", "#F5F7FA"), ("unknown-future-id", "#F5F7FA") })
            {
                apply.Invoke(null, new object[] { new Preferences { UiTheme = pair.Item1 } }); window.UpdateLayout();
                Check(window.Background, pair.Item2, pair.Item1);
            }
            // 选择预设主题会清空显式阅读主题，让预设重新生效。
            store.Pref.UsePreset(5);
            if (store.Pref.ReadingThemeId != "") throw new Exception("Preset must clear explicit reading theme");
            // 旧版 preferences 没有 readingThemeId 时按空值兼容。
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
