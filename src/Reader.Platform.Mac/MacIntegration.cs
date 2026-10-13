using System.Runtime.InteropServices;

namespace Reader.Platform.Mac;

public sealed class MacIntegration : IDisposable
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private readonly EventCallback callback;
    private readonly List<IntPtr> registrations = [];
    private IntPtr handler;
    public List<int> Failed { get; } = [];

    public MacIntegration(IReadOnlyList<(int Id, uint Key)> bindings, Action<int> pressed)
    {
        callback = (_, nativeEvent, _) =>
        {
            int result = GetEventParameter(nativeEvent, 0x2d2d2d2d, 0x686b6964, IntPtr.Zero, 8, out _, out var hotkey);
            if (result == 0 && hotkey.Signature == 0x4e4c5244) pressed((int)hotkey.Id);
            return result;
        };
        EventType type = new() { Class = 0x6b657962, Kind = 6 };
        IntPtr target = GetApplicationEventTarget();
        if (InstallEventHandler(target, callback, 1, ref type, IntPtr.Zero, out handler) != 0)
        {
            Failed.AddRange(bindings.Select(binding => binding.Id));
            return;
        }
        foreach (var binding in bindings)
        {
            HotKeyId id = new() { Signature = 0x4e4c5244, Id = (uint)binding.Id };
            // Control + Option. Uses registered hotkeys, not a global keystroke monitor.
            if (RegisterEventHotKey(binding.Key, 0x1000 | 0x0800, id, target, 0, out var reference) == 0)
                registrations.Add(reference);
            else Failed.Add(binding.Id);
        }
    }

    public static void SetClickThrough(IntPtr handle, bool enabled)
    {
        IntPtr setter = sel_registerName("setIgnoresMouseEvents:");
        if (SendBool(handle, sel_registerName("respondsToSelector:"), setter) == 0)
            handle = Send(handle, sel_registerName("window"));
        if (handle == IntPtr.Zero) throw new InvalidOperationException("无法取得 macOS 阅读窗口。");
        SendSetBool(handle, setter, enabled ? (byte)1 : (byte)0);
    }

    public void Dispose()
    {
        foreach (var reference in registrations) UnregisterEventHotKey(reference);
        registrations.Clear();
        if (handler != IntPtr.Zero) { RemoveEventHandler(handler); handler = IntPtr.Zero; }
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential)] private struct EventType { public uint Class, Kind; }
    [StructLayout(LayoutKind.Sequential)] private struct HotKeyId { public uint Signature, Id; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EventCallback(IntPtr next, IntPtr nativeEvent, IntPtr data);
    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(IntPtr target, EventCallback callback, uint count, ref EventType types, IntPtr data, out IntPtr handler);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint key, uint modifiers, HotKeyId id, IntPtr target, uint options, out IntPtr reference);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr reference);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handler);
    [DllImport(Carbon)] private static extern int GetEventParameter(IntPtr nativeEvent, uint name, uint type, IntPtr actualType, uint size, out uint actualSize, out HotKeyId data);
    [DllImport(ObjectiveC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern byte SendBool(IntPtr receiver, IntPtr selector, IntPtr value);
    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static extern void SendSetBool(IntPtr receiver, IntPtr selector, byte value);
}
