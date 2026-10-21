using LinkForge.Plugin.Hosting;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Presentation;

/// <summary>插件管理页使用的只读展示项。</summary>
/// <remarks>
/// 这里只把扫描结果转成可读文本，不把“声明已签名”升级成“签名已验证”。
/// 阻断原因与入口哈希保留，便于用户核对后决定是否信任。
/// </remarks>
public sealed record PluginDisplayItem
{
    /// <summary>扫描得到的插件描述。</summary>
    public required PluginDescriptor Descriptor { get; init; }

    /// <summary>插件标识；清单无效时使用目录名。</summary>
    public string Id => Descriptor.Manifest?.Id
        ?? System.IO.Path.GetFileName(Descriptor.PluginDirectory);

    /// <summary>展示名称。</summary>
    public string DisplayName => Descriptor.Manifest?.DisplayName ?? Id;

    /// <summary>发布者。</summary>
    public string Publisher => Descriptor.Manifest?.Publisher ?? "未声明";

    /// <summary>插件版本。</summary>
    public string Version => Descriptor.Manifest?.Version.ToString() ?? "未声明";

    /// <summary>插件要求的 API 版本。</summary>
    public string ApiVersion => Descriptor.Manifest?.ApiVersion.ToString() ?? "未声明";

    /// <summary>能力声明。</summary>
    public string Capabilities => Descriptor.Manifest is { Capabilities.Count: > 0 } manifest
        ? string.Join(" / ", manifest.Capabilities)
        : "未声明";

    /// <summary>入口文件哈希的前 12 位；不展示完整哈希。</summary>
    public string HashPrefix => Descriptor.Manifest?.EntryPointSha256 is { Length: >= 12 } hash
        ? hash[..12]
        : "未声明";

    /// <summary>签名状态文案；声明不等于验证。</summary>
    public string SignatureText => Descriptor.Manifest?.SignatureStatus switch
    {
        PluginSignatureStatus.DeclaredSignedUnverified => "声明已签名（未验证）",
        PluginSignatureStatus.Unsigned => "未签名",
        _ => "未知"
    };

    /// <summary>插件目录（只读展示，不提供打开文件按钮）。</summary>
    public string PluginDirectory => Descriptor.PluginDirectory;

    /// <summary>阻断原因或校验通过说明。</summary>
    public string ValidationText => Descriptor.Status switch
    {
        PluginValidationStatus.Compatible => "校验通过：API、架构与入口哈希一致。",
        _ => $"已阻断：{Descriptor.Detail}"
    };

    /// <summary>是否可以进入运行前确认。</summary>
    public bool IsLoadable => Descriptor.IsLoadable;

    /// <summary>未签名或签名未验证时，必须显式确认来源。</summary>
    public bool RequiresExplicitTrust => Descriptor.RequiresExplicitTrust;

    /// <summary>状态标签。</summary>
    public string StatusText => IsLoadable ? "可加载" : "已阻断";

    /// <summary>状态是否属于失败类（仅用于样式，不改变语义）。</summary>
    public bool HasValidationError => !IsLoadable;

    /// <summary>
    /// 状态标签的视觉语义。
    /// </summary>
    /// <remarks>
    /// 用 Passed/Failed 表达"<b>清单校验</b>通过/未通过"，不是诊断任务结论。
    /// 阻断原因仍由 <see cref="ValidationText"/> 说明，避免把校验失败画成"检测失败"。
    /// </remarks>
    public DiagnosticStatus BadgeStatus => IsLoadable
        ? DiagnosticStatus.Passed
        : DiagnosticStatus.Failed;

    /// <summary>详情行的标签/取值对（插件信息与运行面板使用）。</summary>
    public IReadOnlyList<PluginDetailRow> Details =>
    [
        new("插件标识", Id),
        new("名称", DisplayName),
        new("发布者", Publisher),
        new("版本", Version),
        new("要求 API", ApiVersion),
        new("能力", Capabilities),
        new("签名状态", SignatureText),
        new("入口哈希", HashPrefix),
        new("目录", PluginDirectory),
        new("校验结论", ValidationText)
    ];
}

/// <summary>插件详情中的一行。</summary>
/// <param name="Label">标签。</param>
/// <param name="Value">取值。</param>
public sealed record PluginDetailRow(string Label, string Value);
