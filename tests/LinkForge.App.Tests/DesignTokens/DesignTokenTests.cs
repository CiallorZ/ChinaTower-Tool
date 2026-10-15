using System.Windows.Media;

namespace LinkForge.App.Tests.DesignTokens;

/// <summary>
/// 设计令牌契约测试。
/// </summary>
/// <remarks>
/// 依据 docs/LinkForge_UI_Design_Guidelines_V1.1.md §2.2 / §7 与 docs/DECISIONS.md ADR-014 / ADR-015。
/// 这些测试读取 <c>src/LinkForge.App/Themes/Tokens</c> 下的真实源文件，
/// 保证"浅色与深色暴露同一组令牌键"与"对比度达标"不是人工承诺，而是可自动复验的事实。
/// </remarks>
public class DesignTokenTests
{
    private static readonly string[] RequiredColorKeys =
    [
        // 表面
        "LfColorBackground",
        "LfColorSurface",
        "LfColorSurfaceSubtle",
        "LfColorSurfaceRaised",
        "LfColorSurfaceDisabled",
        "LfColorOverlay",
        "LfColorShadow",

        // 文本
        "LfColorTextPrimary",
        "LfColorTextSecondary",
        "LfColorTextMuted",
        "LfColorTextDisabled",
        "LfColorTextOnAccent",

        // 边界
        "LfColorBorder",
        "LfColorBorderStrong",
        "LfColorDivider",

        // 强调与焦点
        "LfColorAccent",
        "LfColorAccentHover",
        "LfColorAccentPressed",
        "LfColorAccentSubtle",
        "LfColorFocus",

        // 语义状态
        "LfColorSuccess",
        "LfColorSuccessSubtle",
        "LfColorRunning",
        "LfColorRunningSubtle",
        "LfColorWarning",
        "LfColorWarningSubtle",
        "LfColorDanger",
        "LfColorDangerSubtle",
        "LfColorUnknown",
        "LfColorUnknownSubtle",
        "LfColorInfo",
        "LfColorInfoSubtle",
        "LfColorDependencyMissing",
        "LfColorDependencyMissingSubtle"
    ];

    private static readonly string[] SurfaceKeys =
    [
        "LfColorBackground",
        "LfColorSurface",
        "LfColorSurfaceSubtle",
        "LfColorSurfaceRaised"
    ];

    private static readonly string[] StateKeys =
    [
        "LfColorSuccess",
        "LfColorRunning",
        "LfColorWarning",
        "LfColorDanger",
        "LfColorUnknown",
        "LfColorInfo",
        "LfColorDependencyMissing"
    ];

