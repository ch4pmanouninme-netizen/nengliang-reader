// Windows 平台集成：全局热键消息循环与窗口点击穿透。
using System.Runtime.InteropServices;

namespace Reader.Platform.Windows;

/// <summary>Windows 全局热键与点击穿透实现。</summary>
/// <remarks>热键注册、消息循环和注销都在专用后台线程上执行；构造函数会阻塞到注册结果确定。
/// 注册失败的 ID 记录在 <see cref="Failed"/> 中，不影响其余热键。</remarks>
public sealed class WindowsIntegration : IDisposable
{
    // 专用线程持有消息队列；Win32 热键消息只会投递到注册它的线程。
    private readonly Thread thread;
    // 构造函数等待此事件，确保返回时注册结果已经确定。
    private readonly ManualResetEventSlim ready = new();
    // 回调在消息循环线程触发，订阅方需自行切换到 UI 线程。
    private readonly Action<int> pressed;
    // 线程 ID 用于 Dispose 时向该线程投递 WM_QUIT。
    private uint threadId;
    /// <summary>注册失败的热键 ID 列表；构造函数返回后即可读取。</summary>
    public List<int> Failed { get; } = [];

    /// <summary>注册全局热键并启动消息循环。</summary>
    /// <param name="bindings">热键 ID 与虚拟键码的对应关系。</param>
    /// <param name="pressed">热键按下时的回调，在专用消息循环线程上调用。</param>
    public WindowsIntegration(IReadOnlyList<(int Id, uint Key)> bindings, Action<int> pressed)
    {
        this.pressed = pressed;
        // 后台线程不阻止进程退出；构造等待 ready，保证调用方拿到对象时注册已完成。
        thread = new Thread(() => Run(bindings)) { IsBackground = true, Name = "Reader hotkeys" };
        thread.Start();
        ready.Wait();
    }

    private void Run(IReadOnlyList<(int Id, uint Key)> bindings)
    {
        threadId = GetCurrentThreadId();
        // Create this thread's message queue before publishing readiness.
        // PeekMessage 即使不取消息也会强制创建线程消息队列，否则后续 PostThreadMessage 可能失败。
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        foreach (var binding in bindings)
            // 0x4003 = MOD_ALT | MOD_CONTROL | MOD_NOREPEAT；NOREPEAT 防止按住热键重复触发。
            if (!RegisterHotKey(IntPtr.Zero, binding.Id, 0x4003, binding.Key)) Failed.Add(binding.Id);
        ready.Set();
        // GetMessage 返回 0 表示收到 WM_QUIT；0x0312 = WM_HOTKEY，wParam 即注册时使用的 ID。
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            if (message.Id == 0x0312) pressed((int)message.WParam);
        // 消息循环退出后注销全部热键，避免线程结束后仍残留系统注册。
        foreach (var binding in bindings) UnregisterHotKey(IntPtr.Zero, binding.Id);
    }

    /// <summary>设置或取消窗口的鼠标点击穿透。</summary>
    /// <param name="window">目标窗口句柄。</param>
    /// <param name="enabled">true 表示鼠标事件穿透到下层窗口。</param>
    /// <exception cref="System.ComponentModel.Win32Exception">设置窗口扩展样式失败。</exception>
    public static void SetClickThrough(IntPtr window, bool enabled)
    {
        // -20 = GWL_EXSTYLE。
        // 0x20 = WS_EX_TRANSPARENT（点击穿透），0x08000000 = WS_EX_NOACTIVATE（点击不激活窗口）。
        int style = GetWindowLong(window, -20);
        int flags = 0x20 | 0x08000000;
        // SetWindowLong 失败返回 0，也可能只是旧值为 0，因此先清零 last error 再判断。
        Marshal.SetLastPInvokeError(0);
        // 0x80000 = WS_EX_LAYERED；启用穿透时一并加上，供后续设置透明度使用。
        _ = SetWindowLong(window, -20, enabled ? style | flags | 0x80000 : style & ~flags);
        int error = Marshal.GetLastPInvokeError();
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
    }

    /// <summary>设置点击穿透，并在窗口为分层窗口时应用透明度。</summary>
    /// <param name="window">目标窗口句柄。</param>
    /// <param name="enabled">true 表示启用鼠标穿透。</param>
    /// <param name="opacity">不透明度，0 为全透明，255 为完全不透明。</param>
    /// <exception cref="System.ComponentModel.Win32Exception">设置扩展样式或透明度失败。</exception>
    public static void SetClickThrough(IntPtr window, bool enabled, byte opacity)
    {
        SetClickThrough(window, enabled);
        // 只有 WS_EX_LAYERED 窗口才能调用 SetLayeredWindowAttributes；2 = LWA_ALPHA。
        if ((GetWindowLong(window, -20) & 0x80000) != 0 && !SetLayeredWindowAttributes(window, 0, opacity, 2))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>停止消息循环并等待后台线程退出。</summary>
    /// <remarks>最多等待 2 秒；线程是后台线程，即使未及时退出也不会阻止进程结束。</remarks>
    public void Dispose()
    {
        // 0x0012 = WM_QUIT；向消息循环线程投递后等待其注销热键并返回。
        PostThreadMessage(threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
        ready.Dispose();
    }

    // MSG 结构；字段顺序和大小必须与 Win32 定义一致，GetMessage/PeekMessage 会直接写入该结构。
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam;
        public uint Time; public int X, Y; public uint Private;
    }
    // DllImport 默认使用 Winapi 调用约定（x86 上为 stdcall，x64 上统一），与这些导出匹配。
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint message, UIntPtr w, IntPtr l);
    // 显式指定 W 版本入口，避免运行时按 CharSet 推断到 ANSI 入口。
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    // SetLastError=true 与 Marshal.GetLastPInvokeError() 配对，用于报告具体 Win32 错误码。
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
}
