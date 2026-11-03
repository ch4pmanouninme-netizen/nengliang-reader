using System.Runtime.InteropServices;

namespace Reader.Platform.Windows;

/// <summary>
/// 读取屏幕指定区域的像素，供阅读条「自动融入背景」取色。1×1 即只取一个点。
/// </summary>
/// <remarks>
/// 用 GDI 的 BitBlt 直接读屏幕像素：不需要任何系统授权，也不会触发录屏提示。
/// 注意它抓到的是「合成后的画面」，抓不到本程序自己窗口正下方的像素 —— 调用方
/// 应采样阅读条上、下沿外侧紧贴的那个点，而不是阅读条自身覆盖的区域。
/// </remarks>
public static class ScreenSampler
{
    /// <summary>读取屏幕矩形的平均颜色（1×1 时就是该像素本身的颜色）。</summary>
    /// <param name="x">屏幕坐标（物理像素）左边界。</param>
    /// <param name="y">屏幕坐标（物理像素）上边界。</param>
    /// <param name="width">宽度（物理像素），至少 1。</param>
    /// <param name="height">高度（物理像素），至少 1。</param>
    /// <param name="red">红色分量；失败时为 0。</param>
    /// <param name="green">绿色分量；失败时为 0。</param>
    /// <param name="blue">蓝色分量；失败时为 0。</param>
    /// <returns>是否成功取到像素。区域完全在屏幕外、或不是 Windows 时返回 false。</returns>
    public static bool TrySampleColor(int x, int y, int width, int height, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (!OperatingSystem.IsWindows() || width < 1 || height < 1) return false;
        // 多显示器时虚拟屏幕可能从负坐标开始，按虚拟屏幕范围裁剪。
        int virtualX = GetSystemMetrics(SM_XVIRTUALSCREEN), virtualY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int left = Math.Max(x, virtualX);
        int top = Math.Max(y, virtualY);
        int right = Math.Min(x + width, virtualX + GetSystemMetrics(SM_CXVIRTUALSCREEN));
        int bottom = Math.Min(y + height, virtualY + GetSystemMetrics(SM_CYVIRTUALSCREEN));
        int sampleWidth = right - left, sampleHeight = bottom - top;
        if (sampleWidth < 1 || sampleHeight < 1) return false;

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, sampleWidth, sampleHeight);
        try
        {
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) return false;
            IntPtr previous = SelectObject(memory, bitmap);
            try
            {
                if (!BitBlt(memory, 0, 0, sampleWidth, sampleHeight, screen, left, top, SRCCOPY)) return false;
                // 负高度表示自上而下的行序，取到的字节顺序就是屏幕上的顺序。
                var info = new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = 40, Width = sampleWidth, Height = -sampleHeight, Planes = 1, BitCount = 32, Compression = 0
                    }
                };
                byte[] pixels = new byte[sampleWidth * sampleHeight * 4];
                if (GetDIBits(memory, bitmap, 0, (uint)sampleHeight, pixels, ref info, 0) == 0) return false;
                long totalRed = 0, totalGreen = 0, totalBlue = 0;
                int count = 0;
                // 整片矩形不需要逐像素，按固定步长抽样足够代表平均颜色。
                int stepX = Math.Max(1, sampleWidth / 64), stepY = Math.Max(1, sampleHeight / 16);
                for (int row = 0; row < sampleHeight; row += stepY)
                {
                    for (int column = 0; column < sampleWidth; column += stepX)
                    {
                        int index = (row * sampleWidth + column) * 4;
                        // GetDIBits 返回 BGRA。
                        totalBlue += pixels[index];
                        totalGreen += pixels[index + 1];
                        totalRed += pixels[index + 2];
                        count++;
                    }
                }
                if (count == 0) return false;
                red = (byte)(totalRed / count);
                green = (byte)(totalGreen / count);
                blue = (byte)(totalBlue / count);
                return true;
            }
            finally { SelectObject(memory, previous); }
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);
}
