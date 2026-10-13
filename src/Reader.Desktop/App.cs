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
                instance = new FileStream(Path.Combine(root, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (Program.DataDirectory == null) DataPaths.MigrateLegacy(Path.Combine(AppContext.BaseDirectory, "data"), root);
                var store = new Store(root);
                var library = new MainWindow(store);
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
                desktop.Exit += (_, _) => { home.DisposeModules(); tray.Dispose(); instance.Dispose(); };
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
