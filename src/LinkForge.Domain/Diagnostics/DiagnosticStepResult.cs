namespace LinkForge.Domain.Diagnostics;

/// <summary>
/// 一个诊断步骤的执行结论。
/// </summary>
/// <param name="StepId">步骤标识（稳定，不随文案变化）。</param>
/// <param name="Name">步骤名称（面向用户）。</param>
/// <param name="Status">步骤结论。</param>
/// <param name="StartedAt">开始时间；未执行时为 null。</param>
/// <param name="Duration">耗时；未执行时为 <see cref="TimeSpan.Zero"/>。</param>
/// <param name="Summary">结论摘要（面向用户的一句话）。</param>
/// <remarks>
/// <para>
/// 依据 <c>docs/ARCHITECTURE.md</c> §3.2 与 <c>docs/DECISIONS.md</c> ADR-016。
/// 本类型是<b>已结束</b>步骤的结论快照；"正在执行"由进度事件表达，不落在这里。
/// </para>
/// <para>
/// 三个字段的组合是有意的：
/// <list type="bullet">
///   <item><see cref="Summary"/> 说明发生了什么（面向用户）。</item>
///   <item><see cref="NextStepSuggestion"/> 说明下一步该做什么（UI 规范 §5.6 要求错误必须给出可执行建议）。</item>
///   <item><see cref="SkipReason"/> 只在 <see cref="DiagnosticStatus.Skipped"/> 时出现，
///         因为"跳过"必须解释原因，否则用户无法判断这是有意设计还是缺陷。</item>
/// </list>
/// </para>
/// <para>
/// 所有面向用户的文本都应由创建方保证已脱敏（见 <c>LinkForge.Application</c> 的任务编排：
/// 结论在进入结果对象前统一过脱敏，UI、历史与报告共用同一份文本）。
/// </para>
/// </remarks>
public sealed record DiagnosticStepResult(
    string StepId,
    string Name,
    DiagnosticStatus Status,
    DateTimeOffset? StartedAt,
    TimeSpan Duration,
    string Summary,
    string? NextStepSuggestion = null,
    string? SkipReason = null,
    IReadOnlyList<EvidenceArtifact>? Evidence = null)
{
    /// <summary>证据文件列表（永不为 null）。</summary>
    public IReadOnlyList<EvidenceArtifact> EvidenceArtifacts => Evidence ?? [];

    /// <summary>是否存在证据文件。</summary>
    public bool HasEvidence => Evidence is { Count: > 0 };

    /// <summary>是否提供了下一步建议。</summary>
    public bool HasSuggestion => !string.IsNullOrWhiteSpace(NextStepSuggestion);

    /// <summary>耗时文本，例如 "0.8 s"；未执行时显示 "-"。</summary>
    public string DurationText => Status == DiagnosticStatus.NotRun || StartedAt is null
        ? "-"
        : Duration.TotalSeconds >= 1
            ? $"{Duration.TotalSeconds:0.0} s"
            : $"{Duration.TotalMilliseconds:0} ms";

    /// <summary>是否必须向用户解释跳过原因。</summary>
    public bool RequiresSkipReason => Status == DiagnosticStatus.Skipped;
}

/// <summary>
/// 步骤结论的构造入口。
/// </summary>
/// <remarks>
/// 集中构造而不是让各处直接 new：状态、文案与建议的搭配规则只写一遍，
/// 避免出现"某个页面把取消写成失败"或"超时不给建议"这类口径漂移。
/// </remarks>
public static class DiagnosticStepResults
{
    /// <summary>构造"通过"结论。</summary>
    public static DiagnosticStepResult Passed(
        string stepId,
        string name,
        string summary,
        DateTimeOffset startedAt,
        TimeSpan duration,
        IReadOnlyList<EvidenceArtifact>? evidence = null) =>
        new(stepId, name, DiagnosticStatus.Passed, startedAt, duration, summary, null, null, evidence);

    /// <summary>构造"警告"结论：通过但需关注，不计入失败。</summary>
    public static DiagnosticStepResult Warning(
        string stepId,
        string name,
        string summary,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string? suggestion = null,
        IReadOnlyList<EvidenceArtifact>? evidence = null) =>
        new(stepId, name, DiagnosticStatus.Warning, startedAt, duration, summary, suggestion, null, evidence);

