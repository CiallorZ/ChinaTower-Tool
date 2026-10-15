using System.Windows.Media;

namespace LinkForge.App.Tests.DesignTokens;

/// <summary>
/// WCAG 2.1 相对亮度与对比度计算。
/// </summary>
internal static class Contrast
{
    /// <summary>正文文本的最小对比度（WCAG 2.1 AA，普通字号）。</summary>
    internal const double TextMinimum = 4.5;

    /// <summary>辅助说明文本的最小对比度；规范 §2.2 禁止将其用于关键数据。</summary>
    internal const double MutedMinimum = 3.0;

    /// <summary>交互控件边界的最小对比度（WCAG 2.1 非文本对比 1.4.11）。</summary>
    internal const double NonTextMinimum = 3.0;

    /// <summary>状态 Subtle 背景相对表面的最小可辨识差异。</summary>
    internal const double SubtleSurfaceMinimum = 1.10;

    /// <summary>结构性分隔线的最小可辨识差异。</summary>
    internal const double DividerMinimum = 1.10;

    internal static double Ratio(Color foreground, Color background)
    {
        var foregroundLuminance = RelativeLuminance(foreground);
        var backgroundLuminance = RelativeLuminance(background);

        var lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        var darker = Math.Min(foregroundLuminance, backgroundLuminance);

        return (lighter + 0.05) / (darker + 0.05);
    }

    internal static double RelativeLuminance(Color color) =>
        (0.2126 * Channel(color.R)) +
        (0.7152 * Channel(color.G)) +
        (0.0722 * Channel(color.B));

    private static double Channel(byte value)
    {
        var normalized = value / 255.0;

        return normalized <= 0.04045
            ? normalized / 12.92
            : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }
}
