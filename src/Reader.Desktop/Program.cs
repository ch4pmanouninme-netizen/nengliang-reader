using Avalonia;
using Reader.Core;

namespace Reader.Desktop;

internal static class Program
{
    // A packaged build is a WinExe with no console of its own, so --stop reports through the exit
    // code and the launcher script owns the wording. These values are a contract with that script.
    public const int StopAccepted = 0;
    public const int StopNotRunning = 1;
    public const int StopRefused = 2;
    public const int StopUnreachable = 3;
    public const int StopFailed = 4;

    public static string? DataDirectory { get; private set; }
    public static string? LegacyRoot { get; private set; }
    public static bool SmokeTest { get; private set; }
    public static bool ServerMode { get; private set; }
    [STAThread]
    public static void Main(string[] args)
    {
        int data = Array.IndexOf(args, "--data");
        if (data >= 0 && data + 1 < args.Length) DataDirectory = Path.GetFullPath(args[data + 1]);
        int legacy = Array.IndexOf(args, "--legacy-root");
        if (legacy >= 0 && legacy + 1 < args.Length) LegacyRoot = Path.GetFullPath(args[legacy + 1]);
        SmokeTest = args.Contains("--smoke-test");
        ServerMode = args.Contains("--server");
        if (args.Contains("--stop")) { Environment.ExitCode = StopRunningCopyAsync().GetAwaiter().GetResult(); return; }
        if (SmokeTest && DataDirectory == null) throw new ArgumentException("--smoke-test requires an isolated --data directory.");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Asks the running copy to save and exit. A copy that predates the control channel still holds
    // the instance lock, so telling that apart from "nothing is running" is worth a separate code.
    private static async Task<int> StopRunningCopyAsync()
    {
        string root = DataDirectory ?? DataPaths.Default;
        string? reply = await ControlChannel.TrySendAsync(root, ControlChannel.ShutdownCommand, TimeSpan.FromSeconds(10));
        if (reply == ControlChannel.Accepted) return StopAccepted;
        if (reply == ControlChannel.Refused) return StopRefused;
        if (reply != null) return StopFailed;
        return ControlChannel.LockHeld(root) ? StopUnreachable : StopNotRunning;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
