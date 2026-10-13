using System.Text.Json;
using Avalonia.Threading;
using Reader.Platform.Windows;
using Reader.Platform.Mac;

namespace Reader.Desktop;

internal sealed class Hotkeys : IDisposable
{
    private readonly string path;
    private readonly Action<int> action;
    private IDisposable? native;
    private List<int> failed = [];
    public string[] Keys { get; private set; } = ["H", "Right", "Left", "M", "Space"];
    private static readonly string[] Labels = ["隐藏", "下一页", "上一页", "穿透", "自动阅读"];
    // Hardware keycodes used by macOS's registered-hotkey API.
    private static readonly Dictionary<string, uint> MacKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"]=0,["S"]=1,["D"]=2,["F"]=3,["H"]=4,["G"]=5,["Z"]=6,["X"]=7,["C"]=8,["V"]=9,["B"]=11,
        ["Q"]=12,["W"]=13,["E"]=14,["R"]=15,["Y"]=16,["T"]=17,["O"]=31,["U"]=32,["I"]=34,["P"]=35,
        ["L"]=37,["J"]=38,["K"]=40,["N"]=45,["M"]=46,["Space"]=49,["Left"]=123,["Right"]=124,
        ["F1"]=122,["F2"]=120,["F3"]=99,["F4"]=118,["F5"]=96,["F6"]=97,["F7"]=98,["F8"]=100,
        ["F9"]=101,["F10"]=109,["F11"]=103,["F12"]=111
    };
    public string Caption => (OperatingSystem.IsMacOS() ? "Control+Option+" : "Ctrl+Alt+") +
        string.Join(" · ", Keys.Select((key, index) => key + " " + Labels[index] + (failed.Contains(index + 1) ? "（注册失败）" : "")));

    public Hotkeys(string root, Action<int> action)
    {
        path = Path.Combine(root, "hotkeys.json"); this.action = action;
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
                if (loaded != null) { Validate(loaded); Keys = loaded; }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException) { /* Keep defaults for an invalid optional preference file. */ }
        }
        Register();
    }

    public void Configure(string[] keys)
    {
        Validate(keys);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(keys)); File.Move(temp, path, true);
        Keys = keys; native?.Dispose(); native = null; Register();
    }
    private static void Validate(string[] keys)
    {
        if (keys.Length != 5 || keys.Any(key => !MacKeys.ContainsKey(key)) || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5)
            throw new ArgumentException("请输入 5 个互不相同的有效按键。");
    }
    private void Register()
    {
        Action<int> dispatch = id => Dispatcher.UIThread.Post(() => action(id));
        if (OperatingSystem.IsWindows())
        {
            var integration = new WindowsIntegration(Keys.Select((key, index) => (index + 1, WindowsKey(key))).ToArray(), dispatch);
            native = integration; failed = integration.Failed;
        }
        else if (OperatingSystem.IsMacOS())
        {
            var integration = new MacIntegration(Keys.Select((key, index) => (index + 1, MacKeys[key])).ToArray(), dispatch);
            native = integration; failed = integration.Failed;
        }
        else failed = [1, 2, 3, 4, 5];
    }
    private static uint WindowsKey(string key)
    {
        string value = key.ToUpperInvariant();
        if (value.Length == 1) return value[0];
        if (value.StartsWith('F')) return 111 + uint.Parse(value[1..]);
        return value switch { "LEFT" => 0x25, "RIGHT" => 0x27, _ => 0x20 };
    }
    public void Dispose() => native?.Dispose();
}
