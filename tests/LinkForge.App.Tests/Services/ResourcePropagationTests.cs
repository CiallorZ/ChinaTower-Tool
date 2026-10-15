using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;

namespace LinkForge.App.Tests.Services;

/// <summary>
/// 验证主题切换能真正传播到已构造的可视元素。
/// </summary>
/// <remarks>
/// 背景（真实缺陷，2026-10-15 运行时截图发现）：
/// 视图若用 <c>{StaticResource LfBrush*}</c> 引用令牌，WPF 只在 XAML 解析时求值一次；
/// <see cref="ThemeManager"/> 替换调色板字典后，这些元素仍持有旧画刷，
/// 表现为"部分区域变色、部分仍是旧主题"（深色下头部保持白色、按钮文字不可见）。
///
/// 结论：主题相关令牌必须用 <c>DynamicResource</c> 引用。本测试锁定该机制，
/// 防止后续页面改回 StaticResource 而无人发现。
/// </remarks>
public class ResourcePropagationTests
{
    [Fact]
    public void DynamicResource_reference_should_follow_palette_swap()
    {
        var (lightColor, darkColor) = StaRunner.Run(() =>
        {
            // DynamicResource 的查找需要元素处于可解析资源的树中，
            // 因此用 Window 作为宿主，而不是孤立的 Border。
            var window = CreateWindowWithPalette();
            var manager = new ThemeManager(window.Resources);
            manager.ApplyResolved(LfResolvedTheme.Light);

            var border = new Border();
            window.Content = border;
            // 等价于 XAML 的 Background="{DynamicResource LfBrushSurface}"。
            border.SetResourceReference(Border.BackgroundProperty, "LfBrushSurface");
            var before = ((SolidColorBrush)border.Background).Color;

            manager.ApplyResolved(LfResolvedTheme.Dark);
            var after = ((SolidColorBrush)border.Background).Color;

            return (before, after);
        });

        darkColor.ShouldNotBe(
            lightColor,
            "DynamicResource 引用在调色板替换后必须跟随变化，否则主题切换对用户不可见。");
    }

    [Fact]
    public void StaticResource_reference_should_not_follow_palette_swap()
    {
        var (before, after) = StaRunner.Run(() =>
        {
            var window = CreateWindowWithPalette();
            var manager = new ThemeManager(window.Resources);
            manager.ApplyResolved(LfResolvedTheme.Light);

            // 直接取当前画刷实例，模拟 StaticResource 的"解析一次并持有"语义。
            var border = new Border
            {
                Background = (SolidColorBrush)window.Resources["LfBrushSurface"]
            };
            var captured = ((SolidColorBrush)border.Background).Color;

            manager.ApplyResolved(LfResolvedTheme.Dark);
            var unchanged = ((SolidColorBrush)border.Background).Color;

            return (captured, unchanged);
        });

        // 这个测试记录的是 WPF 的既定语义，不是期望行为：
        // 它解释"为什么视图必须用 DynamicResource"，若哪天不再成立，说明方案可以简化。
        after.ShouldBe(
            before,
            "StaticResource 语义应保持'解析一次'；若此断言失败，说明 WPF 行为已变化，" +
            "LinkForge 可重新评估是否仍需 DynamicResource。");
    }

    /// <summary>
    /// 构造与 App.xaml 等价的资源宿主：Window -> 调色板字典。
    /// </summary>
    private static Window CreateWindowWithPalette()
    {
        var window = new Window();
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(PaletteFile.LightUri, UriKind.Absolute)
        });

        return window;
    }
}
