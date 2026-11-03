namespace Reader.Core;

/// <summary>
/// 「自动融入背景」主题的判定：底色直接取阅读框中心处的屏幕像素颜色，文字颜色再按该颜色的明暗决定。
/// </summary>
/// <remarks>
/// 只做判定，不碰界面，方便回归测试。两个阈值之间留出滞回区间 ——
/// 亮度落在 0.42–0.58 时保持原文字颜色，避免文字在深色/浅色之间来回闪。
/// </remarks>
public static class AutoBackground
{
    /// <summary>背景暗于该阈值时用浅色文字。</summary>
    public const double SwitchToLightTextBelow = 0.42;
    /// <summary>背景亮过该阈值时用深色文字。</summary>
    public const double SwitchToDarkTextAbove = 0.58;

    /// <summary>把采样到的颜色换算成亮度（0–1），按 Rec.709 权重。</summary>
    public static double Luminance(byte red, byte green, byte blue) =>
        (0.2126 * red + 0.7152 * green + 0.0722 * blue) / 255.0;

    /// <summary>按背景色亮度决定文字是否应该用浅色。</summary>
    /// <param name="currentlyLightText">当前是否已经是浅色文字。</param>
    /// <param name="backgroundLuminance">背景色的亮度（0–1）。</param>
    /// <returns>判定后是否应该使用浅色文字。</returns>
    public static bool UseLightText(bool currentlyLightText, double backgroundLuminance) =>
        // 已经是浅色文字时只有明显变亮才转深色；深色文字时只有明显变暗才转浅色。
        currentlyLightText ? backgroundLuminance < SwitchToDarkTextAbove : backgroundLuminance < SwitchToLightTextBelow;
}
