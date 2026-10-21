using System.IO;
using System.Text.RegularExpressions;
using LinkForge.App.Navigation;
using LinkForge.App.Presentation;

namespace LinkForge.App.Tests.Navigation;

/// <summary>
/// 导航目录契约测试。
/// </summary>
/// <remarks>
/// 导航是"功能树"在界面上的投影，最容易出现的问题不是崩溃而是<b>静默漂移</b>：
/// 文档里加了模块、导航里没加，或者反过来。
/// 因此这里直接把 <c>docs/FUNCTION_TREE.md</c> 的章节读出来做一一比对，
/// 而不是再手写一份期望值（那样只是把同一个错误抄两遍）。
/// </remarks>
public sealed class NavigationCatalogTests
{
    [Fact]
    public void Top_level_items_should_match_the_review_navigation_ruling()
    {
        // 一级导航不是照抄 FUNCTION_TREE 的 12 个章节，而是 REVIEW_V1.0.md C-01 的裁决：
        // 把"视频接入 / IoT 与协议 / API 工作台"归入"接入调试"，
        // 把"报告中心 / 项目与环境"归入"项目与报告"，其余合并为 9 个一级项。
        var documented = ReadReviewNavigationRuling();
        var actual = NavigationCatalog.TopLevel.Select(node => node.Title).ToArray();

        actual.ShouldBe(
            documented,
            "一级导航必须与 REVIEW_V1.0.md C-01 裁决的清单及顺序一致。");
    }

    /// <summary>
    /// 从 <c>docs/REVIEW_V1.0.md</c> 的 C-01 裁决句中解析出约定的一级导航清单。
    /// </summary>
    /// <remarks>
    /// 直接从裁决原文解析（形如 <c>一级：A / B / C</c>）而不是在测试里手抄一份：
    /// 手抄只是把同一份清单写两遍，改文档时两处一起漏改就测不出来了。
    /// </remarks>
    private static string[] ReadReviewNavigationRuling()
    {
        var path = Path.Combine(FindRepositoryRoot(), "docs", "REVIEW_V1.0.md");
        var pattern = new Regex(@"一级：\s*(?<list>[^）)]+)", RegexOptions.Compiled);

        foreach (var line in File.ReadLines(path))
        {
            var match = pattern.Match(line);

            if (!match.Success)
            {
                continue;
            }

            var titles = match.Groups["list"].Value
                .Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToArray();

            titles.ShouldNotBeEmpty($"未能从 C-01 裁决句中解析出一级导航清单：{line}");

            return titles;
        }

        throw new InvalidOperationException(
            $"未在 {path} 中找到 C-01 的一级导航裁决句（应包含“一级：”）。");
    }

