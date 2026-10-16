using LinkForge.Domain.Diagnostics;

namespace LinkForge.Plugin.Abstractions;

/// <summary>
/// 一个诊断步骤的执行计划。
/// </summary>
/// <param name="StepId">步骤标识（稳定，不随文案变化）。</param>
/// <param name="Name">步骤名称（面向用户）。</param>
/// <param name="RequiredDependencies">该步骤依赖的外部组件标识（如 <c>npcap</c>）；为空表示无依赖。</param>
/// <param name="DependsOnSteps">该步骤的前置步骤标识；前置步骤未成功时本步骤跳过。</param>
/// <param name="Timeout">单步超时。</param>
/// <param name="IsOptional">是否为可选步骤（跳过不影响任务整体成功判定）。</param>
/// <remarks>
/// <para>
/// 步骤计划由<b>适配器声明</b>、由<b>编排器执行</b>。这个分工是刻意的：
/// 依赖判定、超时、取消、跳过原因这些跨模块统一规则只实现一次（在编排器里），
/// 适配器只描述"我需要什么、我做什么"，不必各自重复实现一遍超时与取消。
/// </para>
/// <para>
/// <see cref="IsOptional"/> 只影响呈现口径，不影响状态聚合：
/// 可选步骤失败仍然计入失败。把它做成"可选失败不算失败"会让结论失真。
/// </para>
/// </remarks>
public sealed record DiagnosticStepPlan(
    string StepId,
    string Name,
    IReadOnlyList<string>? RequiredDependencies = null,
    IReadOnlyList<string>? DependsOnSteps = null,
    TimeSpan? Timeout = null,
    bool IsOptional = false)
{
    /// <summary>依赖组件标识列表（永不为 null）。</summary>
    public IReadOnlyList<string> Dependencies => RequiredDependencies ?? [];

    /// <summary>前置步骤标识列表（永不为 null）。</summary>
    public IReadOnlyList<string> Prerequisites => DependsOnSteps ?? [];
}

