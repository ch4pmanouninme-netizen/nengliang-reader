// macOS 平台集成：Carbon 全局热键注册与 Objective-C 窗口点击穿透。
using System.Runtime.InteropServices;

namespace Reader.Platform.Mac;

/// <summary>macOS 全局热键与点击穿透实现。</summary>
/// <remarks>热键通过 Carbon 的应用事件目标注册，回调在应用事件循环所在线程触发（通常是主线程）。
/// 注册失败的 ID 记录在 <see cref="Failed"/> 中，不影响其余热键。</remarks>
public sealed class MacIntegration : IDisposable
{
    // 使用系统框架与运行时的绝对路径，DllImport 直接按路径加载，不依赖框架搜索路径。
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    // 保存委托实例，防止原生代码仍持有函数指针时托管对象被 GC 回收。
    private readonly EventCallback callback;
    // 已注册热键的引用，Dispose 时逐一注销。
    private readonly List<IntPtr> registrations = [];
    // Carbon 事件处理器引用；InstallEventHandler 失败时保持为零。
    private IntPtr handler;
    /// <summary>注册失败的热键 ID 列表；构造函数返回后即可读取。</summary>
    public List<int> Failed { get; } = [];

    /// <summary>注册全局热键。</summary>
    /// <param name="bindings">热键 ID 与虚拟键码的对应关系。</param>
    /// <param name="pressed">热键按下时的回调，在应用事件循环线程上调用。</param>
    public MacIntegration(IReadOnlyList<(int Id, uint Key)> bindings, Action<int> pressed)
    {
        callback = (_, nativeEvent, _) =>
        {
            // '----' = kEventParamDirectObject，'hkid' = typeEventHotKeyID，8 = sizeof(EventHotKeyID)。
            // 四字符签名 'NLRD' 用于过滤本程序注册的热键，避免响应其他组件的事件。
            int result = GetEventParameter(nativeEvent, 0x2d2d2d2d, 0x686b6964, IntPtr.Zero, 8, out _, out var hotkey);
            if (result == 0 && hotkey.Signature == 0x4e4c5244) pressed((int)hotkey.Id);
            return result;
        };
        // 5 = kEventHotKeyPressed；6 是 kEventHotKeyReleased，会让热键松开才触发。
        // 0x6b657962 = 'keyb'，即 kEventClassKeyboard。
        EventType type = new() { Class = 0x6b657962, Kind = 5 };
        IntPtr target = GetApplicationEventTarget();
        // 事件处理器安装失败时所有热键都不可用，统一记入 Failed 后返回。
        if (InstallEventHandler(target, callback, 1, ref type, IntPtr.Zero, out handler) != 0)
        {
            Failed.AddRange(bindings.Select(binding => binding.Id));
            return;
        }
        foreach (var binding in bindings)
        {
            HotKeyId id = new() { Signature = 0x4e4c5244, Id = (uint)binding.Id };
            // Control + Option. Uses registered hotkeys, not a global keystroke monitor.
            // 0x1000 = controlKey，0x0800 = optionKey；系统级注册不依赖辅助功能权限。
            if (RegisterEventHotKey(binding.Key, 0x1000 | 0x0800, id, target, 0, out var reference) == 0)
                registrations.Add(reference);
            else Failed.Add(binding.Id);
        }
    }

    /// <summary>设置或取消窗口的鼠标点击穿透。</summary>
    /// <param name="handle">NSWindow 或 NSView 的句柄。</param>
    /// <param name="enabled">true 表示窗口忽略鼠标事件。</param>
    /// <exception cref="InvalidOperationException">无法从视图取得所属窗口。</exception>
    public static void SetClickThrough(IntPtr handle, bool enabled)
    {
        IntPtr setter = sel_registerName("setIgnoresMouseEvents:");
        // respondsToSelector: 能判断传入的是窗口还是视图；视图需要先通过 window 取得所属 NSWindow。
        if (SendBool(handle, sel_registerName("respondsToSelector:"), setter) == 0)
            handle = Send(handle, sel_registerName("window"));
        if (handle == IntPtr.Zero) throw new InvalidOperationException("无法取得 macOS 阅读窗口。");
        // Objective-C 的 BOOL 是单字节，必须用 byte 版本的 objc_msgSend 签名，不能用 IntPtr 版本。
        SendSetBool(handle, setter, enabled ? (byte)1 : (byte)0);
    }

    /// <summary>注销全部热键并移除事件处理器。</summary>
    /// <remarks>可重复调用不会重复注销；建议在创建实例的线程上调用，避免与事件回调并发。</remarks>
    public void Dispose()
    {
        // 先注销热键，再移除事件处理器，避免回调在处理器释放后仍被触发。
        foreach (var reference in registrations) UnregisterEventHotKey(reference);
        registrations.Clear();
        if (handler != IntPtr.Zero) { RemoveEventHandler(handler); handler = IntPtr.Zero; }
        // 原生事件处理器可能仍持有委托指针，确保它在本方法结束前不被回收。
        GC.KeepAlive(callback);
    }

    // EventTypeSpec：事件类与事件种类的组合，必须与 Carbon 的结构布局一致。
    [StructLayout(LayoutKind.Sequential)] private struct EventType { public uint Class, Kind; }
    // EventHotKeyID：四字符签名加调用方分配的 ID。
    [StructLayout(LayoutKind.Sequential)] private struct HotKeyId { public uint Signature, Id; }
    // Carbon 事件回调使用 Cdecl 调用约定，返回值是 OSStatus。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EventCallback(IntPtr next, IntPtr nativeEvent, IntPtr data);
    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(IntPtr target, EventCallback callback, uint count, ref EventType types, IntPtr data, out IntPtr handler);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint key, uint modifiers, HotKeyId id, IntPtr target, uint options, out IntPtr reference);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr reference);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handler);
    // size 必须与 EventHotKeyID 的大小一致（8 字节），否则 GetEventParameter 会返回错误。
    [DllImport(Carbon)] private static extern int GetEventParameter(IntPtr nativeEvent, uint name, uint type, IntPtr actualType, uint size, out uint actualSize, out HotKeyId data);
    [DllImport(ObjectiveC)] private static extern IntPtr sel_registerName(string name);
    // objc_msgSend 必须按选择器的真实返回类型选择签名：对象返回 IntPtr，BOOL 返回 byte，void 直接调用。
    // 在 arm64 上混用签名会破坏返回寄存器或栈布局。
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr value);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern void SendSetBool(IntPtr receiver, IntPtr selector, byte value);
}
