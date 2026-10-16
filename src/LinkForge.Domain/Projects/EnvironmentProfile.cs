namespace LinkForge.Domain.Projects;

/// <summary>
/// 一个环境配置（开发 / 测试 / 生产）。
/// </summary>
/// <remarks>
/// <para>
/// 依据 <c>docs/ARCHITECTURE.md</c> §5 与 PRD §3.1。环境只保存<b>非秘密</b>配置：
/// 变量、代理地址、超时策略。代理若带凭据，必须走 <c>SecretReference</c>，
/// 不得写进 <see cref="Variables"/>。
/// </para>
/// <para>
/// 变量名刻意大小写不敏感（<see cref="Variables"/> 的构造由调用方保证），
/// 因为 <c>Host</c>/<c>host</c> 在排障时被当成同一个变量是常见预期。
/// </para>
/// </remarks>
public sealed record EnvironmentProfile
{
    /// <summary>环境标识。</summary>
    public required string Id { get; init; }

    /// <summary>所属项目标识。</summary>
    public required string ProjectId { get; init; }

    /// <summary>环境名称（如 "测试环境"）。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 环境变量（键值对）。
    /// </summary>
    /// <remarks>
    /// 只允许放非秘密值。含凭据的项必须存 <c>SecretReference</c> 的引用键。
    /// </remarks>
    public IReadOnlyDictionary<string, string> Variables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>HTTP 代理地址（非秘密部分）；未配置时为 null。</summary>
    public string? ProxyUrl { get; init; }

    /// <summary>默认任务超时（秒）；未配置时为 null。</summary>
    public int? DefaultTimeoutSeconds { get; init; }

    /// <summary>是否为当前激活环境。</summary>
    public bool IsActive { get; init; }
}
