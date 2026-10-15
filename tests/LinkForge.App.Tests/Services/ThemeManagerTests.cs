using System.Windows;
using System.Windows.Media;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;

namespace LinkForge.App.Tests.Services;

/// <summary>
/// 主题切换服务测试。
/// </summary>
/// <remarks>
/// 这些测试真实构造 WPF <see cref="ResourceDictionary"/> 并调用 <see cref="ThemeManager"/>，
/// 验证"切换主题后 Lf* 令牌确实变为另一套取值"，而不是只检查枚举映射。
/// </remarks>
public class ThemeManagerTests
{
    [Fact]
    public void Resolve_should_map_explicit_preferences_without_touching_system_theme()
    {
        ThemeManager.Resolve(LfThemePreference.Light).ShouldBe(LfResolvedTheme.Light);
        ThemeManager.Resolve(LfThemePreference.Dark).ShouldBe(LfResolvedTheme.Dark);
    }

    [Fact]
    public void Resolve_follow_system_should_return_a_concrete_theme()
    {
        // 系统主题不可读时必须回退浅色（设计基准），不得抛异常导致应用无法启动。
        var resolved = ThemeManager.Resolve(LfThemePreference.FollowSystem);

        resolved.ShouldBeOneOf(LfResolvedTheme.Light, LfResolvedTheme.Dark);
    }

    [Fact]
    public void ApplyResolved_should_swap_palette_and_update_current_theme()
    {
        var (lightBackground, darkBackground, appliedDark, appliedLight) = StaRunner.Run(() =>
        {
            var root = BuildRootDictionary();
            var manager = new ThemeManager(root);

            manager.ApplyResolved(LfResolvedTheme.Light);
            var lightValue = PaletteValue(root, "LfColorBackground");

            manager.ApplyResolved(LfResolvedTheme.Dark);
            var darkValue = PaletteValue(root, "LfColorBackground");
            var isDark = manager.CurrentTheme;

            manager.ApplyResolved(LfResolvedTheme.Light);
            var backToLight = manager.CurrentTheme;

            return (lightValue, darkValue, isDark, backToLight);
        });

        appliedDark.ShouldBe(LfResolvedTheme.Dark);
        appliedLight.ShouldBe(LfResolvedTheme.Light);
        lightBackground.ShouldNotBe(darkBackground, "浅色与深色的 LfColorBackground 必须不同，否则切换未生效。");
    }

    [Fact]
    public void ApplyResolved_should_keep_all_required_tokens_resolvable_after_switch()
    {
        var failures = StaRunner.Run(() =>
        {
            var root = BuildRootDictionary();
            var manager = new ThemeManager(root);
            var results = new List<string>();

            foreach (var theme in new[] { LfResolvedTheme.Light, LfResolvedTheme.Dark })
            {
                manager.ApplyResolved(theme);

                foreach (var key in RequiredBrushKeys)
                {
                    if (!root.Contains(key))
                    {
                        results.Add($"{theme} 缺少令牌 {key}");
                        continue;
                    }

                    if (root[key] is not SolidColorBrush brush)
                    {
                        results.Add($"{theme} 的 {key} 不是 SolidColorBrush");
                        continue;
                    }

                    // 冻结/未冻结画刷都必须能取到颜色；取不到说明资源引用已损坏。
                    _ = brush.Color;
                }
            }

            return results;
        });

        failures.ShouldBeEmpty(string.Join("; ", failures));
    }

    [Fact]
    public void ThemeManager_should_fail_loudly_when_palette_dictionary_is_absent()
    {
        var exception = StaRunner.Run(() =>
        {
            var root = new ResourceDictionary();
            var manager = new ThemeManager(root);

            return Record.Exception(() => manager.ApplyResolved(LfResolvedTheme.Dark));
        });

        // 静默失败会导致"主题看起来没反应"，因此这里要求显式异常并给出可操作提示。
        exception.ShouldBeOfType<InvalidOperationException>();
        exception!.Message.ShouldContain(ThemeManager.PalettePathPrefix);
    }

    /// <summary>
    /// 页面实际引用的画刷令牌（业务页面禁止直接绑定 Color 令牌）。
    /// </summary>
    private static readonly string[] RequiredBrushKeys =
    [
        "LfBrushBackground",
        "LfBrushSurface",
        "LfBrushTextPrimary",
        "LfBrushTextSecondary",
        "LfBrushBorder",
        "LfBrushBorderStrong",
        "LfBrushAccent",
        "LfBrushTextOnAccent",
        "LfBrushSuccess",
        "LfBrushSuccessSubtle",
        "LfBrushDanger",
        "LfBrushWarning",
        "LfBrushUnknown",
        "LfBrushDependencyMissing",
        "LfBrushFocus"
    ];

    /// <summary>
    /// 构造与 App.xaml 结构等价的资源根：外层 -> ThemeResources -> 调色板。
    /// 使用与生产相同的 pack URI，保证测试对象与运行时一致。
    /// </summary>
    private static ResourceDictionary BuildRootDictionary()
    {
        var themeResources = new ResourceDictionary();
        themeResources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(PaletteFile.LightUri, UriKind.Absolute)
        });

        var root = new ResourceDictionary();
        root.MergedDictionaries.Add(themeResources);

        return root;
    }

    private static Color PaletteValue(ResourceDictionary root, string key)
    {
        var palette = root.MergedDictionaries[0].MergedDictionaries[0];

        return (Color)palette[key];
    }
}
