namespace LinkForge.Plugin.Hosting;

/// <summary>宿主支持的插件 API 版本。</summary>
/// <remarks>
/// 只声明精确支持的主/次版本，不给"未来版本可能兼容"留想象空间：
/// 插件要求 <c>1.1</c> 而宿主只实现 <c>1.0</c> 时必须阻断，
/// 否则会在运行到未实现的方法时才失败。
/// </remarks>
public static class PluginHostContract
{
    /// <summary>支持的插件 API 版本。</summary>
    public static Version SupportedApiVersion { get; } = new(1, 0);

    /// <summary>进程协议版本。</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>清单文件名。</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>清单文件大小上限（64 KiB）。</summary>
    public const int MaxManifestBytes = 64 * 1024;
}

/// <summary>插件声明的签名状态。</summary>
/// <remarks>
/// <b>不存在 Verified。</b> 当前宿主没有证书链与发布者信任配置，
/// 无法验证签名；若提供"已验证"枚举值，界面迟早会把声明当成事实。
/// </remarks>
public enum PluginSignatureStatus
{
    /// <summary>未签名。</summary>
    Unsigned = 0,

    /// <summary>清单声明已签名，但宿主无法验证。</summary>
    DeclaredSignedUnverified = 1
}

/// <summary>一个插件声明的诊断步骤。</summary>
/// <param name="StepId">稳定步骤标识。</param>
/// <param name="Name">展示名称。</param>
/// <param name="Timeout">单步超时。</param>
/// <param name="Dependencies">外部依赖标识。</param>
/// <param name="Prerequisites">前置步骤标识。</param>
public sealed record PluginStepDeclaration(
    string StepId,
    string Name,
    TimeSpan Timeout,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Prerequisites);

/// <summary>一个通过结构校验的插件清单。</summary>
public sealed record PluginManifest
{
    /// <summary>稳定插件标识（小写、3–64 字符）。</summary>
    public required string Id { get; init; }

    /// <summary>展示名称。</summary>
    public required string DisplayName { get; init; }

    /// <summary>插件版本。</summary>
    public required Version Version { get; init; }

    /// <summary>插件要求的宿主 API 版本。</summary>
    public required Version ApiVersion { get; init; }

    /// <summary>发布者名称。</summary>
    public required string Publisher { get; init; }

    /// <summary>用途说明。</summary>
    public required string Description { get; init; }

    /// <summary>能力声明（如 <c>video.vendor.hikvision</c>）。</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>支持的 CPU 架构；<c>any</c> 表示与架构无关。</summary>
    public required IReadOnlyList<string> Architectures { get; init; }

    /// <summary>入口可执行文件（相对插件目录）。</summary>
    public required string EntryPoint { get; init; }

    /// <summary>入口文件的 SHA-256（十六进制）。</summary>
    public required string EntryPointSha256 { get; init; }

    /// <summary>签名状态。</summary>
    public required PluginSignatureStatus SignatureStatus { get; init; }

    /// <summary>声明的诊断步骤。</summary>
    public required IReadOnlyList<PluginStepDeclaration> Steps { get; init; }
}

/// <summary>插件清单的读取结果。</summary>
/// <param name="Manifest">解析成功时的清单；失败时为 null。</param>
/// <param name="Error">失败原因；成功时为 null。</param>
public sealed record PluginManifestReadResult(PluginManifest? Manifest, string? Error)
{
    /// <summary>是否解析成功。</summary>
    public bool Succeeded => Manifest is not null && Error is null;

    /// <summary>构造成功结果。</summary>
    public static PluginManifestReadResult Success(PluginManifest manifest) => new(manifest, null);

    /// <summary>构造失败结果。</summary>
    public static PluginManifestReadResult Failure(string error) => new(null, error);
}

/// <summary>插件校验状态。</summary>
public enum PluginValidationStatus
{
    /// <summary>可安全交给进程外宿主执行。</summary>
    Compatible = 0,

    /// <summary>清单缺失或格式非法。</summary>
    InvalidManifest = 1,

    /// <summary>API 版本与宿主不兼容。</summary>
    IncompatibleApi = 2,

    /// <summary>当前系统或 CPU 架构不受支持。</summary>
    UnsupportedPlatform = 3,

    /// <summary>入口文件缺失或越出插件目录。</summary>
    EntryPointMissing = 4,

    /// <summary>入口文件哈希与清单不一致。</summary>
    HashMismatch = 5,

    /// <summary>扫描时发生 I/O 或权限错误。</summary>
    ScanError = 6
}

/// <summary>一个已扫描插件的可验证事实。</summary>
public sealed record PluginDescriptor(
    string PluginDirectory,
    string ManifestPath,
    PluginValidationStatus Status,
    string Detail,
    PluginManifest? Manifest,
    string? ActualEntryPointSha256)
{
    /// <summary>是否允许启动入口进程。</summary>
    public bool IsLoadable =>
        Status == PluginValidationStatus.Compatible
        && Manifest is not null;

    /// <summary>是否需要在启动前显式确认来源。</summary>
    public bool RequiresExplicitTrust => Manifest?.SignatureStatus
        is PluginSignatureStatus.Unsigned
        or PluginSignatureStatus.DeclaredSignedUnverified;
}
