using System.Windows;
using System.Windows.Media;
using LinkForge.App.Tests.DesignTokens;

namespace LinkForge.App.Tests.DesignTokens;

/// <summary>
/// WPF-UI 语义调色板映射测试。
/// </summary>
/// <remarks>
/// 依据 ADR-015 / ADR-018。这些测试防止两类静默失效：
/// <list type="number">
///   <item><b>映射到不存在的键</b>：写错键名不会报错，只是没有任何效果，
///         表现为"某些控件在深色主题下仍是浅色"。</item>
///   <item><b>漏映射关键键</b>：控件库升级后新增键，若未补充映射，
///         对应控件会回退到控件库自带配色，与设计令牌不一致。</item>
/// </list>
/// 因此这里以 WPF-UI 实际加载的资源字典为准做校验，而不是比对文本。
/// </remarks>
public class WpfUiMappingTests
{
    /// <summary>
    /// LinkForge 必须覆盖的 WPF-UI 语义调色板键。
    /// </summary>
    /// <remarks>
    /// 该清单对应"控件外观的语义来源"：表面、文本、强调、控件填充/描边、焦点、
    /// 输入控件、滚动条、系统状态色。新增控件类型时若用到新的语义键，在此追加。
    /// </remarks>
    private static readonly string[] RequiredMappingKeys =
    [
        // 背景与表面
        "ApplicationBackgroundBrush",
        "SolidBackgroundFillColorBaseBrush",
        "SolidBackgroundFillColorSecondaryBrush",
        "SolidBackgroundFillColorTertiaryBrush",
        "SolidBackgroundFillColorQuarternaryBrush",
        "CardBackgroundFillColorDefaultBrush",
        "CardBackgroundFillColorSecondaryBrush",
        "CardStrokeColorDefaultBrush",
        "LayerFillColorDefaultBrush",
        "LayerFillColorAltBrush",
        "LayerOnAcrylicFillColorDefaultBrush",

        // 文本
        "TextFillColorPrimaryBrush",
        "TextFillColorSecondaryBrush",
        "TextFillColorTertiaryBrush",
        "TextFillColorDisabledBrush",
        "TextFillColorInverseBrush",

        // 强调色
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentFillColorDisabled",
        "TextOnAccentFillColorPrimaryBrush",
        "TextOnAccentFillColorSecondaryBrush",
        "TextOnAccentFillColorDisabledBrush",

        // 控件填充与描边
        "ControlFillColorDefaultBrush",
        "ControlFillColorSecondaryBrush",
        "ControlFillColorTertiaryBrush",
        "ControlFillColorDisabledBrush",
        "ControlFillColorInputActiveBrush",
        "ControlStrokeColorDefaultBrush",
        "ControlStrokeColorSecondaryBrush",
        "ControlStrongFillColorDefaultBrush",
        "ControlStrongStrokeColorDefaultBrush",

        // 分隔线与焦点环
        "DividerStrokeColorDefaultBrush",
        "FocusStrokeColorOuterBrush",
        "FocusStrokeColorInnerBrush",

        // 低强度填充
        "SubtleFillColorSecondaryBrush",
        "SubtleFillColorTertiaryBrush",

        // 输入控件
        "TextControlForeground",
        "TextControlBackground",
        "TextControlBackgroundPointerOver",
        "TextControlBackgroundFocused",
        "TextControlBackgroundDisabled",
        "TextControlForegroundDisabled",
        "TextControlBorderBrushDisabled",
        "TextControlPlaceholderForeground",

        // 滚动条
        "ScrollBarThumbFill",

        // 系统状态色
        "SystemFillColorSuccessBrush",
        "SystemFillColorSuccessBackgroundBrush",
        "SystemFillColorCautionBrush",
        "SystemFillColorCautionBackgroundBrush",
        "SystemFillColorCriticalBrush",
        "SystemFillColorCriticalBackgroundBrush",
        "SystemFillColorAttentionBrush",
        "SystemFillColorAttentionBackgroundBrush",
        "SystemFillColorNeutralBrush",
        "SystemFillColorNeutralBackgroundBrush",
        "InfoBarSuccessSeverityBackgroundBrush",
        "InfoBarWarningSeverityBackgroundBrush",
        "InfoBarErrorSeverityBackgroundBrush",
        "InfoBarInformationalSeverityBackgroundBrush",

        // 阶段 01 已映射的按钮键（保留，防止回退）
        "ButtonBackground",
        "ButtonBackgroundPointerOver",
        "ButtonBackgroundPressed",
        "ButtonBackgroundDisabled",
        "ButtonForeground",
        "ButtonForegroundPointerOver",
        "ButtonForegroundPressed",
        "ButtonForegroundDisabled",
        "ButtonBorderBrushPressed",
        "ButtonBorderBrushDisabled",
        "AccentButtonBackground",
        "AccentButtonBackgroundPointerOver",
        "AccentButtonBackgroundPressed",
        "AccentButtonForeground",
        "AccentButtonForegroundPointerOver",
        "AccentButtonForegroundPressed",
        "AccentButtonBorderBrushPressed"
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinkForge_palette_should_define_every_required_wpfui_mapping_key(bool light)
    {
        var keys = StaRunner.Run(() =>
            (light ? PaletteFile.LoadLight() : PaletteFile.LoadDark()).AllKeys);

        var missing = RequiredMappingKeys
            .Except(keys, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(
            $"{(light ? "浅色" : "深色")}调色板缺少 WPF-UI 映射键：" + string.Join(", ", missing));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_mapped_key_should_exist_in_wpfui(bool light)
    {
        // 映射到不存在的键不会报错，只会静默失效——因此必须显式校验。
        var unknown = StaRunner.Run(() =>
        {
            var wpfUiKeys = WpfUiResources.EnumerateKeys();
            var paletteKeys = (light ? PaletteFile.LoadLight() : PaletteFile.LoadDark()).AllKeys;

            return paletteKeys
                .Where(key => !key.StartsWith("Lf", StringComparison.Ordinal))
                .Where(key => !wpfUiKeys.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
        });

        unknown.ShouldBeEmpty(
            "以下键在 LinkForge 调色板中定义，但 WPF-UI 并不提供同名键（映射无效）：" +
            string.Join(", ", unknown));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_required_mapping_key_should_be_a_brush_or_color(bool light)
    {
        var wrongTypes = StaRunner.Run(() =>
        {
            var palette = light ? PaletteFile.LoadLight() : PaletteFile.LoadDark();

            return RequiredMappingKeys
                .Where(key =>
                {
                    var value = palette.RawValue(key);
                    return value is not SolidColorBrush and not Color;
                })
                .Select(key => $"{key}={palette.RawValue(key)?.GetType().Name ?? "null"}")
                .ToList();
        });

        wrongTypes.ShouldBeEmpty(
            "以下映射键的类型不是 SolidColorBrush/Color，控件模板可能无法消费：" +
            string.Join(", ", wrongTypes));
    }
}
