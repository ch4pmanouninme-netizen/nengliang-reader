using System.Runtime.InteropServices;

namespace Reader.Platform.Windows;

public sealed class WindowsIntegration : IDisposable
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private readonly Action<int> pressed;
    private uint threadId;
    public List<int> Failed { get; } = [];

    public WindowsIntegration(IReadOnlyList<(int Id, uint Key)> bindings, Action<int> pressed)
    {
        this.pressed = pressed;
        thread = new Thread(() => Run(bindings)) { IsBackground = true, Name = "Reader hotkeys" };
        thread.Start();
        ready.Wait();
    }

    private void Run(IReadOnlyList<(int Id, uint Key)> bindings)
    {
        threadId = GetCurrentThreadId();
        // Create this thread's message queue before publishing readiness.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        foreach (var binding in bindings)
            if (!RegisterHotKey(IntPtr.Zero, binding.Id, 0x4003, binding.Key)) Failed.Add(binding.Id);
        ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            if (message.Id == 0x0312) pressed((int)message.WParam);
        foreach (var binding in bindings) UnregisterHotKey(IntPtr.Zero, binding.Id);
    }

    public static void SetClickThrough(IntPtr window, bool enabled)
    {
        int style = GetWindowLong(window, -20);
        int flags = 0x20 | 0x08000000;
        Marshal.SetLastPInvokeError(0);
        _ = SetWindowLong(window, -20, enabled ? style | flags | 0x80000 : style & ~flags);
        int error = Marshal.GetLastPInvokeError();
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
    }

    public static void SetClickThrough(IntPtr window, bool enabled, byte opacity)
    {
        SetClickThrough(window, enabled);
        if ((GetWindowLong(window, -20) & 0x80000) != 0 && !SetLayeredWindowAttributes(window, 0, opacity, 2))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    public void Dispose()
    {
        PostThreadMessage(threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam;
        public uint Time; public int X, Y; public uint Private;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint message, UIntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
}
