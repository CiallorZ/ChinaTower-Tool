using LinkForge.Application.Assets;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;

namespace LinkForge.App.Presentation;

/// <summary>
/// 项目下拉项（LF-004）。
/// </summary>
/// <param name="Id">项目标识。</param>
/// <param name="Name">项目名称。</param>
/// <param name="Description">项目描述。</param>
/// <param name="DefaultTimeoutSeconds">项目级默认超时。</param>
/// <param name="UpdatedAt">最后修改时间。</param>
public sealed record ProjectDisplayItem(
    string Id,
    string Name,
    string? Description,
    int? DefaultTimeoutSeconds,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>最后修改时间文案（本地时间，未设置时为"尚未修改"）。</summary>
    public string UpdatedText => UpdatedAt is { } updated
        ? updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
        : "尚未修改";

    /// <summary>下拉框显示文本。</summary>
    public override string ToString() => Name;

    /// <summary>由领域模型创建展示项。</summary>
    /// <param name="project">项目。</param>
    /// <returns>展示项。</returns>
    public static ProjectDisplayItem From(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new ProjectDisplayItem(
            project.Id,
            project.Name,
            project.Description,
            project.DefaultTimeoutSeconds,
            project.UpdatedAt);
    }
}

/// <summary>
/// 资产类型筛选项。
/// </summary>
/// <param name="Kind">资产类型；null 表示"全部"。</param>
/// <param name="Title">显示名称。</param>
public sealed record AssetKindOption(AssetKind? Kind, string Title)
{
    /// <inheritdoc />
    public override string ToString() => Title;

    /// <summary>构建"全部" + 每种已知类型 + "未指定"的选项表。</summary>
    /// <returns>选项列表。</returns>
    public static IReadOnlyList<AssetKindOption> BuildAll() =>
    [
        new(null, "全部类型"),
        .. AssetKindNames.All.Select(kind => new AssetKindOption(kind, AssetKindNames.Display(kind)))
    ];
}

/// <summary>
/// 资产列表项（LF-004）。
/// </summary>
/// <remarks>
/// 包一层而不是直接把 <c>Asset</c> 交给界面：列表需要显示"类型中文名"、
/// "标签串"、"凭据是否已配置"这些派生信息，把它们写在展示层可以避免
/// 领域模型为了显示而长出 <c>DisplayName</c> 之类的成员。
/// </remarks>
public sealed record AssetDisplayItem(Asset Source)
{
    /// <summary>资产标识。</summary>
    public string Id => Source.Id;

    /// <summary>名称。</summary>
    public string Name => Source.Name;

    /// <summary>"主机:端口"文本。</summary>
    public string TargetText => Source.TargetText;

    /// <summary>类型中文名。</summary>
    public string KindText => AssetKindNames.Display(Source.Kind);

    /// <summary>厂商（未知时为空）。</summary>
    public string VendorText => string.IsNullOrWhiteSpace(Source.Vendor) ? "未知厂商" : Source.Vendor;

    /// <summary>标签文本（无标签时为空）。</summary>
    public string TagsText => AssetTagText.Format(Source.Tags);

    /// <summary>是否有标签。</summary>
    public bool HasTags => Source.Tags.Count > 0;

    /// <summary>是否配置了凭据引用。</summary>
    public bool HasCredential => !string.IsNullOrWhiteSpace(Source.CredentialReference);

    /// <summary>凭据状态文案（只说明"是否已配置引用"，绝不含任何值）。</summary>
    public string CredentialText => HasCredential ? "已配置凭据引用" : "未配置凭据";
}

/// <summary>
/// 资产列表中的一行。
/// </summary>
/// <remarks>
/// <para>
/// 分组用一个"扁平列表 + 分组头行"表达，而不是嵌套集合：
/// 嵌套集合下选中态要在多个列表之间同步，键盘上下移动会跨不过分组边界。
/// 扁平列表让"分组"只是视觉上的分隔，交互仍然是同一个列表。
/// </para>
/// <para>
/// 分组头行在界面上不可选中（<c>IsGroupHeader</c> 为 true），
/// 因此"选中项一定是一台真实资产"是结构上的保证。
/// </para>
/// </remarks>
public sealed record AssetListItem
{
    /// <summary>分组头行的标题；资产行为 null。</summary>
    public string? GroupTitle { get; init; }

    /// <summary>资产；分组头行为 null。</summary>
    public AssetDisplayItem? Asset { get; init; }

    /// <summary>是否为分组头行。</summary>
    public bool IsGroupHeader => Asset is null;

    /// <summary>是否为资产行。</summary>
    public bool IsAssetRow => Asset is not null;

    /// <summary>创建分组头行。</summary>
    /// <param name="title">标题。</param>
    /// <returns>行。</returns>
    public static AssetListItem Header(string title) => new() { GroupTitle = title };

    /// <summary>创建资产行。</summary>
    /// <param name="asset">资产展示项。</param>
    /// <returns>行。</returns>
    public static AssetListItem Row(AssetDisplayItem asset) => new() { Asset = asset };
}