/// <summary>
/// 一次诊断请求。
/// </summary>
/// <param name="RunId">运行标识（由编排器生成，用于日志与历史关联）。</param>
/// <param name="TargetLabel">目标描述（面向用户），例如"本机"、"10.0.0.8:554"。</param>
/// <param name="Parameters">参数表；值为 null 表示"未提供"。</param>
/// <param name="DefaultStepTimeout">未在步骤计划中指定超时时的默认值。</param>
/// <remarks>
/// 参数值可能是秘密（如设备密码）。编排器<b>不</b>把参数写入日志或历史，
/// 适配器也不得把参数原文放进结论摘要；结论文本在离开编排器前统一脱敏，
/// 但脱敏是最后一道防线，不是允许随手透传的理由。
/// </remarks>
public sealed record DiagnosticRequest(
    string RunId,
    string TargetLabel,
    IReadOnlyDictionary<string, string?>? Parameters = null,
    TimeSpan? DefaultStepTimeout = null)
{
    /// <summary>参数表（永不为 null）。</summary>
    public IReadOnlyDictionary<string, string?> ParameterMap =>
        Parameters ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取一个参数；不存在时返回 null。</summary>
    /// <param name="key">参数名（大小写不敏感）。</param>
    public string? GetParameter(string key)
    {
        foreach (var pair in ParameterMap)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// 单个步骤的执行上下文。
/// </summary>
/// <param name="RunId">运行标识。</param>
/// <param name="StepId">步骤标识。</param>
/// <param name="Name">步骤名称。</param>
/// <param name="TargetLabel">目标描述。</param>
/// <param name="Parameters">参数表。</param>
/// <param name="StartedAt">步骤开始时间（由编排器给出，适配器不得自行取时钟）。</param>
/// <remarks>
/// <see cref="StartedAt"/> 由编排器注入而不是适配器自己调 <c>DateTimeOffset.Now</c>：
/// 一是保证同一任务内时间基准一致（跨步骤耗时可直接相加），
/// 二是让测试能注入确定的时间而不必等待真实时间流逝。
/// </remarks>
public sealed record DiagnosticStepContext(
    string RunId,
    string StepId,
    string Name,
    string TargetLabel,
    IReadOnlyDictionary<string, string?> Parameters,
    DateTimeOffset StartedAt)
{
    /// <summary>读取一个参数；不存在时返回 null。</summary>
    public string? GetParameter(string key)
    {
        foreach (var pair in Parameters)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}

/// <summary>
/// 进度阶段。
/// </summary>
public enum DiagnosticStepPhase
{
    /// <summary>步骤即将开始。</summary>
    Starting = 0,

    /// <summary>步骤执行中（阶段性说明）。</summary>
    Running = 1,

    /// <summary>步骤结束（结果随 <see cref="DiagnosticStepResult"/> 一起给出）。</summary>
    Finished = 2
}

/// <summary>
/// 一条进度事件。
/// </summary>
/// <param name="RunId">运行标识。</param>
/// <param name="StepId">步骤标识；整体阶段事件时为空字符串。</param>
/// <param name="Phase">阶段。</param>
/// <param name="Message">面向用户的说明，例如"正在建立 TCP 连接…"。</param>
/// <param name="Timestamp">事件时间（由编排器给出）。</param>
/// <param name="Elapsed">任务已运行时间。</param>
/// <remarks>
/// 进度<b>不</b>包含百分比：诊断步骤的真实耗时无法可靠预估，
/// 编造一个"37%"只会误导用户（UI 规范 §4.2 明确禁止伪造百分比）。
/// </remarks>
public sealed record DiagnosticProgress(
    string RunId,
    string StepId,
    DiagnosticStepPhase Phase,
    string Message,
    DateTimeOffset Timestamp,
    TimeSpan Elapsed);

/// <summary>
/// 诊断适配器契约（架构文档 §3.3）。
/// </summary>
/// <remarks>
/// <para>
/// 实现约束：
/// <list type="number">
///   <item>不得在 UI 线程做阻塞 I/O；所有操作 <c>async</c> 并响应 <see cref="CancellationToken"/>。</item>
///   <item>不得把凭据写入 <see cref="DiagnosticStepResult.Summary"/> 或证据文件。</item>
///   <item>无法判定时必须返回 <see cref="DiagnosticStatus.Unknown"/>，不得猜成通过或失败。</item>
///   <item>未执行的步骤由编排器标记 <see cref="DiagnosticStatus.Skipped"/>，适配器不要自己造跳过项。</item>
/// </list>
/// </para>
/// <para>
/// 阶段 04 只落地进程内契约；原生 SDK（海康 / 大华）按 ADR-011 走进程外隔离，
/// 届时通过一个进程外代理适配器实现同一接口，调用方无感。
/// </para>
/// </remarks>
public interface IDiagnosticAdapter
{
    /// <summary>稳定标识，例如 <c>environment.selfcheck</c>。</summary>
    string Id { get; }

    /// <summary>显示名称，例如"运行环境自检"。</summary>
    string DisplayName { get; }

    /// <summary>契约版本；不兼容时宿主拒绝加载。</summary>
    Version ApiVersion { get; }

    /// <summary>能力声明（如 <c>network.tcp</c>、<c>video.rtsp</c>）。</summary>
    IReadOnlyCollection<string> Capabilities { get; }

    /// <summary>
    /// 声明本次请求要执行的步骤。
    /// </summary>
    /// <param name="request">诊断请求。</param>
    /// <returns>按执行顺序排列的步骤计划。</returns>
    /// <remarks>
    /// 计划阶段<b>不应</b>做耗时工作（连接、探测）：工作台需要在用户点击前
    /// 就能展示"将执行哪些步骤"，一个会阻塞的 <c>Plan</c> 会让界面卡住。
    /// </remarks>
    IReadOnlyList<DiagnosticStepPlan> Plan(DiagnosticRequest request);

    /// <summary>
    /// 执行单个步骤。
    /// </summary>
    /// <param name="context">步骤上下文。</param>
    /// <param name="progress">进度上报器；可为空实现。</param>
    /// <param name="cancellationToken">取消令牌（编排器已叠加单步超时）。</param>
    /// <returns>该步骤的结论。</returns>
    /// <remarks>
    /// 适配器应让 <see cref="OperationCanceledException"/> 正常抛出：
    /// 编排器据此区分"用户取消"与"超时"，自行吞掉异常会让两种情形都变成失败。
    /// </remarks>
    Task<DiagnosticStepResult> ExecuteStepAsync(
        DiagnosticStepContext context,
        IProgress<DiagnosticProgress> progress,
        CancellationToken cancellationToken);
}
