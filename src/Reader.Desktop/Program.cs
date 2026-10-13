using Avalonia;

namespace Reader.Desktop;

internal static class Program
{
    public static string? DataDirectory { get; private set; }
    public static bool SmokeTest { get; private set; }
    public static bool ServerMode { get; private set; }
    [STAThread]
    public static void Main(string[] args)
    {
        int data = Array.IndexOf(args, "--data");
        if (data >= 0 && data + 1 < args.Length) DataDirectory = Path.GetFullPath(args[data + 1]);
        SmokeTest = args.Contains("--smoke-test");
        ServerMode = args.Contains("--server");
        if (SmokeTest && DataDirectory == null) throw new ArgumentException("--smoke-test requires an isolated --data directory.");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
