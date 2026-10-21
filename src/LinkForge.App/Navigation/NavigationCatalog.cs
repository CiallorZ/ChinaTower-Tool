namespace LinkForge.App.Navigation;

/// <summary>
/// 模块的实现状态。
/// </summary>
/// <remarks>
/// 依据 UI 规范 §3.2 与 DEV_STANDARDS §3.4：
/// "功能未实现"、"当前环境不可用"、"检测失败"、"用户取消"必须使用不同文案与不同状态。
/// 因此导航项必须显式声明自己属于哪一种，而不是靠"能不能点"来隐式表达。
/// </remarks>
public enum LfModuleAvailability
{
    /// <summary>已实现，可正常使用。</summary>
    Available,

    /// <summary>已规划但尚未实现；入口可见并明确标注，不伪装成可用。</summary>
    NotImplemented,

    /// <summary>依赖外部组件（驱动 / SDK / 服务）当前不可用。</summary>
    DependencyMissing
}

/// <summary>
/// 一个导航节点。
/// </summary>
/// <param name="Id">稳定标识（持久化与测试用，不随文案变化）。</param>
/// <param name="Title">导航显示名称。</param>
/// <param name="Glyph">图标字形；取值来自 <see cref="LfGlyph"/>。</param>
/// <param name="Purpose">一句话用途（页面标题区使用，见规范 §4.2）。</param>
/// <param name="Availability">实现状态。</param>
/// <param name="Children">二级项；为空表示这是叶子页面。</param>
public sealed record NavigationNode(
    string Id,
    string Title,
    string Glyph,
    string Purpose,
    LfModuleAvailability Availability,
    IReadOnlyList<NavigationNode> Children)
{
    /// <summary>是否为分组（含二级项）。</summary>
    public bool IsGroup => Children.Count > 0;

    /// <summary>是否可直接导航到页面（叶子且已实现）。</summary>
    public bool IsNavigable => !IsGroup && Availability == LfModuleAvailability.Available;
}

/// <summary>
/// 导航目录：功能树的一级与二级结构。
/// </summary>
/// <remarks>
/// <para>
/// 依据 <c>docs/FUNCTION_TREE.md</c> 与 <c>docs/REVIEW_V1.0.md</c> C-01 裁决：
/// 一级导航采用功能树结构（工作台 / 设备与资产 / 接入调试 / 网络诊断 / 诊断中心 /
/// 安全中心 / 开发者工具 / 项目与报告 / 设置）；
/// 规范推荐的"视频接入 / IoT 接入 / API 调试"作为"接入调试"下的二级项。
/// </para>
/// <para>
/// 本目录是<b>数据</b>而非 UI：它让"功能树有哪些模块、哪些已实现"
/// 成为可单元测试的事实，避免导航与功能树文档长期漂移。
/// <c>NavigationCatalogTests</c> 会核对一级项与 FUNCTION_TREE.md 的章节一一对应。
/// </para>
/// <para>
/// 阶段 03 只交付壳层，除工作台外的模块均为 <see cref="LfModuleAvailability.NotImplemented"/>。
/// 这是如实声明，不是功能删减：模块入口、用途说明与占位页都已就位，
/// 后续阶段只需把 Availability 改为 Available 并挂上真实页面。
/// </para>
/// </remarks>
public static class NavigationCatalog
{
    /// <summary>一级导航项（顺序即界面顺序）。</summary>
    public static IReadOnlyList<NavigationNode> TopLevel { get; } =
    [
        new(
            "dashboard",
            "工作台",
            LfGlyph.Dashboard,
            "快速开始任务、查看近期项目与诊断健康状态。",
            LfModuleAvailability.Available,
            []),

        new(
            "assets",
            "设备与资产",
            LfGlyph.Assets,
            "管理视频设备、IoT 设备与网关资产。",
            LfModuleAvailability.NotImplemented,
            []),

        new(
            "connect",
            "接入调试",
            LfGlyph.Connect,
            "按协议调试设备接入链路。",
            LfModuleAvailability.NotImplemented,
            [
                new("connect.video", "视频接入", LfGlyph.Video,
                    "RTSP / ONVIF 与厂商 SDK 的阶段化诊断。",
                    LfModuleAvailability.Available, []),

                new("connect.iot", "IoT 与协议", LfGlyph.Iot,
                    "MQTT / HTTP / WebSocket 等协议调试。",
                    LfModuleAvailability.Available, []),

                new("connect.api", "API 工作台", LfGlyph.Api,
                    "构造并发送 HTTP/API 请求，查看响应与耗时。",
                    LfModuleAvailability.Available, [])
            ]),

        new(
            "network",
            "网络诊断",
            LfGlyph.Network,
            "连通性、路径、DNS 与端口排查。",
            LfModuleAvailability.Available,
            []),

        new(
            "diagnostics",
            "诊断中心",
            LfGlyph.Diagnostics,
            "编排全链路任务、查看步骤结论与证据。",
            LfModuleAvailability.NotImplemented,
            []),

        new(
            "security",
            "安全中心",
            LfGlyph.Security,
            "在授权范围内检查暴露面与安全基线。",
            LfModuleAvailability.NotImplemented,
            []),

        new(
            "developer",
            "开发者工具",
            LfGlyph.Developer,
            "JSON / 编码 / 哈希 / 正则等常用转换。",
            LfModuleAvailability.NotImplemented,
            []),

        new(
            "reports",
            "项目与报告",
            LfGlyph.Reports,
            "管理项目、任务历史与诊断报告。",
            LfModuleAvailability.Available,
            []),

        new(
            "settings",
            "设置",
            LfGlyph.Settings,
            "外观、日志、存储与扩展设置。",
            LfModuleAvailability.NotImplemented,
            [])
    ];

    /// <summary>按 Id 查找节点（含二级项）。</summary>
    /// <param name="id">节点标识。</param>
    /// <returns>找到的节点；未找到返回 null。</returns>
    public static NavigationNode? Find(string id)
    {
        foreach (var node in Enumerate())
        {
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>深度优先枚举全部节点（一级项在前，随后其二级项）。</summary>
    public static IEnumerable<NavigationNode> Enumerate()
    {
        foreach (var node in TopLevel)
        {
            yield return node;

            foreach (var child in node.Children)
            {
                yield return child;
            }
        }
    }
}
