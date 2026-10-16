namespace LinkForge.Domain.Assets;

/// <summary>
/// 资产类型（PRD §3.2）。
/// </summary>
public enum AssetKind
{
    /// <summary>未指定。</summary>
    Unknown = 0,

    /// <summary>摄像机。</summary>
    Camera = 1,

    /// <summary>录像机（NVR/DVR）。</summary>
    Recorder = 2,

    /// <summary>视频平台。</summary>
    VideoPlatform = 3,

    /// <summary>IoT 设备。</summary>
    IotDevice = 4,

    /// <summary>IoT 网关。</summary>
    Gateway = 5,

    /// <summary>MQTT Broker。</summary>
    Broker = 6,

    /// <summary>通用主机 / 服务器。</summary>
    Host = 7
}

/// <summary>
/// 一个被调试的目标资产。
/// </summary>
/// <remarks>
/// <para>
/// 依据 PRD §3.2（手动添加、CSV 导入导出、搜索筛选分组）。字段刻意保守：
/// 只保留排障与报告真正会用到的事实，避免变成"什么都能填"的备注表。
/// </para>
/// <para>
/// 与凭据的关系：<see cref="CredentialReference"/> 只保存秘密引用的<b>键</b>，
/// 不保存用户名密码本身。URL 中若内嵌凭据，必须在导入时剥离并转成引用，
/// 否则会随 CSV 导出泄漏（LF-004 的导入校验负责这一点）。
/// </para>
/// </remarks>
public sealed record Asset
{
    /// <summary>资产标识。</summary>
    public required string Id { get; init; }

    /// <summary>所属项目标识。</summary>
    public required string ProjectId { get; init; }

    /// <summary>资产名称（如 "东门摄像机 A"）。</summary>
    public required string Name { get; init; }

    /// <summary>主机（IP 或域名）。</summary>
    public required string Host { get; init; }

    /// <summary>端口；未指定时为 null。</summary>
    public int? Port { get; init; }

    /// <summary>资产类型。</summary>
    public AssetKind Kind { get; init; } = AssetKind.Unknown;

    /// <summary>厂商（如 "海康"、"大华"）；未知时为 null。</summary>
    public string? Vendor { get; init; }

    /// <summary>标签（用于筛选与分组）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>秘密引用键；未配置凭据时为 null。</summary>
    public string? CredentialReference { get; init; }

    /// <summary>备注。</summary>
    public string? Note { get; init; }

    /// <summary>最后修改时间。</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// "主机:端口" 形式的目标文本。
    /// </summary>
    /// <remarks>
    /// 端口未知时只显示主机，不补默认端口 ——
    /// 补一个"看起来合理"的端口会让报告里的目标与实际不符。
    /// </remarks>
    public string TargetText => Port is { } port ? $"{Host}:{port}" : Host;
}