    /// <summary>构造"失败"结论。</summary>
    public static DiagnosticStepResult Failed(
        string stepId,
        string name,
        string summary,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string? suggestion = null,
        IReadOnlyList<EvidenceArtifact>? evidence = null) =>
        new(stepId, name, DiagnosticStatus.Failed, startedAt, duration, summary, suggestion, null, evidence);

    /// <summary>
    /// 构造"跳过"结论。
    /// </summary>
    /// <param name="stepId">步骤标识。</param>
    /// <param name="name">步骤名称。</param>
    /// <param name="reason">跳过原因（必填，且会写入摘要）。</param>
    /// <remarks>
    /// 跳过的步骤没有开始时间与耗时（它确实没有执行），
    /// 因此 <see cref="DiagnosticStepResult.StartedAt"/> 为 null、耗时为 0。
    /// </remarks>
    public static DiagnosticStepResult Skipped(string stepId, string name, string reason) =>
        new(
            stepId,
            name,
            DiagnosticStatus.Skipped,
            null,
            TimeSpan.Zero,
            $"已跳过：{reason}",
            null,
            reason);

    /// <summary>构造"未判定"结论。</summary>
    public static DiagnosticStepResult Unknown(
        string stepId,
        string name,
        string summary,
        DateTimeOffset? startedAt,
        TimeSpan duration,
        string? suggestion = null) =>
        new(stepId, name, DiagnosticStatus.Unknown, startedAt, duration, summary, suggestion);

    /// <summary>构造"超时"结论。</summary>
    /// <param name="stepId">步骤标识。</param>
    /// <param name="name">步骤名称。</param>
    /// <param name="timeout">被触发的超时值。</param>
    /// <param name="startedAt">开始时间。</param>
    /// <param name="duration">耗时。</param>
    /// <param name="suggestion">下一步建议。</param>
    /// <param name="timeoutScope">
    /// 超时所属范围，默认"单步"。
    /// 任务总超时也会中止步骤，但两者原因不同（一个是目标慢，一个是整体预算耗尽），
    /// 文案必须区分，否则用户会去调单步超时却解决不了问题。
    /// </param>
    public static DiagnosticStepResult TimedOut(
        string stepId,
        string name,
        TimeSpan timeout,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string? suggestion = null,
        string timeoutScope = "单步") =>
        new(
            stepId,
            name,
            DiagnosticStatus.TimedOut,
            startedAt,
            duration,
            $"超过{timeoutScope}超时 {FormatTimeout(timeout)}，已中止该步骤。",
            suggestion ?? "可提高超时时间或检查目标可达性后重试。");

    /// <summary>构造"已取消"结论。</summary>
    public static DiagnosticStepResult Cancelled(
        string stepId,
        string name,
        DateTimeOffset startedAt,
        TimeSpan duration) =>
        new(
            stepId,
            name,
            DiagnosticStatus.Cancelled,
            startedAt,
            duration,
            "用户取消了任务，该步骤已中止。",
            "如需完整结论，请重新运行。");

    /// <summary>
    /// 构造"因异常失败"的结论。
    /// </summary>
    /// <param name="stepId">步骤标识。</param>
    /// <param name="name">步骤名称。</param>
    /// <param name="exception">原始异常（用于提取可读原因）。</param>
    /// <param name="startedAt">开始时间。</param>
    /// <param name="duration">耗时。</param>
    /// <remarks>
    /// 面向用户的部分只放异常类型与消息，堆栈留在技术详情里：
    /// UI 规范 §5.6 要求"错误必须包含可读原因 + 原始信息 + 下一步建议"，
    /// 而把堆栈直接铺在摘要里既不可读，也容易把内部路径与凭据带出去。
    /// </remarks>
    public static DiagnosticStepResult FromException(
        string stepId,
        string name,
        Exception exception,
        DateTimeOffset startedAt,
        TimeSpan duration) =>
        new(
            stepId,
            name,
            DiagnosticStatus.Failed,
            startedAt,
            duration,
            $"步骤执行失败：{exception.GetType().Name} — {exception.Message}",
            "查看技术详情确认原因；若为依赖或权限问题，请先修复环境后重试。");

    /// <summary>把超时格式化为可读文本，例如 "30 s"、"5 min"。</summary>
    internal static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalMinutes >= 1
            ? $"{timeout.TotalMinutes:0.#} 分钟"
            : $"{timeout.TotalSeconds:0.#} 秒";
}
