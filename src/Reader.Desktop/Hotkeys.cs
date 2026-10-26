using System.Text.Json;
using Avalonia.Input;
using Avalonia.Threading;
using Reader.Platform.Windows;
using Reader.Platform.Mac;

namespace Reader.Desktop;

/// <summary>
/// 全局快捷键管理：读写 hotkeys.json、按平台注册系统热键，并把回调调度回 UI 线程。
/// </summary>
/// <remarks>空字符串表示该功能不绑定；无效配置回退默认值。实例持有原生注册句柄，Dispose 时释放。</remarks>
internal sealed class Hotkeys : IDisposable
{
    private readonly string path;
    private readonly Action<int> action;
    // 平台原生注册对象；Suspend/Resume 和 Dispose 都会替换或释放它。
    private IDisposable? native;
    // 注册失败的功能 id（从 1 开始）。
    private List<int> failed = [];
    /// <summary>五个功能的默认按键。</summary>
    public static readonly string[] Defaults = ["H", "Right", "Left", "M", "Space"];
    /// <summary>功能名称，顺序与 <see cref="Keys"/> 和默认按键一致。</summary>
    public static readonly string[] Functions = ["隐藏", "下一页", "上一页", "穿透", "自动阅读"];
    // 空串表示该功能不绑定全局快捷键，不会注册，也不会在书库底部显示组合键。
    /// <summary>当前绑定的按键；空串表示该功能不绑定。</summary>
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
    /// <summary>按平台返回展示给用户的修饰键组合名称。</summary>
    public static string Modifier => OperatingSystem.IsMacOS() ? "Control + Option" : "Ctrl + Alt";
    /// <summary>注册失败的功能数量。</summary>
    public int FailedCount => failed.Count;
    // 每个功能是否注册失败，供快捷键设置窗口在三行内标注。
    /// <summary>各功能是否注册失败，供设置窗口逐行标注。</summary>
    public bool[] FailedRows => Enumerable.Range(0, Keys.Length).Select(index => failed.Contains(index + 1)).ToArray();
    // 功能 id 从 1 开始，与原生注册时使用的编号一致。
    private bool IsFailed(int index) => failed.Contains(index + 1);
    // 把用户输入归一化成 hotkeys.json 里的规范写法（大小写不敏感），无效返回 null。
    /// <summary>把按键名归一化为配置文件中的规范写法（大小写不敏感）。</summary>
    /// <param name="key">用户输入或文件中的按键名。</param>
    /// <returns>规范名称；不支持的按键返回 null。</returns>
    public static string? Canonical(string key)
    {
        string trimmed = key.Trim();
        foreach (string name in MacKeys.Keys) if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }
    // 空串表示不绑定；无效键名返回 null。
    /// <summary>规范化按键值，空串保留为“未绑定”。</summary>
    /// <param name="key">用户输入或文件中的按键名。</param>
    /// <returns>空串表示不绑定；有效键名返回规范写法；无效返回 null。</returns>
    public static string? Normalize(string key)
    {
        string trimmed = key.Trim();
        return trimmed.Length == 0 ? "" : Canonical(trimmed);
    }
    // 把按下的物理按键映射成键名；不在支持范围内的键返回 null。
    /// <summary>把 Avalonia 按键映射为配置中的键名。</summary>
    /// <param name="key">按下的物理按键。</param>
    /// <returns>A–Z、F1–F12、Left、Right、Space 之一；其它按键返回 null。</returns>
    public static string? NameOf(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.F1 && key <= Key.F12) return key.ToString();
        return key switch { Key.Left => "Left", Key.Right => "Right", Key.Space => "Space", _ => null };
    }

    /// <summary>创建快捷键管理器：读取配置、校验并立即注册全局热键。</summary>
    /// <param name="root">数据目录；配置文件为其中的 hotkeys.json。</param>
    /// <param name="action">收到热键时执行的动作，参数为从 1 开始的功能 id。</param>
    /// <remarks>配置文件缺失或无效时使用默认按键；构造期间即完成平台注册。</remarks>
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
            // 可选的偏好文件损坏时不应阻止启动，保留默认按键即可。
            catch (Exception ex) when (ex is JsonException or ArgumentException) { /* Keep defaults for an invalid optional preference file. */ }
        }
        Register();
    }

    /// <summary>保存新的按键配置并重新注册全局热键。</summary>
    /// <param name="keys">五个功能的按键，空串表示不绑定。</param>
    /// <remarks>先落盘再替换注册；写入使用临时文件加原子移动，避免留下半个 JSON。</remarks>
    public void Configure(string[] keys)
    {
        Validate(keys);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(keys)); File.Move(temp, path, true);
        // 释放旧句柄后再注册，否则旧组合键可能仍被系统占用。
        Keys = keys; native?.Dispose(); native = null; Register();
    }
    // 录入期间必须先停掉全局热键：这些组合是系统级注册的，会抢先于窗口收到按键，
    // 于是按 Ctrl+Alt+H 只会触发原来的功能，根本进不了录入框。
    /// <summary>临时释放全局热键，供快捷键录入窗口接收原始按键。</summary>
    public void Suspend() { native?.Dispose(); native = null; }
    /// <summary>重新注册当前按键配置。</summary>
    public void Resume() { native?.Dispose(); native = null; Register(); }
    // 空位允许：留空即不绑定，其余按键仍然必须有效且互不相同。
    private static void Validate(string[] keys)
    {
        string?[] normalized = keys.Select(Normalize).ToArray();
        string[] bound = normalized.Where(key => !string.IsNullOrEmpty(key)).Select(key => key!).ToArray();
        // 只对实际绑定的键查重；空位可以出现多次。
        if (keys.Length != Defaults.Length || normalized.Any(key => key == null)
            || bound.Distinct(StringComparer.OrdinalIgnoreCase).Count() != bound.Length)
            throw new ArgumentException("请输入互不相同的有效按键；留空表示不绑定。");
    }
    // 按平台注册当前绑定的按键；空位直接跳过。
    private void Register()
    {
        // 原生回调来自系统线程，统一 Post 到 UI 线程再执行动作。
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
        // 其它平台没有原生注册实现，所有绑定的功能都按注册失败处理。
        else failed = bound.Select(index => index + 1).ToList();
    }
    // 把配置里的键名转换为 Windows 虚拟键码：单字符直接用 ASCII，F1–F12 为连续区间。
    private static uint WindowsKey(string key)
    {
        string value = key.ToUpperInvariant();
        if (value.Length == 1) return value[0];
        if (value.StartsWith('F')) return 111 + uint.Parse(value[1..]);
        return value switch { "LEFT" => 0x25, "RIGHT" => 0x27, _ => 0x20 };
    }
    /// <summary>释放原生全局热键注册。</summary>
    public void Dispose() => native?.Dispose();
}
