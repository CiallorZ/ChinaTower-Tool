namespace LinkForge.Domain.Projects;

/// <summary>
/// 一个调试项目（PRD §7）。
/// </summary>
/// <remarks>
/// <para>
/// 阶段 04 只落地模型，持久化（SQLite + 迁移）属 LF-004。
/// 之所以现在就把模型定下来：工作台的"最近项目"、诊断结果的 <c>ProjectId</c>、
/// 报告的目标信息都要引用它，先把字段与不变量固定，后续接入存储时不必改调用方。
/// </para>
/// <para>
/// <b>本类型不包含任何秘密字段。</b>密码/Token 一律通过
/// <see cref="LinkForge.Domain.Secrets.SecretReference"/> 间接引用（ADR-008），
/// 这样"导出项目不含明文凭据"是结构上的保证，而不是靠导出代码记得过滤。
/// </para>
/// </remarks>
public sealed record Project
{
    /// <summary>项目标识（稳定，供历史与报告引用）。</summary>
    public required string Id { get; init; }

    /// <summary>项目名称。</summary>
    public required string Name { get; init; }

    /// <summary>项目描述（可为空）。</summary>
    public string? Description { get; init; }

    /// <summary>项目包 schema 版本；读取旧项目时据此迁移。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>创建时间。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后修改时间。</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>项目级默认超时（秒）；未设置时为 null，由任务使用默认值。</summary>
    public int? DefaultTimeoutSeconds { get; init; }

    /// <summary>当前项目包 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;
}
