using Avalonia;
using Reader.Core;

namespace Reader.Desktop;

/// <summary>
/// 桌面端进程入口：解析命令行开关、启动 Avalonia 应用，并处理“停止已运行实例”的控制请求。
/// </summary>
/// <remarks>
/// 静态属性在 <see cref="Main"/> 中一次性赋值，之后供各窗口共享；入口运行在 STA 主线程。
/// </remarks>
internal static class Program
{
    // A packaged build is a WinExe with no console of its own, so --stop reports through the exit
    // code and the launcher script owns the wording. These values are a contract with that script.
    /// <summary>停止请求已被运行中的实例接受，实例将保存并退出。</summary>
    public const int StopAccepted = 0;
    /// <summary>没有找到运行中的实例。</summary>
    public const int StopNotRunning = 1;
    /// <summary>实例正在忙或正在显示对话框，拒绝了停止请求。</summary>
    public const int StopRefused = 2;
    /// <summary>实例锁仍被占用，但控制通道无应答（通常是旧版本实例）。</summary>
    public const int StopUnreachable = 3;
    /// <summary>控制通道返回了其他失败结果。</summary>
    public const int StopFailed = 4;

    /// <summary>本次进程使用的数据目录；未通过 --data 指定时为 null。</summary>
    public static string? DataDirectory { get; private set; }
    /// <summary>旧版本数据根目录（--legacy-root）；未指定时为 null。</summary>
    public static string? LegacyRoot { get; private set; }
    /// <summary>是否处于冒烟测试模式；该模式要求独立的 --data 目录，并跳过真实集成注册。</summary>
    public static bool SmokeTest { get; private set; }
    /// <summary>是否以聊天服务模式（--server）启动，此时只创建服务窗口。</summary>
    public static bool ServerMode { get; private set; }
    /// <summary>
    /// 解析命令行参数并启动应用；带 --stop 时改为向已运行的实例发送停止请求。
    /// </summary>
    /// <param name="args">进程命令行参数。</param>
    [STAThread]
    public static void Main(string[] args)
    {
        // --data / --legacy-root 需要先解析：后续的单实例锁、数据迁移和存储都依赖它们。
        int data = Array.IndexOf(args, "--data");
        if (data >= 0 && data + 1 < args.Length) DataDirectory = Path.GetFullPath(args[data + 1]);
        int legacy = Array.IndexOf(args, "--legacy-root");
        if (legacy >= 0 && legacy + 1 < args.Length) LegacyRoot = Path.GetFullPath(args[legacy + 1]);
        // 模式开关在初始化前确定，供窗口创建和集成注册分支使用。
        SmokeTest = args.Contains("--smoke-test");
        ServerMode = args.Contains("--server");
        // --stop 不创建窗口，直接把控制通道的结果映射为退出码交给启动脚本。
        if (args.Contains("--stop")) { Environment.ExitCode = StopRunningCopyAsync().GetAwaiter().GetResult(); return; }
        // 冒烟测试必须使用隔离目录，否则测试数据会写进真实书库。
        if (SmokeTest && DataDirectory == null) throw new ArgumentException("--smoke-test requires an isolated --data directory.");
        // 经典桌面生命周期会一直阻塞到应用退出；入口线程需要 STA。
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Asks the running copy to save and exit. A copy that predates the control channel still holds
    // the instance lock, so telling that apart from "nothing is running" is worth a separate code.
    private static async Task<int> StopRunningCopyAsync()
    {
        string root = DataDirectory ?? DataPaths.Default;
        // 有应答时按控制通道的约定映射；无应答时再用实例锁区分“没在运行”和“旧实例占着锁”。
        string? reply = await ControlChannel.TrySendAsync(root, ControlChannel.ShutdownCommand, TimeSpan.FromSeconds(10));
        if (reply == ControlChannel.Accepted) return StopAccepted;
        if (reply == ControlChannel.Refused) return StopRefused;
        if (reply != null) return StopFailed;
        return ControlChannel.LockHeld(root) ? StopUnreachable : StopNotRunning;
    }

    /// <summary>创建 Avalonia 应用构建器。</summary>
    /// <returns>已配置平台检测和跟踪日志的构建器。</returns>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
