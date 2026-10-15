using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Reader.Desktop;

// 应用图标。位图来自嵌入资源（由 build/make-icon.py 生成），托盘、窗口标题栏和「关于」共用同一份。
internal static class AppIcon
{
    private static readonly Lazy<Bitmap> bitmap = new(Load);

    public static Bitmap Image => bitmap.Value;
    private static readonly Lazy<WindowIcon> windowIcon = new(() => new WindowIcon(bitmap.Value));
    public static WindowIcon Shared => windowIcon.Value;

    private static Bitmap Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Reader.Desktop.icon.png")
            ?? throw new InvalidOperationException("缺少应用图标资源。");
        return new Bitmap(stream);
    }
}
