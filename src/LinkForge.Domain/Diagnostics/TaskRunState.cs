namespace LinkForge.Domain.Diagnostics;

/// <summary>
/// 一次诊断任务的整体执行状态（任务层状态）。
/// </summary>
/// <remarks>
/// 依据 <c>docs/DECISIONS.md</c> ADR-016 与 ADR-019。
/// 与步骤层 <see cref="DiagnosticStatus"/> 分离，不得合并。
/// </remarks>
public enum TaskRunState
{
    /// <summary>尚未开始。</summary>
    Pending = 0,

    /// <summary>准备中（解析参数、建立连接、加载依赖等）。</summary>
    Preparing = 1,

    /// <summary>执行中。</summary>
    Running = 2,

    /// <summary>全部步骤通过（允许存在 <see cref="DiagnosticStatus.Warning"/>，但不得有失败）。</summary>
    Completed = 3,

    /// <summary>部分成功：至少一个步骤通过，且至少一个步骤失败或超时。</summary>
    PartiallySucceeded = 4,

    /// <summary>失败：无步骤通过，且存在失败或超时。</summary>
    Failed = 5,

    /// <summary>被用户取消。</summary>
    Cancelled = 6,

    /// <summary>
    /// 无有效结论：所有步骤都被跳过或未能判定，既无通过也无失败。
    /// </summary>
    /// <remarks>
    /// 见 ADR-019。该值存在的意义：ADR-016 原映射表未覆盖"无任何确定性结论"的情形，
    /// 而把这种情形归入 <see cref="Completed"/>（违反"Unknown 不得归入 Completed"）
    /// 或 <see cref="Failed"/>（违反"不得把 Skipped/Unknown 渲染为 Failed"）都是错误的，
    /// 因此显式建模，而不是让实现者自行兜底。
    /// </remarks>
    Inconclusive = 7
}
