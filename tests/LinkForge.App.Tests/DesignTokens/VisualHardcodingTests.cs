using System.IO;
using System.Text.RegularExpressions;

namespace LinkForge.App.Tests.DesignTokens;

/// <summary>
/// 静态守卫：视图与控件模板中不得硬编码视觉值（DEV_STANDARDS §3、UI 规范 §2.2 / §7 / §10）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须静态扫描而不是靠人工走查：硬编码的 <c>#2563EB</c> 或 <c>FontSize="15"</c>
/// 完全能编译、能运行、能通过渲染测试，只是让"改一处令牌、全局生效"这一承诺失效。
/// 这种漂移是渐进的，等人发现时通常已经散布在多个页面里。
/// </para>
/// <para>
/// 扫描磁盘上的 XAML 源文件而不是 BAML：BAML 里已看不出写法差异。
/// </para>
/// </remarks>
public class VisualHardcodingTests
{
    /// <summary>颜色字面量：<c>#RGB</c> / <c>#RRGGBB</c> / <c>#AARRGGBB</c>。</summary>
    private static readonly Regex ColorLiteral = new(
        @"""#[0-9A-Fa-f]{3,8}""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>直接写在属性上的字号/圆角/边距等视觉值。</summary>
    /// <remarks>
    /// 只匹配"属性直接等于数字"的形式，例如 <c>FontSize="15"</c>；
    /// <c>FontSize="{DynamicResource LfFontSizeBody}"</c> 不会被匹配。
    /// </remarks>
    private static readonly Regex HardcodedVisualValue = new(
        @"\b(FontSize|CornerRadius|Padding|Margin|BorderThickness)\s*=\s*""[0-9]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Views_and_templates_should_not_contain_color_literals()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateScannedFiles())
        {
            foreach (Match match in ColorLiteral.Matches(File.ReadAllText(file)))
            {
                violations.Add($"{Relative(file)} -> {match.Value}");
            }
        }

        violations.ShouldBeEmpty(
            "视图/模板中不得出现颜色字面量，必须引用 Lf* 语义令牌（ADR-015）。" +
            "调色板文件本身除外（它是令牌的唯一来源）：" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Views_and_templates_should_not_contain_hardcoded_font_sizes_or_spacing()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateScannedFiles())
        {
            foreach (Match match in HardcodedVisualValue.Matches(File.ReadAllText(file)))
            {
                violations.Add($"{Relative(file)} -> {match.Value}");
            }
        }

        violations.ShouldBeEmpty(
            "字号/圆角/边距/描边不得硬编码，必须引用 Lf* 令牌（UI 规范 §2.2）。" +
            "确有例外时请在该处加注释说明并更新本守卫的白名单：" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 需要扫描的文件：视图与控件模板。
    /// </summary>
    /// <remarks>
    /// <c>Themes/Tokens/</c> 被排除：那是令牌的<b>定义处</b>，
    /// 颜色与尺寸本来就该写在那里；把定义处也扫掉等于禁止定义令牌。
    /// </remarks>
    private static IEnumerable<string> EnumerateScannedFiles()
    {
        var appDirectory = LocateAppSourceDirectory();

        foreach (var directory in new[] { "Views", "Themes\\Controls" })
        {
            var root = Path.Combine(appDirectory, directory);

            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(LocateAppSourceDirectory(), file).Replace('\\', '/');

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