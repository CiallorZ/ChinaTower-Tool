using LinkForge.Plugin.Abstractions;

namespace LinkForge.Application.Diagnostics;

/// <summary>
/// 把适配器上报的进度补全为“带 RunId 的完整事件”后同步转发。
/// </summary>
/// <remarks>
/// <para>
/// 适配器不知道自己的 RunId 上下文时（或图省事）可以只填 StepId 与消息，
/// 由本类补齐 RunId 与时间基准。这比在每个适配器里重复拼 RunId 更不容易漏。
/// </para>
/// <para>
/// 刻意<b>不</b>使用 <c>Progress&lt;T&gt;</c>：它会在构造时捕获
/// <see cref="SynchronizationContext"/> 并异步投递，于是
/// “步骤结束”事件可能晚于任务结果返回才到达订阅方，出现
/// “结果已经出来了，进度条还在跑”的错序。
/// 这里同步转发，线程归属完全由调用方注入的 <see cref="IProgress{T}"/> 决定
/// （WPF 侧注入 <c>Progress&lt;T&gt;</c> 即可回到 UI 线程）。
/// </para>
/// </remarks>
internal sealed class StepProgressRelay : IProgress<DiagnosticProgress>
{
    private readonly IProgress<DiagnosticProgress> _inner;
    private readonly string _runId;
    private readonly string _stepId;
    private readonly DateTimeOffset _runStartedAt;

    /// <summary>
    /// 创建转发器。
    /// </summary>
    /// <param name="inner">下游进度接收方。</param>
    /// <param name="runId">运行标识。</param>
    /// <param name="stepId">当前步骤标识。</param>
    /// <param name="runStartedAt">任务开始时间（用于计算已运行时长）。</param>
    public StepProgressRelay(
        IProgress<DiagnosticProgress> inner,
        string runId,
        string stepId,
        DateTimeOffset runStartedAt)
    {
        _inner = inner;
        _runId = runId;
        _stepId = stepId;
        _runStartedAt = runStartedAt;
    }

    /// <inheritdoc />
    public void Report(DiagnosticProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var now = DateTimeOffset.Now;

        _inner.Report(value with
        {
            RunId = string.IsNullOrEmpty(value.RunId) ? _runId : value.RunId,
            StepId = string.IsNullOrEmpty(value.StepId) ? _stepId : value.StepId,
            Elapsed = now - _runStartedAt
        });
    }
}