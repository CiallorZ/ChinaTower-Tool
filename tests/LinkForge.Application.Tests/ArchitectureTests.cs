using System.Reflection;
using System.Xml.Linq;
using LinkForge.DeveloperTools;
using LinkForge.Plugin.Hosting;

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
    public void Persistence_project_should_stay_below_the_ui_layer()
    {
        // LF-004：Persistence 只允许依赖 Domain / Application。
        // 一旦它引用 App，数据库层就会开始触碰对话框与窗口状态，
        // 之后任何单测都必须起一个 WPF 应用才能验证存储。
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.Persistence/LinkForge.Persistence.csproj");

        references.ShouldNotContain("LinkForge.App");
        references.ShouldContain("LinkForge.Application");

        var persistence = typeof(LinkForge.Persistence.LinkForgeDatabase).Assembly;
        var forbidden = persistence
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbiddenPrefix => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        forbidden.ShouldBeEmpty(
            $"Persistence 不得引用 WPF 程序集，实际引用：{string.Join(", ", forbidden)}");
    }

    [Fact]
    public void Persistence_must_not_reference_any_adapter()
    {
        // 适配器是诊断能力的实现，持久化是资产数据的存储。
        // 两者耦合后，"读一个项目"就会拉进 RTSP/MQTT/HTTP 依赖，
        // 打开资产管理页的启动成本会被协议库的无谓初始化吃掉。
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.Persistence/LinkForge.Persistence.csproj");

        foreach (var adapter in new[]
                 {
                     "LinkForge.Adapters.Http",
                     "LinkForge.Adapters.Mqtt",
                     "LinkForge.Adapters.Network",
                     "LinkForge.Adapters.Onvif",
                     "LinkForge.Adapters.Security",
                     "LinkForge.Adapters.Video"
                 })
        {
            references.ShouldNotContain(adapter);
        }
    }

    [Fact]
    public void App_project_should_reference_persistence()
    {
        // 界面必须通过持久化项目拿数据，而不是自己 new 一个 DbContext：
        // 后者会让连接与迁移策略散落在各个页面里。
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.App/LinkForge.App.csproj");

        references.ShouldContain("LinkForge.Persistence");
    }

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
        references.ShouldContain("LinkForge.Adapters.Security");
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

    [Fact]
    public void Mqtt_adapter_should_not_reference_app_application_or_wpf()
    {
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.Adapters.Mqtt/LinkForge.Adapters.Mqtt.csproj");

        references.ShouldBeEmpty(
            $"Adapters.Mqtt 是独立系统边界，不得反向引用上层；实际引用：{string.Join(", ", references)}");

        var mqtt = Assembly.Load("LinkForge.Adapters.Mqtt");
        var referenced = mqtt
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbidden => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        referenced.ShouldBeEmpty(
            $"Adapters.Mqtt 不得引用 WPF 程序集，实际引用：{string.Join(", ", referenced)}");
    }

    [Fact]
    public void Video_adapter_should_not_reference_app_application_or_wpf()
    {
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.Adapters.Video/LinkForge.Adapters.Video.csproj");

        references.ShouldNotContain("LinkForge.App");
        references.ShouldNotContain("LinkForge.Application");

        var video = Assembly.Load("LinkForge.Adapters.Video");
        var forbidden = video
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbiddenPrefix => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        forbidden.ShouldBeEmpty(
            $"Adapters.Video 不得引用 WPF 程序集，实际引用：{string.Join(", ", forbidden)}");
    }

    [Fact]
    public void Onvif_adapter_should_not_reference_app_application_or_wpf()
    {
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.Adapters.Onvif/LinkForge.Adapters.Onvif.csproj");

        references.ShouldNotContain("LinkForge.App");
        references.ShouldNotContain("LinkForge.Application");

        var onvif = typeof(LinkForge.Adapters.Onvif.OnvifDiagnosticAdapter).Assembly;
        var forbidden = onvif
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbiddenPrefix => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        forbidden.ShouldBeEmpty(
            $"Adapters.Onvif 不得引用 WPF 程序集，实际引用：{string.Join(", ", forbidden)}");
    }

    [Fact]
    public void Security_adapter_should_not_reference_app_application_or_wpf()
    {
        // LF-020：安全基线适配器只负责单目标端口/TLS 探测，
        // 不得依赖 UI 或应用编排层，也不得把对话框和授权状态带进协议实现。
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.Adapters.Security/LinkForge.Adapters.Security.csproj");

        references.ShouldNotContain("LinkForge.App");
        references.ShouldNotContain("LinkForge.Application");
        references.ShouldContain("LinkForge.Domain");
        references.ShouldContain("LinkForge.Plugin.Abstractions");
    }

    [Fact]
    public void Reporting_project_should_not_reference_app_adapters_or_wpf()
    {
        // Reporting 是纯渲染与扫描库：只允许依赖 Domain / Application。
        // 一旦它引用了 App 或某个适配器，"报告格式"就会与某个具体协议耦合，
        // 之后新增协议必须改报告库，而不是只加一个适配器。
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.Reporting/LinkForge.Reporting.csproj");

        references.ShouldNotContain("LinkForge.App");
        references.ShouldNotContain("LinkForge.Adapters.Http");
        references.ShouldNotContain("LinkForge.Adapters.Mqtt");
        references.ShouldNotContain("LinkForge.Adapters.Network");
        references.ShouldNotContain("LinkForge.Adapters.Onvif");
        references.ShouldNotContain("LinkForge.Adapters.Security");
        references.ShouldNotContain("LinkForge.Adapters.Video");
        references.ShouldContain("LinkForge.Domain");

        var reporting = Assembly.Load("LinkForge.Reporting");
        var forbidden = reporting
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbiddenPrefix => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        forbidden.ShouldBeEmpty(
            $"Reporting 不得引用 WPF 程序集，实际引用：{string.Join(", ", forbidden)}");
    }

    [Fact]
    public void Developer_tools_should_be_a_pure_offline_library()
    {
        // LF-011：开发者工具必须是纯离线转换库。
        // 一旦它引用 App/WPF，剪贴板、对话框与 UI 状态就会渗进转换逻辑，
        // "结果不写日志/历史/报告"这条约束将失去结构性保证。
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.DeveloperTools/LinkForge.DeveloperTools.csproj");

        references.ShouldBeEmpty(
            $"DeveloperTools 必须保持零项目引用，实际引用：{string.Join(", ", references)}");

        var packages = ProjectFile.ReadPackageReferences(
            "src/LinkForge.DeveloperTools/LinkForge.DeveloperTools.csproj");

        packages.ShouldBeEmpty(
            $"DeveloperTools 不得引入 NuGet 依赖，实际引用：{string.Join(", ", packages)}");

        // 用 typeof 而不是 Assembly.Load("...")：未被代码引用的程序集会被编译器
        // 从 IL 引用表中裁掉，Load byname 会因此间歇性失败（阶段 05 已踩过）。
        var assembly = typeof(DeveloperTransforms).Assembly;
        var forbidden = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbiddenPrefix => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        forbidden.ShouldBeEmpty(
            $"DeveloperTools 不得引用 WPF 程序集，实际引用：{string.Join(", ", forbidden)}");
    }

    [Fact]
    public void Plugin_hosting_should_stay_below_the_ui_layer()
    {
        // LF-012：插件宿主必须停留在 UI 层之下。
        // 一旦它能引用 App/WPF，"插件读不到宿主秘密库"就从结构性保证退化为约定：
        // 宿主侧会有服务对象可传，页面侧也能直接把对话框塞进执行路径。
        var references = ProjectFile.ReadProjectReferences(
            "src/LinkForge.Plugin.Hosting/LinkForge.Plugin.Hosting.csproj");

        references.ShouldNotContain("LinkForge.App");
        references.ShouldNotContain("LinkForge.Application");
        references.ShouldContain("LinkForge.Plugin.Abstractions");

        var packages = ProjectFile.ReadPackageReferences(
            "src/LinkForge.Plugin.Hosting/LinkForge.Plugin.Hosting.csproj");

        packages.ShouldBeEmpty(
            $"插件宿主不得引入 NuGet 依赖，实际引用：{string.Join(", ", packages)}");

        // 用 typeof 而不是 Assembly.Load：未被引用的程序集会被编译器从 IL 引用表裁掉。
        var assembly = typeof(PluginHostContract).Assembly;
        var forbidden = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(
                forbiddenPrefix => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        forbidden.ShouldBeEmpty(
            $"Plugin.Hosting 不得引用 WPF 程序集，实际引用：{string.Join(", ", forbidden)}");
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
