namespace LinkForge.Application.Diagnostics;

/// <summary>
/// 一次诊断任务的执行选项。
/// </summary>
/// <param name="DefaultStepTimeout">步骤计划未指定超时时的默认单步超时。</param>
/// <param name="OverallTimeout">整任务超时；null 表示不限制（由用户手动取消）。</param>
/// <remarks>
/// <para>
/// 依据 DEV_STANDARDS §2.4：“每个任务必须定义超时，超时映射为 TimedOut，不得与 Failed 混用”。
/// 因此 <see cref="DefaultStepTimeout"/> 是必填的，没有“不设超时”这个选项。
/// </para>
/// <para>
/// <see cref="OverallTimeout"/> 默认 null 是刻意的：诊断任务包含人工判断环节
/// （例如等待设备响应、等待抓包完成），硬性总超时会把“慢但正常”判成失败。
/// 需要限制时由项目级配置显式给出。
/// </para>
/// </remarks>
public sealed record DiagnosticRunOptions(TimeSpan DefaultStepTimeout, TimeSpan? OverallTimeout = null)
{
    /// <summary>默认单步超时（30 秒），用于未显式指定选项的调用方。</summary>
    public static DiagnosticRunOptions Default { get; } = new(TimeSpan.FromSeconds(30));

    /// <summary>
    /// 校验取值合法性。
    /// </summary>
    /// <returns>本对象（便于链式调用）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">超时非正数，或总超时小于单步超时。</exception>
    /// <remarks>
    /// 总超时小于单步超时会让每个步骤都在完成前被总超时打断，
    /// 表现为“所有步骤都超时”，用户却以为是目标的问题。这种配置必须在启动前拒绝。
    /// </remarks>
    public DiagnosticRunOptions Validated()
    {
        if (DefaultStepTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DefaultStepTimeout),
                DefaultStepTimeout,
                "单步超时必须为正数。");
        }

        if (OverallTimeout is { } overall)
        {
            if (overall <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(OverallTimeout),
                    overall,
                    "任务总超时必须为正数。");
            }

            if (overall < DefaultStepTimeout)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(OverallTimeout),
                    overall,
                    "任务总超时不得小于单步超时，否则所有步骤都会在完成前被中止。");
            }
        }

        return this;
    }
}