    [Fact]
    public void Every_function_tree_module_should_stay_reachable_from_the_navigation()
    {
        // 用户要求"不得擅自删减 PRD 功能"。C-01 合并了若干模块，
        // 因此这里逐条核对：FUNCTION_TREE 的每个章节都必须能在导航里找到落点，
        // 允许改名或降为二级项，但不允许消失。
        //
        // 覆盖表写死在这里是有意为之：FUNCTION_TREE 新增章节而没人把它接进导航时，
        // 本用例会失败（因为文档里的标题在覆盖表中找不到），从而把"静默丢模块"变成红灯。
        var documented = ReadFunctionTreeTopLevelTitles();
        var coveredTitles = NavigationCatalog.Enumerate()
            .SelectMany(node => new[] { node.Title })
            .Concat(ModuleAliases.Keys)
            .ToHashSet(StringComparer.Ordinal);

        var unreachable = documented
            .Where(title => !coveredTitles.Contains(title))
            .ToArray();

        unreachable.ShouldBeEmpty(
            "以下 FUNCTION_TREE 章节在导航中找不到落点（既不是导航标题，也没有登记别名）：" +
            string.Join(", ", unreachable) +
            "。请把新模块接入导航，或在 ModuleAliases 中登记它映射到哪个导航项。");

        // 反向守卫：别名表里的目标必须真实存在，避免"登记了一个不存在的落点"。
        var allTitles = NavigationCatalog.Enumerate()
            .Select(node => node.Title)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (module, target) in ModuleAliases)
        {
            allTitles.ShouldContain(
                target,
                $"别名 {module} 指向的导航项 {target} 不存在。");
        }
    }

    /// <summary>
    /// FUNCTION_TREE 章节 -> 导航项标题的覆盖表（C-01 合并结果）。
    /// </summary>
    /// <remarks>
    /// 只登记"被合并/改名"的章节；标题完全一致的章节由导航标题自身覆盖。
    /// </remarks>
    private static readonly Dictionary<string, string> ModuleAliases = new(StringComparer.Ordinal)
    {
        // C-01：视频 / IoT / API 作为"接入调试"下的二级项。
        ["视频接入"] = "接入调试",
        ["IoT 与协议"] = "接入调试",
        ["API 工作台"] = "接入调试",

        // C-01：诊断中心与报告中心收敛到两个一级项。
        ["全链路诊断中心"] = "诊断中心",
        ["报告中心"] = "项目与报告",

        // C-01：项目/环境管理并入"项目与报告"。
        ["项目与环境"] = "项目与报告",

        // C-01：设置与扩展收敛为"设置"。
        ["设置与扩展"] = "设置"
    };

    [Fact]
    public void Every_node_should_have_a_unique_stable_id()
    {
        var ids = NavigationCatalog.Enumerate().Select(node => node.Id).ToArray();

        ids.ShouldAllBe(id => !string.IsNullOrWhiteSpace(id));
        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            ids.Length,
            "导航 Id 用于持久化与测试定位，必须唯一。");
    }

    [Fact]
    public void Every_node_should_have_a_title_glyph_and_purpose()
    {
        foreach (var node in NavigationCatalog.Enumerate())
        {
            node.Title.ShouldNotBeNullOrWhiteSpace($"{node.Id} 缺少标题。");
            node.Glyph.ShouldNotBeNullOrWhiteSpace($"{node.Id} 缺少图标（规范 §3.2 要求图标 + 文本）。");
            node.Purpose.ShouldNotBeNullOrWhiteSpace($"{node.Id} 缺少一句话用途（规范 §4.2 第 1 项）。");
        }
    }

    [Fact]
    public void Group_nodes_should_not_be_directly_navigable()
    {
        // 分组节点只负责展开二级项；允许直接导航会得到一个没有内容的页面。
        var groups = NavigationCatalog.Enumerate().Where(node => node.IsGroup).ToArray();

        groups.ShouldNotBeEmpty("功能树中存在带二级项的模块（例如接入调试）。");

        foreach (var group in groups)
        {
            group.IsNavigable.ShouldBeFalse($"{group.Id} 是分组节点，不应可直接导航。");
            group.Children.ShouldNotBeEmpty($"{group.Id} 声明为分组但没有子项。");
        }
    }

    [Fact]
    public void Leaf_nodes_should_be_navigable_exactly_when_available()
    {
        foreach (var leaf in NavigationCatalog.Enumerate().Where(node => !node.IsGroup))
        {
            leaf.IsNavigable.ShouldBe(
                leaf.Availability == LfModuleAvailability.Available,
                $"{leaf.Id} 的可导航性必须由 Availability 决定，而不是另设开关。");
        }
    }

    [Fact]
    public void Find_should_locate_top_level_and_nested_nodes()
    {
        NavigationCatalog.Find("dashboard").ShouldNotBeNull();
        NavigationCatalog.Find("connect.video").ShouldNotBeNull();
        NavigationCatalog.Find("connect.video")!.Title.ShouldBe("视频接入");
    }

    [Fact]
    public void Find_should_return_null_for_unknown_ids()
    {
        NavigationCatalog.Find("no-such-module").ShouldBeNull();
        NavigationCatalog.Find(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void Nested_node_ids_should_be_prefixed_by_their_parent()
    {
        // 前缀约定让"某个二级项属于哪个一级项"无需查表即可判断，
        // 也让持久化下来的 Id 在重构层级时能一眼看出归属。
        foreach (var parent in NavigationCatalog.TopLevel)
        {
            foreach (var child in parent.Children)
            {
                child.Id.ShouldStartWith(
                    parent.Id + ".",
                    customMessage: $"{child.Id} 应以父节点 Id 加分隔符为前缀。");
            }
        }
    }

    [Fact]
    public void Implemented_modules_should_match_the_current_stage_delivery()
    {
        // 阶段 05 已交付网络/API/MQTT/RTSP 诊断、报告页、开发者工具与插件管理。断言防止
        // 有人为了好看把未实现模块标成 Available —— 那会让导航指向空白页，
        // 属于"用假状态伪装真实功能"。
        var available = NavigationCatalog.Enumerate()
            .Where(node => node.Availability == LfModuleAvailability.Available)
            .Select(node => node.Id)
            .ToArray();

        available.ShouldBe(
            [
                "dashboard",
                "connect.video",
                "connect.iot",
                "connect.api",
                "network",
                "developer",
                "reports",
                "settings.plugins"
            ]);
    }

    [Fact]
    public void Every_non_implemented_leaf_should_explain_itself()
    {
        // "尚未实现"必须能给出原因，否则用户看到入口却不知道能不能用。
        // 具体文案由 ShellViewModel 生成，这里只保证状态不是默认值。
        foreach (var leaf in NavigationCatalog.Enumerate().Where(node => !node.IsGroup))
        {
            if (leaf.Availability == LfModuleAvailability.Available)
            {
                continue;
            }

            leaf.Availability.ShouldBeOneOf(
                LfModuleAvailability.NotImplemented,
                LfModuleAvailability.DependencyMissing);
        }
    }

    /// <summary>
    /// 已实现模块 -> （视图模型类型名，壳层内容区元素名）。
    /// </summary>
    /// <remarks>
    /// 这张表是"Available 不是空话"的唯一机器可检查表达。
    /// 工作台不需要独立条目：它由 <c>App.OnStartup</c> 直接解析并作为默认页。
    /// </remarks>
    private static readonly Dictionary<string, (string ViewModel, string ContentHost)> ImplementedModuleWiring =
        new(StringComparer.Ordinal)
        {
            ["network"] = ("NetworkDiagnosticsViewModel", "NetworkDiagnosticsContent"),
            ["connect.api"] = ("ApiWorkbenchViewModel", "ApiWorkbenchContent"),
            ["connect.iot"] = ("MqttWorkbenchViewModel", "MqttWorkbenchContent"),
            ["connect.video"] = ("RtspDiagnosticsViewModel", "RtspDiagnosticsContent"),
            ["reports"] = ("ReportPageViewModel", "ReportContent"),
            ["developer"] = ("DeveloperToolsViewModel", "DeveloperToolsContent"),
            ["settings.plugins"] = ("PluginManagerViewModel", "PluginManagerContent")
        };

    [Fact]
    public void Every_available_module_must_be_registered_and_hosted()
    {
        // 真实缺陷回归守卫：阶段 04 曾因 IRunHistoryStore 只注册具体类型未注册接口，
        // 构建与全部测试都通过，但应用一启动就崩（退出码 -532462766）。
        // 把模块标成 Available 而漏掉 DI 注册或壳层宿主，会得到一个空白页 ——
        // 那正是"用假状态伪装真实功能"，必须由测试挡住而不是靠人工走查。
        var appSource = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "LinkForge.App", "App.xaml.cs"));
        var shellSource = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "LinkForge.App", "Views", "ShellWindow.xaml"));

        var available = NavigationCatalog.Enumerate()
            .Where(static node => !node.IsGroup)
            .Where(static node => node.Availability == LfModuleAvailability.Available)
            .Select(static node => node.Id)
            .ToArray();

        // 双向核对：既不允许"Available 但没有接线"，也不允许"接线了却仍标为未实现"。
        var dashboard = "dashboard";

        foreach (var id in available.Where(id => id != dashboard))
        {
            ImplementedModuleWiring.ContainsKey(id).ShouldBeTrue(
                $"模块 {id} 已标记 Available，但未在 ImplementedModuleWiring 中登记视图模型与宿主。");
        }

        foreach (var (id, wiring) in ImplementedModuleWiring)
        {
            available.ShouldContain(id, $"登记了 {id} 的接线，但导航目录未把它标为 Available。");

            appSource.Contains(
                    $"services.AddSingleton<{wiring.ViewModel}>()",
                    StringComparison.Ordinal)
                .ShouldBeTrue(
                    $"视图模型 {wiring.ViewModel} 未注册到 DI：导航到 {id} 会得到空白页。");

            shellSource.Contains(
                    $"x:Name=\"{wiring.ContentHost}\"",
                    StringComparison.Ordinal)
                .ShouldBeTrue(
                    $"壳层缺少 {id} 的内容宿主 {wiring.ContentHost}。");

            // 宿主必须绑定到对应视图模型属性，否则页面 DataContext 落空（阶段 03 真实缺陷）。
            var expectedBinding = wiring.ViewModel[..^"ViewModel".Length];
            shellSource.Contains(
                    $"DataContext=\"{{Binding {expectedBinding}}}\"",
                    StringComparison.Ordinal)
                .ShouldBeTrue(
                    $"{wiring.ContentHost} 未绑定 DataContext={{Binding {expectedBinding}}}，页面绑定会静默落空。");
        }

        appSource.Contains(
                "services.AddSingleton<DashboardViewModel>()",
                StringComparison.Ordinal)
            .ShouldBeTrue("工作台视图模型必须注册，否则启动即失败。");
        shellSource.Contains(
                "x:Name=\"DashboardContent\"",
                StringComparison.Ordinal)
            .ShouldBeTrue("壳层必须承载工作台页面。");
    }

    [Fact]
    public void Developer_tool_page_must_not_claim_capabilities_it_lacks()
    {
        // LF-011 边界：本页只做离线文本转换。
        // 若有人把"哈希/正则/XML"写进页面文案而不实现，就是假宣传。
        var pagePath = Path.Combine(
            FindRepositoryRoot(), "src", "LinkForge.App", "Views", "Pages", "DeveloperToolsPage.xaml");
        var page = File.ReadAllText(pagePath);
        var titles = string.Join(' ', DeveloperToolDescriptor.All.Select(static tool => tool.Name));

        foreach (var unimplemented in new[] { "哈希", "正则", "XML", "YAML", "Hex 视图" })
        {
            page.Contains(unimplemented, StringComparison.Ordinal).ShouldBeFalse(
                $"页面文案提到 {unimplemented}，但转换库未实现该能力。");
            titles.Contains(unimplemented, StringComparison.Ordinal).ShouldBeFalse(
                $"工具目录出现 {unimplemented}，但转换库未实现该能力。");
        }
    }

    /// <summary>
    /// 从 <c>docs/FUNCTION_TREE.md</c> 的代码块中解析一级章节标题。
    /// </summary>
    /// <remarks>
    /// 形如 <c>├─ 1. 工作台</c> 或 <c>└─ 12. 设置与扩展</c>；
    /// 二级项以 <c>│  ├─</c> 开头（前面有竖线），据此与一级项区分。
    /// </remarks>
    private static string[] ReadFunctionTreeTopLevelTitles()
    {
        var path = Path.Combine(FindRepositoryRoot(), "docs", "FUNCTION_TREE.md");
        var pattern = new Regex(@"^[├└]─\s*\d+\.\s*(?<title>.+?)\s*$", RegexOptions.Compiled);
        var titles = new List<string>();

        foreach (var line in File.ReadLines(path))
        {
            var match = pattern.Match(line.Trim());

            if (match.Success)
            {
                titles.Add(match.Groups["title"].Value);
            }
        }

        titles.ShouldNotBeEmpty($"未能从 {path} 解析出一级章节，文档格式可能已改变。");

        return [.. titles];
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