    [Fact]
    public void Light_and_dark_palettes_should_expose_identical_keys()
    {
        var (light, dark) = StaRunner.Run(() =>
        {
            var lightPalette = PaletteFile.LoadLight();
            var darkPalette = PaletteFile.LoadDark();
            return (lightPalette.AllKeys, darkPalette.AllKeys);
        });

        var missingInDark = light.Except(dark, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var missingInLight = dark.Except(light, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();

        missingInDark.ShouldBeEmpty($"深色调色板缺少令牌：{string.Join(", ", missingInDark)}");
        missingInLight.ShouldBeEmpty($"浅色调色板缺少令牌：{string.Join(", ", missingInLight)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Palette_should_define_every_required_color_token(bool light)
    {
        var keys = StaRunner.Run(() => (light ? PaletteFile.LoadLight() : PaletteFile.LoadDark()).ColorKeys);

        var missing = RequiredColorKeys.Except(keys, StringComparer.Ordinal).ToList();

        missing.ShouldBeEmpty(
            $"{(light ? "浅色" : "深色")}调色板缺少必需令牌：{string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_color_token_should_have_a_matching_brush(bool light)
    {
        var (colors, brushes) = StaRunner.Run(() =>
        {
            var palette = light ? PaletteFile.LoadLight() : PaletteFile.LoadDark();
            return (palette.ColorKeys, palette.BrushKeys);
        });

        var missing = colors
            .Select(colorKey => "LfBrush" + colorKey["LfColor".Length..])
            .Where(brushKey => !brushes.Contains(brushKey, StringComparer.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(
            $"{(light ? "浅色" : "深色")}调色板缺少配套画刷：{string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Brush_should_match_its_color_token(bool light)
    {
        var mismatches = StaRunner.Run(() =>
        {
            var palette = light ? PaletteFile.LoadLight() : PaletteFile.LoadDark();

            return palette.ColorKeys
                .Select(colorKey => new
                {
                    ColorKey = colorKey,
                    BrushKey = "LfBrush" + colorKey["LfColor".Length..],
                    Color = palette.Color(colorKey),
                    Brush = palette.Brush("LfBrush" + colorKey["LfColor".Length..])
                })
                .Where(pair => pair.Brush.Color != pair.Color)
                .Select(pair => $"{pair.BrushKey}({pair.Brush.Color}) != {pair.ColorKey}({pair.Color})")
                .ToList();
        });

        mismatches.ShouldBeEmpty(
            $"{(light ? "浅色" : "深色")}调色板中画刷与颜色不一致：{string.Join("; ", mismatches)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Primary_and_secondary_text_should_meet_wcag_aa_on_every_surface(bool light)
    {
        var failures = StaRunner.Run(() => CheckTextContrast(light, ["LfColorTextPrimary", "LfColorTextSecondary"], Contrast.TextMinimum));

        failures.ShouldBeEmpty(
            $"{(light ? "浅色" : "深色")}主题正文文本对比度不足（要求 >= {Contrast.TextMinimum}）：{string.Join("; ", failures)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Muted_text_should_remain_legible_but_is_not_for_key_data(bool light)
    {
        var failures = StaRunner.Run(() => CheckTextContrast(light, ["LfColorTextMuted"], Contrast.MutedMinimum));

        failures.ShouldBeEmpty(
            $"{(light ? "浅色" : "深色")}主题辅助文本对比度不足（要求 >= {Contrast.MutedMinimum}）：{string.Join("; ", failures)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Interactive_borders_should_meet_non_text_contrast(bool light)
    {
        var failures = StaRunner.Run(() => CheckTextContrast(light, ["LfColorBorderStrong"], Contrast.NonTextMinimum));

        failures.ShouldBeEmpty(
            $"{(light ? "浅色" : "深色")}主题交互边界对比度不足（要求 >= {Contrast.NonTextMinimum}，WCAG 1.4.11）：{string.Join("; ", failures)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Accent_and_focus_should_meet_contrast_requirements(bool light)
    {
        var failures = StaRunner.Run(() =>
        {
            var palette = light ? PaletteFile.LoadLight() : PaletteFile.LoadDark();
            var results = new List<string>();

            foreach (var surfaceKey in SurfaceKeys)
            {
                results.AddRange(Check(palette, "LfColorAccent", surfaceKey, Contrast.TextMinimum));
                results.AddRange(Check(palette, "LfColorFocus", surfaceKey, Contrast.NonTextMinimum));
            }

            results.AddRange(Check(palette, "LfColorTextOnAccent", "LfColorAccent", Contrast.TextMinimum));
            results.AddRange(Check(palette, "LfColorTextOnAccent", "LfColorAccentHover", Contrast.TextMinimum));
            results.AddRange(Check(palette, "LfColorAccent", "LfColorAccentSubtle", Contrast.TextMinimum));

            return results;
        });

        failures.ShouldBeEmpty($"{(light ? "浅色" : "深色")}主题强调色/焦点对比度不足：{string.Join("; ", failures)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Semantic_state_colors_should_be_readable_on_all_surfaces_and_own_subtle(bool light)
    {
        var failures = StaRunner.Run(() =>
        {
            var palette = light ? PaletteFile.LoadLight() : PaletteFile.LoadDark();
            var results = new List<string>();

            foreach (var stateKey in StateKeys)
            {
                foreach (var surfaceKey in SurfaceKeys)
                {
                    results.AddRange(Check(palette, stateKey, surfaceKey, Contrast.TextMinimum));
                }

                var subtleKey = stateKey + "Subtle";
                results.AddRange(Check(palette, stateKey, subtleKey, Contrast.TextMinimum));

                // 状态底色必须与表面有可辨识差异，否则状态标签在视觉上"消失"。
                var ratio = Contrast.Ratio(palette.Color(subtleKey), palette.Color("LfColorSurface"));
                if (ratio < Contrast.SubtleSurfaceMinimum)
                {
                    results.Add(
                        $"{subtleKey} vs LfColorSurface 差异不足（{ratio:F2} < {Contrast.SubtleSurfaceMinimum}）");
                }
            }

            return results;
        });

        failures.ShouldBeEmpty($"{(light ? "浅色" : "深色")}主题状态色对比度不足：{string.Join("; ", failures)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Structural_dividers_should_remain_visible(bool light)
    {
        var failures = StaRunner.Run(() =>
        {
            var palette = light ? PaletteFile.LoadLight() : PaletteFile.LoadDark();
            var results = new List<string>();

            foreach (var surfaceKey in SurfaceKeys)
            {
                results.AddRange(Check(palette, "LfColorBorder", surfaceKey, Contrast.DividerMinimum));
                results.AddRange(Check(palette, "LfColorDivider", surfaceKey, Contrast.DividerMinimum));
            }

            return results;
        });

        failures.ShouldBeEmpty($"{(light ? "浅色" : "深色")}主题分隔线可见性不足：{string.Join("; ", failures)}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Raised_surface_should_be_distinguishable_or_use_shadow_layer(bool light)
    {
        var (raised, surface) = StaRunner.Run(() =>
        {
            var palette = light ? PaletteFile.LoadLight() : PaletteFile.LoadDark();
            return (palette.Color("LfColorSurfaceRaised"), palette.Color("LfColorSurface"));
        });

        // 浅色主题下 Raised 与 Surface 同色，层级由阴影表达（规范 §2.1 克制阴影）；
        // 一旦两者取值不同，则必须达到可辨识差异，避免"看起来是同一层但实际不同"。
        if (raised != surface)
        {
            var ratio = Contrast.Ratio(raised, surface);
            ratio.ShouldBeGreaterThanOrEqualTo(
                1.05,
                $"SurfaceRaised 与 Surface 取值不同但差异不足（{ratio:F2}）。");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Surfaces_should_be_distinguishable_from_background(bool light)
    {
        var failures = StaRunner.Run(() =>
        {
            var palette = light ? PaletteFile.LoadLight() : PaletteFile.LoadDark();
            var results = new List<string>();

            foreach (var key in new[] { "LfColorSurface", "LfColorSurfaceSubtle" })
            {
                var ratio = Contrast.Ratio(palette.Color(key), palette.Color("LfColorBackground"));
                if (ratio < 1.03)
                {
                    results.Add($"{key} vs LfColorBackground 差异不足（{ratio:F3} < 1.03）");
                }
            }

            return results;
        });

        failures.ShouldBeEmpty($"{(light ? "浅色" : "深色")}主题表面层级不可辨：{string.Join("; ", failures)}");
    }

    private static List<string> CheckTextContrast(bool light, string[] foregroundKeys, double minimum)
    {
        var palette = light ? PaletteFile.LoadLight() : PaletteFile.LoadDark();
        var results = new List<string>();

        foreach (var foregroundKey in foregroundKeys)
        {
            foreach (var surfaceKey in SurfaceKeys)
            {
                results.AddRange(Check(palette, foregroundKey, surfaceKey, minimum));
            }
        }

        return results;
    }

    private static IEnumerable<string> Check(
        PaletteFile palette,
        string foregroundKey,
        string backgroundKey,
        double minimum)
    {
        var ratio = Contrast.Ratio(palette.Color(foregroundKey), palette.Color(backgroundKey));

        if (ratio < minimum)
        {
            yield return $"{foregroundKey} on {backgroundKey} = {ratio:F2} < {minimum}";
        }
    }
}
