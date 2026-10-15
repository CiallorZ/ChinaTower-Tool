using System.IO;
using System.Windows;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 与 App.xaml 等价的 WPF 资源宿主。
/// </summary>
/// <remarks>
/// 资源树的形状必须与生产完全一致，否则测试会给出假阳性。
///
/// 曾经踩过的坑：本文件把 LinkForge 令牌字典直接挂在应用资源下（扁平结构），
/// 而生产是通过 Themes/ThemeResources.xaml 这层嵌套字典来合并的。
/// 两种结构下控件模板的 StaticResource 解析上下文不同，
/// 结果是"测试全绿、应用启动即崩"。
/// 现在只合并 ThemeResources.xaml 这一个入口，与 App.xaml 逐字对齐。
///
/// 合并顺序与 App.xaml 保持一致：WPF-UI 主题 -> WPF-UI 控件 -> LinkForge 令牌。
/// 顺序颠倒会让控件模板解析不到令牌。
/// </remarks>
internal static class ThemedHost
{
    private static readonly string[] LinkForgeSources =
    [
        // 只引用 ThemeResources.xaml 这一个入口，与 App.xaml 逐字对齐。
        // 不要在这里展开成 Dimensions/Palette 的扁平列表：
        // 扁平结构会让模板的 StaticResource 走通，从而掩盖生产环境真实存在的缺陷。
        "pack://application:,,,/LinkForge.App;component/Themes/ThemeResources.xaml"
    ];

    /// <summary>
    /// 确保应用级资源已按 App.xaml 的结构合并（只执行一次）。
    /// </summary>
    internal static void EnsureResources()
    {
        var resources = Application.Current.Resources;

        if (resources.MergedDictionaries.Count > 0)
        {
            return;
        }

        resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        {
            Theme = Wpf.Ui.Appearance.ApplicationTheme.Light
        });
        resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

        foreach (var source in LinkForgeSources)
        {
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(source, UriKind.Absolute)
            });
        }
    }

    /// <summary>
    /// 创建宿主窗口并把控件挂上、应用模板。
    /// </summary>
    /// <param name="control">待承载的控件。</param>
    /// <returns>宿主窗口。</returns>
    internal static Window Host(UIElement control)
    {
        EnsureResources();

        var window = new Window { Content = control };

        if (control is FrameworkElement element)
        {
            element.ApplyTemplate();
        }

        return window;
    }

    /// <summary>
    /// 把应用级资源中的调色板替换为指定主题。
    /// </summary>
    /// <param name="paletteName">"Light" 或 "Dark"。</param>
    internal static void UsePalette(string paletteName)
    {
        EnsureResources();

        // 必须递归：调色板字典藏在 ThemeResources.xaml 的 MergedDictionaries 里，
        // 只在顶层扫一遍会找不到它，主题切换也就静默失效了。
        ReplacePalette(Application.Current.Resources, paletteName);
    }

    private static void ReplacePalette(ResourceDictionary parent, string paletteName)
    {
        foreach (var merged in parent.MergedDictionaries)
        {
            if (merged.Source?.OriginalString.Contains(
                    "Themes/Tokens/Palette.", StringComparison.OrdinalIgnoreCase) == true)
            {
                merged.Source = new Uri(
                    $"pack://application:,,,/LinkForge.App;component/Themes/Tokens/Palette.{paletteName}.xaml",
                    UriKind.Absolute);
                continue;
            }

            ReplacePalette(merged, paletteName);
        }
    }
}
