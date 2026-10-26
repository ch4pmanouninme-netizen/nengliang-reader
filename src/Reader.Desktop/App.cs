using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Reader.Core;

namespace Reader.Desktop;

/// <summary>
/// 应用生命周期宿主：创建主页与书库、占用单实例锁、注册托盘菜单，并托管停止控制通道。
/// </summary>
/// <remarks>
/// 窗口与托盘都在 UI 线程创建；<see cref="HandleControl"/> 由控制通道的工作线程调用，需自行调度回 UI 线程。
/// 退出时统一释放锁、托盘、控制通道和各模块资源，避免残留后台线程。
/// </remarks>
public sealed class App : Application
{
    // 单实例锁文件，进程存活期间保持打开。
    private FileStream? instance;
    // 托盘图标：所有窗口隐藏后仍能恢复或退出。
    private TrayIcon? tray;
    // 书库窗口由应用长期持有；控制通道用它判断当前实例能否退出。
    private MainWindow? library;
    // 控制通道的取消源，退出时停止监听。
    private CancellationTokenSource? control;
    /// <summary>供主题切换写入调色板的 Fluent 主题实例。</summary>
    internal FluentTheme Fluent { get; } = new();
    /// <summary>把 Fluent 主题加入应用样式，供后续切换主题变体。</summary>
    public override void Initialize() => Styles.Add(Fluent);
    /// <summary>
    /// 框架初始化完成后建立主窗口、托盘与控制通道。
    /// </summary>
    /// <remarks>
    /// 服务模式、单实例冲突和启动异常都会各自创建错误/提示窗口，并以非零退出码结束。
    /// </remarks>
    public override void OnFrameworkInitializationCompleted()
    {
        // 窗口隐藏到托盘时不能自动退出，只有显式 Shutdown 才结束进程。
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // 服务模式只需要聊天服务窗口，不创建书库，也不占用书库的单实例锁。
            if (Program.ServerMode)
            {
                try { var server = new ServerWindow(); desktop.MainWindow = server; server.Closed += (_, _) => desktop.Shutdown(); }
                catch (Exception ex)
                {
                    // 服务启动失败也要显示可关闭的错误窗口，不能用静默退出掩盖原因。
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
                    // 以 FileShare.None 持有锁文件：第二个实例打开同一文件时会抛 IOException，从而实现单实例。
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
                // 只有使用默认数据目录时才迁移旧数据；显式 --data 应保持隔离，不掺入历史书库。
                if (Program.DataDirectory == null) DataPaths.MigrateLegacy(DataPaths.ResolveLegacyRoot(Program.LegacyRoot, AppContext.BaseDirectory), root);
                var store = new Store(root);
                library = new MainWindow(store);
                var home = new HomeWindow(store, library);
                // 冒烟测试需要拿到主页实例来驱动整条 UI 链路，正常启动则无需暴露。
                if (Program.SmokeTest) library.SmokeHome = home;
                desktop.MainWindow = Program.SmokeTest ? library : home;
                // 托盘菜单是窗口全部隐藏后仍能恢复/退出的唯一入口。
                var menu = new NativeMenu();
                Add(menu, "主页", home.Restore);
                Add(menu, "隐藏 / 恢复窗口", home.ToggleVisibility);
                Add(menu, "退出", library.RequestExit);
                tray = new TrayIcon { Icon = AppIcon.Shared, ToolTipText = "良子小窗", Menu = menu, IsVisible = true };
                tray.Clicked += (_, _) => home.Restore();
                TrayIcon.SetIcons(this, new TrayIcons { tray });
                library.StartIntegration();
                // 控制通道让 `--stop` 能在不创建第二个实例的前提下请求保存并退出。
                control = new CancellationTokenSource();
                _ = ControlChannel.ServeAsync(root, HandleControl, control.Token);
                // 退出顺序：先停控制通道，再关模块、托盘和实例锁；dispose 回调本身只执行一次。
                desktop.Exit += (_, _) => { control?.Cancel(); control?.Dispose(); library = null; home.DisposeModules(); tray.Dispose(); instance.Dispose(); };
            }
            catch (Exception ex)
            {
                // 启动中途失败时先释放已取得的实例锁，再显示错误窗口并延迟退出。
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

    // NativeMenu 只能由原生菜单项组成，这里统一创建并转发到动作。
    private static void Add(NativeMenu menu, string label, Action action)
    {
        var item = new NativeMenuItem(label);
        item.Click += (_, _) => action();
        menu.Add(item);
    }

}
