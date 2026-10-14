using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Reader.Core;

namespace Reader.Desktop;

public sealed class App : Application
{
    private FileStream? instance;
    private TrayIcon? tray;
    private MainWindow? library;
    private CancellationTokenSource? control;
    internal FluentTheme Fluent { get; } = new();
    public override void Initialize() => Styles.Add(Fluent);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (Program.ServerMode)
            {
                try { var server = new ServerWindow(); desktop.MainWindow = server; server.Closed += (_, _) => desktop.Shutdown(); }
                catch (Exception ex)
                {
                    var error = new Window { Title = "聊天服务启动失败", Width = 520, Height = 220, Content = new TextBlock { Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = ex.Message } };
                    desktop.MainWindow = error; error.Closed += (_, _) => desktop.Shutdown(1);
                }
                base.OnFrameworkInitializationCompleted(); return;
            }
            try
            {
                string root = Program.DataDirectory ?? DataPaths.Default;
                Directory.CreateDirectory(root);
                try
                {
                    instance = new FileStream(Path.Combine(root, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    // Another copy is holding the lock. Closing the window only hides it to the tray,
                    // so the lock outlives the close and the raw IOException reads as a broken install.
                    var busy = new Window { Title = "良子小窗已经在运行", Width = 520, Height = 220,
                        Content = new TextBlock { Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            Text = "良子小窗已经在运行，不需要重复打开。\n\n如果窗口不见了，请查看屏幕右下角托盘里的良子小窗图标。想彻底退出（或重新打开），在该图标上点右键，选择「退出」。" } };
                    busy.Closed += (_, _) => desktop.Shutdown(1);
                    desktop.MainWindow = busy;
                    base.OnFrameworkInitializationCompleted();
                    return;
                }
                if (Program.DataDirectory == null) DataPaths.MigrateLegacy(DataPaths.ResolveLegacyRoot(Program.LegacyRoot, AppContext.BaseDirectory), root);
                var store = new Store(root);
                library = new MainWindow(store);
                var home = new HomeWindow(store, library);
                if (Program.SmokeTest) library.SmokeHome = home;
                desktop.MainWindow = Program.SmokeTest ? library : home;
                var menu = new NativeMenu();
                Add(menu, "主页", home.Restore);
                Add(menu, "隐藏 / 恢复窗口", home.ToggleVisibility);
                Add(menu, "退出", library.RequestExit);
                tray = new TrayIcon { Icon = CreateIcon(), ToolTipText = "良子小窗", Menu = menu, IsVisible = true };
                tray.Clicked += (_, _) => home.Restore();
                TrayIcon.SetIcons(this, new TrayIcons { tray });
                library.StartIntegration();
                control = new CancellationTokenSource();
                _ = ControlChannel.ServeAsync(root, HandleControl, control.Token);
                desktop.Exit += (_, _) => { control?.Cancel(); control?.Dispose(); library = null; home.DisposeModules(); tray.Dispose(); instance.Dispose(); };
            }
            catch (Exception ex)
            {
                instance?.Dispose();
                var error = new Window { Title = "良子阅读器 · 启动失败", Width = 520, Height = 220,
                    Content = new TextBlock { Margin = new Thickness(24), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = "无法打开书库。请检查是否已启动阅读器、数据目录权限或进度文件。\n\n" + ex.Message } };
                error.Closed += (_, _) => desktop.Shutdown(1);
                desktop.MainWindow = error;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Runs on a worker thread. Posting rather than awaiting the UI thread matters: the caller is
    // a launcher script with a timeout, and blocking the pipe on a UI round trip turns a slow
    // save into an unanswered stop request. ExitAsync re-checks the same guard, so a race here
    // can only cost us a "busy" answer we did not need to give.
    private Task<string> HandleControl(CancellationToken cancellation)
    {
        MainWindow? target = library;
        if (target == null || !target.CanExit) return Task.FromResult(ControlChannel.Refused);
        Dispatcher.UIThread.Post(target.RequestExit);
        return Task.FromResult(ControlChannel.Accepted);
    }

    private static void Add(NativeMenu menu, string label, Action action)
    {
        var item = new NativeMenuItem(label);
        item.Click += (_, _) => action();
        menu.Add(item);
    }

    private static WindowIcon CreateIcon()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
        {
            int[] pixels = new int[32 * 32];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++) pixels[y * 32 + x] = y is >= 9 and <= 22 && x is >= 7 and <= 24 ? unchecked((int)0xffeeeeee) : unchecked((int)0xff295b46);
            for (int y = 0; y < 32; y++) System.Runtime.InteropServices.Marshal.Copy(pixels, y * 32, buffer.Address + y * buffer.RowBytes, 32);
        }
        return new WindowIcon(bitmap);
    }
}
