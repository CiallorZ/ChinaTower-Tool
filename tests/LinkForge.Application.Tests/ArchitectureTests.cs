using System.Reflection;
using System.Xml.Linq;

namespace LinkForge.Application.Tests;

/// <summary>
/// 架构红线测试，对应 DEV_STANDARDS.md §1 依赖方向规则。
/// </summary>
/// <remarks>
/// 分两类断言：
/// 1. 项目依赖图（读 csproj）：约束"声明层面"的依赖方向，即使项目暂时没有代码也有效。
/// 2. 程序集引用（读 IL）：约束"编译产出层面"的依赖，能捕获代码中实际引入 WPF 的情况。
/// 两类都要，是因为 C# 编译器会裁掉未被使用的程序集引用：只看 IL 会漏判空项目的依赖声明，
/// 只看 csproj 又无法发现代码里新增的隐式依赖。
/// </remarks>
public class ArchitectureTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "Wpf.Ui"
    ];

    [Fact]
    public void Application_assembly_should_not_reference_wpf_assemblies()
    {
        var application = Assembly.Load("LinkForge.Application");

        var referenced = application
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbidden => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        referenced.ShouldBeEmpty(
            $"Application 层不得引用 WPF 程序集，实际引用：{string.Join(", ", referenced)}");
    }

    [Fact]
    public void Application_project_should_declare_reference_to_domain()
    {
        var references = ProjectFile.ReadProjectReferences("src/LinkForge.Application/LinkForge.Application.csproj");

        references.ShouldContain("LinkForge.Domain");
    }

    [Fact]
    public void Domain_project_should_have_no_project_references()
    {
        // Domain 是依赖图的叶子，不得依赖任何其他项目。
        var references = ProjectFile.ReadProjectReferences("src/LinkForge.Domain/LinkForge.Domain.csproj");

        references.ShouldBeEmpty(
            $"Domain 必须保持为叶子节点，实际引用：{string.Join(", ", references)}");
    }

    [Fact]
    public void Domain_project_should_have_no_package_references()
    {
        // Domain 保持纯领域模型，不引入任何 NuGet 依赖。
        var packages = ProjectFile.ReadPackageReferences("src/LinkForge.Domain/LinkForge.Domain.csproj");

        packages.ShouldBeEmpty(
            $"Domain 不得引入 NuGet 依赖，实际引用：{string.Join(", ", packages)}");
    }

    [Fact]
    public void App_project_should_reference_application_domain_and_plugin_abstractions()
    {
        var references = ProjectFile.ReadProjectReferences("src/LinkForge.App/LinkForge.App.csproj");

        references.ShouldContain("LinkForge.Application");
        references.ShouldContain("LinkForge.Domain");
        references.ShouldContain("LinkForge.Plugin.Abstractions");
        references.ShouldContain("LinkForge.Adapters.Http");
    }

    [Fact]
    public void Http_adapter_should_not_reference_app_application_or_wpf()
    {
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.Adapters.Http/LinkForge.Adapters.Http.csproj");

        references.ShouldBeEmpty(
            $"Adapters.Http 是独立系统边界，不得反向引用上层；实际引用：{string.Join(", ", references)}");

        var http = Assembly.Load("LinkForge.Adapters.Http");
        var referenced = http
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbidden => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        referenced.ShouldBeEmpty(
            $"Adapters.Http 不得引用 WPF 程序集，实际引用：{string.Join(", ", referenced)}");
    }
}

/// <summary>
/// 定位仓库根目录并读取 csproj 的依赖声明。
/// </summary>
internal static class ProjectFile
{
    public static IReadOnlyList<string> ReadProjectReferences(string relativePath) =>
        ReadValues(relativePath, "ProjectReference");

    public static IReadOnlyList<string> ReadPackageReferences(string relativePath) =>
        ReadValues(relativePath, "PackageReference");

    private static IReadOnlyList<string> ReadValues(string relativePath, string elementName)
    {
        var fullPath = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"未找到项目文件：{fullPath}");

        var document = XDocument.Load(fullPath);

        return document
            .Descendants()
            .Where(e => e.Name.LocalName == elementName)
            .Select(e => e.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeName(value!))
            .ToList();
    }

    /// <summary>
    /// 把 "..\LinkForge.Domain\LinkForge.Domain.csproj" 归一化为 "LinkForge.Domain"。
    /// </summary>
    private static string NormalizeName(string include)
    {
        var fileName = include.Replace('\\', '/').Split('/').Last();

        return fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".csproj".Length]
            : fileName;
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
