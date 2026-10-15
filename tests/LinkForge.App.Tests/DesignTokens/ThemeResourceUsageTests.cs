using System.IO;
using System.Text.RegularExpressions;

namespace LinkForge.App.Tests.DesignTokens;

/// <summary>
/// 主题资源引用方式守卫。
/// </summary>
/// <remarks>
/// 真实缺陷记录（2026-10-15，运行时截图发现）：
/// 视图用 <c>{StaticResource LfBrush*}</c> 引用颜色令牌时，深色主题只切换了一部分元素，
/// 头部与卡片保持浅色、按钮文字在深色背景上不可见。
///
/// 原因：StaticResource 只在解析时求值一次，<c>ThemeManager</c> 替换调色板后不会重新解析。
/// 因此颜色/画刷类令牌（<c>LfBrush*</c>）必须用 <c>DynamicResource</c> 引用。
///
/// 尺寸/间距/字号类令牌（<c>LfSpacing*</c>、<c>LfFontSize*</c> 等）不随主题变化，
/// 保持 StaticResource 是正确且更高效的，不在本守卫范围内。
/// </remarks>
public class ThemeResourceUsageTests
{
    private static readonly Regex StaticBrushReference = new(
        @"\{\s*StaticResource\s+(LfBrush[A-Za-z0-9_]*)\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Color_tokens_must_be_referenced_as_DynamicResource_in_views()
    {
        var appDirectory = LocateAppSourceDirectory();
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(appDirectory, file).Replace('\\', '/');

            // 调色板文件本身以 StaticResource 把 Color 组装为 SolidColorBrush，
            // 这发生在同一字典内、且整个字典会被替换，因此是允许的。
            if (relative.StartsWith("Themes/Tokens/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match match in StaticBrushReference.Matches(File.ReadAllText(file)))
            {
                violations.Add($"{relative}: {{StaticResource {match.Groups[1].Value}}}");
            }
        }

        violations.ShouldBeEmpty(
            "以下位置用 StaticResource 引用了主题颜色令牌，切换主题时不会更新；" +
            "请改为 DynamicResource：" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 从测试输出目录向上定位 <c>src/LinkForge.App</c>，直接扫描源文件而非构建产物。
    /// </summary>
    private static string LocateAppSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "LinkForge.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"未能从 {AppContext.BaseDirectory} 向上定位 src/LinkForge.App 目录。");
    }
}
