using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Reader.Desktop;

// 应用图标。位图来自嵌入资源（由 build/make-icon.py 生成），托盘、窗口标题栏和「关于」共用同一份。
/// <summary>
/// 应用图标提供器：从嵌入资源加载位图，并缓存窗口图标实例供各窗口复用。
/// </summary>
/// <remarks>位图采用线程安全的懒加载，首次访问后一直驻留，调用方无需释放。</remarks>
internal static class AppIcon
{
    private static readonly Lazy<Bitmap> bitmap = new(Load);

    /// <summary>「关于」窗口展示用的 PNG 位图。</summary>
    public static Bitmap Image => bitmap.Value;
    private static readonly Lazy<WindowIcon> windowIcon = new(() => new WindowIcon(bitmap.Value));
    /// <summary>窗口标题栏与托盘共用的窗口图标。</summary>
    public static WindowIcon Shared => windowIcon.Value;

    // 资源名由 csproj 的嵌入资源逻辑名固定；缺失说明打包不完整，直接失败比显示空图标更易排查。
    private static Bitmap Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Reader.Desktop.icon.png")
            ?? throw new InvalidOperationException("缺少应用图标资源。");
        return new Bitmap(stream);
    }
}
