using System.IO;
using System.Text.RegularExpressions;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 静态守卫：控件模板内的 <c>Lf*</c> 引用必须是 <c>DynamicResource</c>（ADR-019）。
/// </summary>
/// <remarks>
/// 为什么必须有静态扫描：
/// 模板是延迟实例化的，模板内的 <c>StaticResource</c> 要等到
/// <c>LoadContent</c> 时才报错，而 <c>dotnet build</c> 完全发现不了。
/// 阶段 02 的真实崩溃就是这么漏到运行时的。
/// 静态扫描把这类问题挡在编译期，比渲染测试更早、也更便宜。
///
/// 扫描的是磁盘上的 XAML 源文件而非 BAML：
/// BAML 里已无 <c>StaticResource</c>/<c>DynamicResource</c> 的写法差异可供判断。
/// </remarks>
public class ResourceReferenceTests
{
    /// <summary>
    /// 位置豁免：<c>Converter=</c> 不是依赖属性，只能用 <c>StaticResource</c>。
    /// </summary>
    private static readonly Regex StaticTokenReference = new(
        @"\{StaticResource\s+(Lf[A-Za-z0-9]+)\}",
        RegexOptions.Compiled);

    private static readonly Regex ConverterReference = new(
        @"Converter=\{StaticResource\s+(Lf[A-Za-z0-9]+)\}",
        RegexOptions.Compiled);

    [Fact]
    public void Control_templates_should_reference_tokens_with_dynamic_resource()
    {
        var violations = new List<string>();

        foreach (var file in FindControlTemplateFiles())
        {
            var content = File.ReadAllText(file);

            // 先剔除 Converter= 位置的合法 StaticResource，再检查剩下的。
            var withoutConverters = ConverterReference.Replace(content, string.Empty);

            foreach (Match match in StaticTokenReference.Matches(withoutConverters))
            {
                violations.Add($"{Path.GetFileName(file)} -> {match.Groups[1].Value}");
            }
        }

        violations.ShouldBeEmpty(
            "控件模板内不得用 StaticResource 引用 Lf* 令牌。\n" +
            "模板是延迟实例化的，LoadContent 时模板根元素不在逻辑树上，\n" +
            "StaticResource 沿树查找会落空并抛 XamlParseException。\n" +
            "违反项：\n  " + string.Join("\n  ", violations) +
            "\n依据：ADR-019。Converter= 位置豁免（Binding.Converter 不是依赖属性）。");
    }

    /// <summary>
    /// 反向守卫：确认豁免范围没有被滥用。
    /// </summary>
    /// <remarks>
    /// 只有扫描豁免还不够 —— 如果有人为了让扫描通过而把
    /// <c>Converter=</c> 硬编码进大量位置，豁免本身就失去了意义。
    /// 这条用例把豁免限制在已知的转换器键上。
    /// </remarks>
    [Fact]
    public void Static_resource_exemptions_should_be_limited_to_converters()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "LfBoolToVisibility" };
        var unexpected = new List<string>();

        foreach (var file in FindControlTemplateFiles())
        {
            foreach (Match match in ConverterReference.Matches(File.ReadAllText(file)))
            {
                if (!allowed.Contains(match.Groups[1].Value))
                {
                    unexpected.Add($"{Path.GetFileName(file)} -> {match.Groups[1].Value}");
                }
            }
        }

        unexpected.ShouldBeEmpty(
            "Converter= 位置的 StaticResource 豁免只应覆盖布尔转可见性转换器。\n" +
            "出现新键时请先确认它确实只能用 StaticResource，再把它加入允许清单。\n" +
            "意外项：\n  " + string.Join("\n  ", unexpected));
    }

    /// <summary>定位 <c>Themes/Controls</c> 目录下的全部 XAML。</summary>
    private static string[] FindControlTemplateFiles()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "src", "LinkForge.App", "Themes", "Controls");

        return Directory.GetFiles(directory, "*.xaml");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LinkForge.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"未能从 {AppContext.BaseDirectory} 向上找到包含 LinkForge.slnx 的仓库根目录。");
    }
}