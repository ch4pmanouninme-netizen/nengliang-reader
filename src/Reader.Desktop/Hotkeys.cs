using System.Text.Json;
using Avalonia.Input;
using Avalonia.Threading;
using Reader.Platform.Windows;
using Reader.Platform.Mac;

namespace Reader.Desktop;

/// <summary>
/// 全局快捷键管理：读写 hotkeys.json、按平台注册系统热键，并把回调调度回 UI 线程。
/// </summary>
/// <remarks>
/// 每个功能保存一个完整组合键（如 <c>Ctrl+Alt+H</c>），修饰键可以是 Ctrl / Alt / Shift / Win
/// （Mac 为 Control / Option / Shift / Command）的任意组合，但至少需要一个修饰键。
/// 空字符串表示该功能不绑定；旧版只保存单键名的配置会按 Ctrl+Alt 自动迁移。
/// 实例持有原生注册句柄，Dispose 时释放。
/// </remarks>
internal sealed class Hotkeys : IDisposable
{
    private readonly string path;
    private readonly Action<int> action;
    // 平台原生注册对象；Suspend/Resume 和 Dispose 都会替换或释放它。
    private IDisposable? native;
    // 注册失败的功能 id（从 1 开始）。
    private List<int> failed = [];
    /// <summary>五个功能的默认组合键。</summary>
    public static readonly string[] Defaults = ["Ctrl+Alt+H", "Ctrl+Alt+Right", "Ctrl+Alt+Left", "Ctrl+Alt+M", "Ctrl+Alt+Space"];
    /// <summary>功能名称，顺序与 <see cref="Keys"/> 和默认组合键一致。</summary>
    public static readonly string[] Functions = ["隐藏", "下一页", "上一页", "穿透", "自动阅读"];
    // 空串表示该功能不绑定全局快捷键，不会注册，也不会在书库底部显示组合键。
    /// <summary>当前绑定的组合键；空串表示该功能不绑定。</summary>
    public string[] Keys { get; private set; } = [.. Defaults];

