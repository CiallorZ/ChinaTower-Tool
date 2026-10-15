namespace LinkForge.Domain.Diagnostics;

/// <summary>
/// 单个诊断步骤的执行结论（步骤层状态）。
/// </summary>
/// <remarks>
/// 依据 <c>docs/ARCHITECTURE.md</c> §3.2 与 <c>docs/DECISIONS.md</c> ADR-016。
/// 本枚举描述"一个步骤跑完之后的结论"，与任务层状态
/// （<see cref="TaskRunState"/>）语义不同，两者不得合并。
///
/// 渲染红线（UI 规范 §5.4、§5.6）：
/// <list type="bullet">
///   <item><see cref="Skipped"/> 不计入失败，但必须在结论中说明跳过原因。</item>
///   <item><see cref="Unknown"/> 表示未能获得结论，不得渲染为"通过"或"失败"。</item>
/// </list>
/// </remarks>
public enum DiagnosticStatus
{
    /// <summary>尚未执行。</summary>
    NotRun = 0,

    /// <summary>执行中。</summary>
    Running = 1,

    /// <summary>通过 / 符合预期。</summary>
    Passed = 2,

    /// <summary>失败 / 不符合预期。</summary>
    Failed = 3,

    /// <summary>通过但存在需要关注的信息（非失败）。</summary>
    Warning = 4,

    /// <summary>被跳过（依赖未满足、用户未选择等）；必须记录跳过原因。</summary>
    Skipped = 5,

    /// <summary>未能判定（证据不足、目标无响应且无法区分原因等）。</summary>
    Unknown = 6,

    /// <summary>被用户取消。</summary>
    Cancelled = 7,

    /// <summary>超时。</summary>
    TimedOut = 8
}

/// <summary>
/// <see cref="DiagnosticStatus"/> 的语义分类辅助方法。
/// </summary>
/// <remarks>
/// 分类规则集中在此处，避免各页面自行判断导致口径不一致。
/// </remarks>
public static class DiagnosticStatusExtensions
{
    /// <summary>是否为失败类结论（用于任务层聚合）。</summary>
    public static bool IsFailure(this DiagnosticStatus status) =>
        status is DiagnosticStatus.Failed or DiagnosticStatus.TimedOut;

    /// <summary>是否为成功类结论。</summary>
    public static bool IsSuccess(this DiagnosticStatus status) =>
        status is DiagnosticStatus.Passed;

    /// <summary>是否属于"未能得出结论"（既非成功也非失败）。</summary>
    public static bool IsInconclusive(this DiagnosticStatus status) =>
        status is DiagnosticStatus.Skipped or DiagnosticStatus.Unknown;

    /// <summary>是否仍在执行。</summary>
    public static bool IsInProgress(this DiagnosticStatus status) =>
        status is DiagnosticStatus.Running;

    /// <summary>是否尚未开始。</summary>
    public static bool IsPending(this DiagnosticStatus status) =>
        status is DiagnosticStatus.NotRun;

    /// <summary>是否已到达终态（不会再变化）。</summary>
    public static bool IsTerminal(this DiagnosticStatus status) =>
        status is DiagnosticStatus.Passed
            or DiagnosticStatus.Failed
            or DiagnosticStatus.Warning
            or DiagnosticStatus.Skipped
            or DiagnosticStatus.Unknown
            or DiagnosticStatus.Cancelled
            or DiagnosticStatus.TimedOut;
}
