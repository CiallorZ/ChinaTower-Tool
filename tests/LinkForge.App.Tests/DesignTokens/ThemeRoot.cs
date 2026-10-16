using System.Windows;

namespace LinkForge.App.Tests.DesignTokens;

/// <summary>
/// 构造与生产等价的主题资源根字典。
/// </summary>
/// <remarks>
/// <para>
/// 形状必须与 <c>App.xaml</c> -> <c>ThemeResources.xaml</c> -> <c>Palette.*.xaml</c>
/// 的嵌套结构一致：<see cref="ThemeManager"/> 是<b>递归</b>查找调色板字典的，
/// 若测试把调色板平铺在根字典下，就会掩盖"递归查找写错"的缺陷。
/// </para>
/// <para>
/// 使用与生产相同的 pack URI，保证加载的是编译进程序集的 BAML 而不是磁盘副本。
/// </para>
/// </remarks>
internal static class ThemeRoot
{
    /// <summary>创建以浅色调色板起始的主题资源根。</summary>
    internal static ResourceDictionary Create()
    {
        var themeResources = new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/LinkForge.App;component/Themes/ThemeResources.xaml",
                UriKind.Absolute)
        };

        var root = new ResourceDictionary();
        root.MergedDictionaries.Add(themeResources);

        return root;
    }
}