    // macOS 注册热键使用的硬件键码；同时作为受支持按键的名称表。
    // Carbon 的键码与键盘布局相关，这里只收录跨布局稳定的常用键。
    private static readonly Dictionary<string, uint> MacKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"]=0,["S"]=1,["D"]=2,["F"]=3,["H"]=4,["G"]=5,["Z"]=6,["X"]=7,["C"]=8,["V"]=9,["B"]=11,
        ["Q"]=12,["W"]=13,["E"]=14,["R"]=15,["Y"]=16,["T"]=17,["O"]=31,["U"]=32,["I"]=34,["P"]=35,
        ["L"]=37,["J"]=38,["K"]=40,["N"]=45,["M"]=46,["Space"]=49,["Left"]=123,["Right"]=124,
        ["Up"]=126,["Down"]=125,["Home"]=115,["End"]=119,["PageUp"]=116,["PageDown"]=121,
        ["0"]=29,["1"]=18,["2"]=19,["3"]=20,["4"]=21,["5"]=23,["6"]=22,["7"]=26,["8"]=28,["9"]=25,
        ["NumPad0"]=82,["NumPad1"]=83,["NumPad2"]=84,["NumPad3"]=85,["NumPad4"]=86,
        ["NumPad5"]=87,["NumPad6"]=88,["NumPad7"]=89,["NumPad8"]=91,["NumPad9"]=92,
        ["F1"]=122,["F2"]=120,["F3"]=99,["F4"]=118,["F5"]=96,["F6"]=97,["F7"]=98,["F8"]=100,
        ["F9"]=101,["F10"]=109,["F11"]=103,["F12"]=111
    };
    // 受支持按键的规范名称（大小写不敏感查找，返回字典里的规范写法）。
    private static readonly Dictionary<string, string> KeyNames = MacKeys.Keys.ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);
    /// <summary>注册失败的功能数量。</summary>
    public int FailedCount => failed.Count;
    // 每个功能是否注册失败，供快捷键设置窗口逐行标注。
    /// <summary>各功能是否注册失败，供设置窗口逐行标注。</summary>
    public bool[] FailedRows => Enumerable.Range(0, Keys.Length).Select(index => failed.Contains(index + 1)).ToArray();

    /// <summary>规范化组合键：空串表示未绑定，无效或缺少修饰键返回 null。</summary>
    /// <param name="value">存储值或用户输入，如 <c>Ctrl+Shift+H</c>；允许修饰键周围有空格。</param>
    /// <returns>规范写法（修饰键固定按 Ctrl、Alt、Shift、Win 排序）；无效返回 null。</returns>
    public static string? Normalize(string value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return "";
        return TryParse(trimmed, out Gesture gesture)
            ? Build(gesture.Ctrl, gesture.Alt, gesture.Shift, gesture.Win, gesture.Key)
            : null;
    }

    /// <summary>把新录入的按键与当前修饰键组合成规范写法。</summary>
    /// <param name="key">受支持的键名，通常来自 <see cref="NameOf"/>。</param>
    /// <param name="modifiers">按下主键时的修饰键状态。</param>
    /// <returns>规范组合键；没有修饰键或按键不受支持时返回 null。</returns>
    public static string? FromInput(string key, KeyModifiers modifiers)
    {
        string? canonical = CanonicalKey(key);
        if (canonical == null) return null;
        bool ctrl = modifiers.HasFlag(KeyModifiers.Control);
        bool alt = modifiers.HasFlag(KeyModifiers.Alt);
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        bool win = modifiers.HasFlag(KeyModifiers.Meta);
        if (!(ctrl || alt || shift || win)) return null;
        return Build(ctrl, alt, shift, win, canonical);
    }

    /// <summary>按当前平台显示组合键；Mac 上显示 Control / Option / Command。</summary>
    /// <param name="gesture">规范组合键，空串表示未绑定。</param>
    /// <returns>面向用户的展示文本；无法解析时原样返回。</returns>
    public static string Display(string gesture)
    {
        if (String.IsNullOrEmpty(gesture)) return "";
        if (!TryParse(gesture, out Gesture parsed)) return gesture;
        List<string> parts = new List<string>();
        if (parsed.Ctrl) parts.Add(OperatingSystem.IsMacOS() ? "Control" : "Ctrl");
        if (parsed.Alt) parts.Add(OperatingSystem.IsMacOS() ? "Option" : "Alt");
        if (parsed.Shift) parts.Add("Shift");
        if (parsed.Win) parts.Add(OperatingSystem.IsMacOS() ? "Command" : "Win");
        parts.Add(parsed.Key);
        return String.Join(" + ", parts);
    }

    /// <summary>把 Avalonia 按键映射为受支持的键名。</summary>
    /// <param name="key">按下的物理按键。</param>
    /// <returns>A–Z、0–9、小键盘 0–9、F1–F12、方向键、Home/End/PageUp/PageDown、Space 之一；其它按键返回 null。</returns>
    public static string? NameOf(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + ((int)key - (int)Key.D0))).ToString();
        if (key >= Key.F1 && key <= Key.F12) return key.ToString();
        return key switch
        {
            Key.NumPad0 => "NumPad0", Key.NumPad1 => "NumPad1", Key.NumPad2 => "NumPad2", Key.NumPad3 => "NumPad3", Key.NumPad4 => "NumPad4",
            Key.NumPad5 => "NumPad5", Key.NumPad6 => "NumPad6", Key.NumPad7 => "NumPad7", Key.NumPad8 => "NumPad8", Key.NumPad9 => "NumPad9",
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Space => "Space", _ => null
        };
    }

    /// <summary>创建快捷键管理器：读取配置、校验并立即注册全局热键。</summary>
    /// <param name="root">数据目录；配置文件为其中的 hotkeys.json。</param>
    /// <param name="action">收到热键时执行的动作，参数为从 1 开始的功能 id。</param>
    /// <remarks>配置文件缺失或无效时使用默认组合键；构造期间即完成平台注册。</remarks>
    public Hotkeys(string root, Action<int> action)
    {
        path = Path.Combine(root, "hotkeys.json"); this.action = action;
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
                if (loaded != null && loaded.Length == Defaults.Length)
                {
                    string?[] normalized = loaded.Select(NormalizeLoaded).ToArray();
                    if (normalized.All(value => value != null))
                    {
                        string[] migrated = normalized.Select(value => value!).ToArray();
                        Validate(migrated);
                        Keys = migrated;
                    }
                }
            }
            // 可选的偏好文件损坏时不应阻止启动，保留默认组合键即可。
            catch (Exception ex) when (ex is JsonException or ArgumentException) { /* Keep defaults for an invalid optional preference file. */ }
        }
        Register();
    }

    /// <summary>保存新的组合键配置并重新注册全局热键。</summary>
    /// <param name="keys">五个功能的组合键，空串表示不绑定。</param>
    /// <remarks>先落盘再替换注册；写入使用临时文件加原子移动，避免留下半个 JSON。</remarks>
    public void Configure(string[] keys)
    {
        Validate(keys);
        string[] normalized = keys.Select(key => Normalize(key) ?? "").ToArray();
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(normalized)); File.Move(temp, path, true);
        // 释放旧句柄后再注册，否则旧组合键可能仍被系统占用。
        Keys = normalized; native?.Dispose(); native = null; Register();
    }
    // 录入期间必须先停掉全局热键：这些组合是系统级注册的，会抢先于窗口收到按键，
    // 于是按原来的组合只会触发原来的功能，根本进不了录入框。
    /// <summary>临时释放全局热键，供快捷键录入窗口接收原始按键。</summary>
    public void Suspend() { native?.Dispose(); native = null; }
    /// <summary>重新注册当前组合键配置。</summary>
    public void Resume() { native?.Dispose(); native = null; Register(); }

    // 空位允许：留空即不绑定，其余组合键仍然必须有效且互不相同。
    private static void Validate(string[] keys)
    {
        if (keys.Length != Defaults.Length) throw new ArgumentException("快捷键数量与功能数量不一致。");
        string?[] normalized = keys.Select(Normalize).ToArray();
        string[] bound = normalized.Where(key => !String.IsNullOrEmpty(key)).Select(key => key!).ToArray();
        // 只对实际绑定的组合键查重；空位可以出现多次。
        if (normalized.Any(key => key == null) || bound.Distinct(StringComparer.OrdinalIgnoreCase).Count() != bound.Length)
            throw new ArgumentException("请输入互不相同的有效组合键；留空表示不绑定。");
    }

    // 旧版只保存单键名（如 "H"）；按原来的默认修饰键 Ctrl+Alt 迁移，升级后行为不变。
    private static string? NormalizeLoaded(string value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return "";
        if (!trimmed.Contains('+'))
        {
            string? key = CanonicalKey(trimmed);
            return key == null ? null : Build(true, true, false, false, key);
        }
        return Normalize(trimmed);
    }

    // 按平台注册当前绑定的组合键；空位直接跳过。
    private void Register()
    {
        // 原生回调来自系统线程，统一 Post 到 UI 线程再执行动作。
        Action<int> dispatch = id => Dispatcher.UIThread.Post(() => action(id));
        List<(int Id, uint Modifiers, uint Key)> bindings = new List<(int Id, uint Modifiers, uint Key)>();
        failed = [];
        for (int index = 0; index < Keys.Length; index++)
        {
            if (Keys[index].Length == 0) continue;
            if (!TryParse(Keys[index], out Gesture gesture))
            {
                failed.Add(index + 1);
                continue;
            }
            if (OperatingSystem.IsWindows())
                bindings.Add((index + 1, WindowsModifiers(gesture), WindowsKey(gesture.Key)));
            else if (OperatingSystem.IsMacOS())
                bindings.Add((index + 1, MacModifiers(gesture), MacKeys[gesture.Key]));
            else
                failed.Add(index + 1);
        }
        if (OperatingSystem.IsWindows())
        {
            var integration = new WindowsIntegration(bindings, dispatch);
            native = integration; failed.AddRange(integration.Failed);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var integration = new MacIntegration(bindings, dispatch);
            native = integration; failed.AddRange(integration.Failed);
        }
    }

    // Windows RegisterHotKey 的修饰键位：MOD_ALT=1、MOD_CONTROL=2、MOD_SHIFT=4、MOD_WIN=8。
    private static uint WindowsModifiers(Gesture gesture)
    {
        uint modifiers = 0;
        if (gesture.Alt) modifiers |= 0x0001;
        if (gesture.Ctrl) modifiers |= 0x0002;
        if (gesture.Shift) modifiers |= 0x0004;
        if (gesture.Win) modifiers |= 0x0008;
        return modifiers;
    }

    // Carbon 的修饰键位：cmdKey=0x0100、shiftKey=0x0200、optionKey=0x0800、controlKey=0x1000。
    private static uint MacModifiers(Gesture gesture)
    {
        uint modifiers = 0;
        if (gesture.Win) modifiers |= 0x0100;
        if (gesture.Shift) modifiers |= 0x0200;
        if (gesture.Alt) modifiers |= 0x0800;
        if (gesture.Ctrl) modifiers |= 0x1000;
        return modifiers;
    }

    // 把规范键名转换为 Windows 虚拟键码：单字符（A–Z/0–9）直接用 ASCII，F1–F12 为连续区间。
    private static uint WindowsKey(string key)
    {
        string value = key.ToUpperInvariant();
        if (value.Length == 1) return value[0];
        if (value.StartsWith('F')) return 111 + uint.Parse(value[1..]);
        // 小键盘数字：VK_NUMPAD0..VK_NUMPAD9 = 0x60..0x69。
        if (value.StartsWith("NUMPAD") && uint.TryParse(value[6..], out uint pad) && pad <= 9) return 0x60 + pad;
        return value switch
        {
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            _ => 0x20 // Space
        };
    }

    private static string? CanonicalKey(string name)
    {
        if (String.IsNullOrWhiteSpace(name)) return null;
        return KeyNames.TryGetValue(name.Trim(), out string? canonical) ? canonical : null;
    }

    private static string Build(bool ctrl, bool alt, bool shift, bool win, string key)
    {
        List<string> parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(key);
        return String.Join("+", parts);
    }

    // 解析组合键：最后一个片段是主键，前面的都是修饰键；至少需要一个修饰键。
    private static bool TryParse(string value, out Gesture gesture)
    {
        gesture = default;
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return false;
        string[] parts = trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        string? key = CanonicalKey(parts[^1]);
        if (key == null) return false;
        bool ctrl = false, alt = false, shift = false, win = false;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            switch (parts[index].ToLowerInvariant())
            {
                case "ctrl":
                case "control": ctrl = true; break;
                case "alt":
                case "option": alt = true; break;
                case "shift": shift = true; break;
                case "win":
                case "meta":
                case "cmd":
                case "command": win = true; break;
                default: return false;
            }
        }
        if (!(ctrl || alt || shift || win)) return false;
        gesture = new Gesture(ctrl, alt, shift, win, key);
        return true;
    }

    /// <summary>释放原生全局热键注册。</summary>
    public void Dispose() => native?.Dispose();

    // 解析后的组合键：修饰键布尔值 + 规范键名。
    private readonly record struct Gesture(bool Ctrl, bool Alt, bool Shift, bool Win, string Key);
}
