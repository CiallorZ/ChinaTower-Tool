using System.Reflection;

namespace LinkForge.Domain.Tests;

/// <summary>
/// 架构红线测试：Domain 层必须保持纯净，不得依赖 UI 或基础设施。
/// 对应 DEV_STANDARDS.md §1 依赖方向规则。
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "PresentationFramework", // WPF
        "PresentationCore",
        "WindowsBase",
        "Microsoft.EntityFrameworkCore",
        "Serilog",
        "Wpf.Ui"
    ];

    [Fact]
    public void Domain_assembly_should_not_reference_ui_or_infrastructure_assemblies()
    {
        var domain = Assembly.Load("LinkForge.Domain");

        var referenced = domain
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbidden => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        referenced.ShouldBeEmpty(
            $"Domain 层不得引用 UI/基础设施程序集，实际引用：{string.Join(", ", referenced)}");
    }

    [Fact]
    public void Domain_assembly_should_be_loadable_and_non_empty_contract()
    {
        var domain = Assembly.Load("LinkForge.Domain");

        // 防止"项目被误删或未编译"导致后续测试静默通过。
        domain.ShouldNotBeNull();
        domain.GetName().Name.ShouldBe("LinkForge.Domain");
    }
}
