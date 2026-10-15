using System.Text.Json;
using Avalonia.Input;
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
    public static readonly string[] Defaults = ["H", "Right", "Left", "M", "Space"];
    public static readonly string[] Functions = ["隐藏", "下一页", "上一页", "穿透", "自动阅读"];
    // 空串表示该功能不绑定全局快捷键，不会注册，也不会在书库底部显示组合键。
    public string[] Keys { get; private set; } = [.. Defaults];
    // Hardware keycodes used by macOS's registered-hotkey API.
    private static readonly Dictionary<string, uint> MacKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"]=0,["S"]=1,["D"]=2,["F"]=3,["H"]=4,["G"]=5,["Z"]=6,["X"]=7,["C"]=8,["V"]=9,["B"]=11,
        ["Q"]=12,["W"]=13,["E"]=14,["R"]=15,["Y"]=16,["T"]=17,["O"]=31,["U"]=32,["I"]=34,["P"]=35,
        ["L"]=37,["J"]=38,["K"]=40,["N"]=45,["M"]=46,["Space"]=49,["Left"]=123,["Right"]=124,
        ["F1"]=122,["F2"]=120,["F3"]=99,["F4"]=118,["F5"]=96,["F6"]=97,["F7"]=98,["F8"]=100,
        ["F9"]=101,["F10"]=109,["F11"]=103,["F12"]=111
    };
    public static string Modifier => OperatingSystem.IsMacOS() ? "Control + Option" : "Ctrl + Alt";
    public int FailedCount => failed.Count;
    // 每个功能是否注册失败，供快捷键设置窗口在三行内标注。
    public bool[] FailedRows => Enumerable.Range(0, Keys.Length).Select(index => failed.Contains(index + 1)).ToArray();
    private bool IsFailed(int index) => failed.Contains(index + 1);
    // 把用户输入归一化成 hotkeys.json 里的规范写法（大小写不敏感），无效返回 null。
    public static string? Canonical(string key)
    {
        string trimmed = key.Trim();
        foreach (string name in MacKeys.Keys) if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }
    // 空串表示不绑定；无效键名返回 null。
    public static string? Normalize(string key)
    {
        string trimmed = key.Trim();
        return trimmed.Length == 0 ? "" : Canonical(trimmed);
    }
    // 把按下的物理按键映射成键名；不在支持范围内的键返回 null。
    public static string? NameOf(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.F1 && key <= Key.F12) return key.ToString();
        return key switch { Key.Left => "Left", Key.Right => "Right", Key.Space => "Space", _ => null };
    }

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
    // 录入期间必须先停掉全局热键：这些组合是系统级注册的，会抢先于窗口收到按键，
    // 于是按 Ctrl+Alt+H 只会触发原来的功能，根本进不了录入框。
    public void Suspend() { native?.Dispose(); native = null; }
    public void Resume() { native?.Dispose(); native = null; Register(); }
    // 空位允许：留空即不绑定，其余按键仍然必须有效且互不相同。
    private static void Validate(string[] keys)
    {
        string?[] normalized = keys.Select(Normalize).ToArray();
        string[] bound = normalized.Where(key => !string.IsNullOrEmpty(key)).Select(key => key!).ToArray();
        if (keys.Length != Defaults.Length || normalized.Any(key => key == null)
            || bound.Distinct(StringComparer.OrdinalIgnoreCase).Count() != bound.Length)
            throw new ArgumentException("请输入互不相同的有效按键；留空表示不绑定。");
    }
    private void Register()
    {
        Action<int> dispatch = id => Dispatcher.UIThread.Post(() => action(id));
        int[] bound = Enumerable.Range(0, Keys.Length).Where(index => Keys[index].Length > 0).ToArray();
        if (OperatingSystem.IsWindows())
        {
            var integration = new WindowsIntegration(bound.Select(index => (index + 1, WindowsKey(Keys[index]))).ToArray(), dispatch);
            native = integration; failed = integration.Failed;
        }
        else if (OperatingSystem.IsMacOS())
        {
            var integration = new MacIntegration(bound.Select(index => (index + 1, MacKeys[Keys[index]])).ToArray(), dispatch);
            native = integration; failed = integration.Failed;
        }
        else failed = bound.Select(index => index + 1).ToList();
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
