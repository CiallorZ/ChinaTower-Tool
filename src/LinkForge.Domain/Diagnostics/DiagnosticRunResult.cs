namespace LinkForge.Domain.Diagnostics;

/// <summary>
/// 一次诊断任务的完整结果（任务层）。
/// </summary>
/// <remarks>
/// <para>
/// 依据 <c>docs/ARCHITECTURE.md</c> §3.2。任务层状态由
/// <see cref="DiagnosticRunStateMapper"/> 从步骤结论<b>聚合</b>得到，而不是单独存一个字段：
/// 存两份必然漂移（改了一处忘另一处），聚合则永远一致。
/// </para>
/// <para>
/// <see cref="WasCancelled"/> 必须持久化：一个"所有步骤都通过但随后被取消"的任务
/// 若丢掉取消标记，重新聚合会得到"已完成"，那就是把用户的中断报成成功。
/// </para>
/// </remarks>
public sealed record DiagnosticRunResult
{
    /// <summary>运行标识（历史与日志关联用）。</summary>
    public required string RunId { get; init; }

    /// <summary>产生该结果的适配器标识，例如 <c>environment.selfcheck</c>。</summary>
    public required string AdapterId { get; init; }

    /// <summary>适配器显示名称，例如"环境自检"。</summary>
    public required string DisplayName { get; init; }

    /// <summary>目标描述（面向用户），例如"本机"、"10.0.0.8:554"。</summary>
    public required string TargetLabel { get; init; }

    /// <summary>开始时间。</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>结束时间；仍在执行或未落盘时为 null。</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>各步骤结论（顺序即执行顺序）。</summary>
    public required IReadOnlyList<DiagnosticStepResult> Steps { get; init; }

    /// <summary>是否被用户取消。</summary>
    public bool WasCancelled { get; init; }

    /// <summary>证据根目录（绝对路径）；未产出证据时为 null。</summary>
    public string? EvidenceDirectory { get; init; }

    /// <summary>任务层状态（由步骤结论聚合得到）。</summary>
    public TaskRunState State =>
        DiagnosticRunStateMapper.Map(new DiagnosticRunSnapshot(
            [.. Steps.Select(static s => s.Status)],
            IsStarted: true,
            IsPreparing: false,
            IsCancelled: WasCancelled,
            IsRunning: false));

    /// <summary>耗时；未结束时为 null。</summary>
    public TimeSpan? Duration => EndedAt is { } ended ? ended - StartedAt : null;

    /// <summary>耗时文本，例如 "2.4 s"；未结束时显示 "进行中"。</summary>
    public string DurationText => Duration is { } duration
        ? duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds:0.0} s"
            : $"{duration.TotalMilliseconds:0} ms"
        : "进行中";

    /// <summary>本地开始时间文本，例如 "2026-10-16 14:03:21"。</summary>
    public string StartedAtText => StartedAt.ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>通过 / 警告 / 失败 / 跳过 / 未判定的步骤数。</summary>
    public int PassedCount => Steps.Count(static s => s.Status == DiagnosticStatus.Passed);

    /// <inheritdoc cref="PassedCount"/>
    public int WarningCount => Steps.Count(static s => s.Status == DiagnosticStatus.Warning);

    /// <inheritdoc cref="PassedCount"/>
    public int FailedCount => Steps.Count(static s => s.Status.IsFailure());

    /// <inheritdoc cref="PassedCount"/>
    public int SkippedCount => Steps.Count(static s => s.Status == DiagnosticStatus.Skipped);

    /// <inheritdoc cref="PassedCount"/>
    public int InconclusiveCount => Steps.Count(static s => s.Status == DiagnosticStatus.Unknown);

    /// <summary>步骤数统计文本，例如 "5 个步骤：2 通过 / 1 警告 / 1 失败 / 1 跳过"。</summary>
    /// <remarks>
    /// 只列出计数大于 0 的类别：全零类别堆在结论里会让用户以为缺数据。
    /// </remarks>
    public string StepSummaryText
    {
        get
        {
            var parts = new List<string>(5);

            if (PassedCount > 0)
            {
                parts.Add($"{PassedCount} 通过");
            }

            if (WarningCount > 0)
            {
                parts.Add($"{WarningCount} 警告");
            }

            if (FailedCount > 0)
            {
                parts.Add($"{FailedCount} 失败");
            }

            if (SkippedCount > 0)
            {
                parts.Add($"{SkippedCount} 跳过");
            }

            if (InconclusiveCount > 0)
            {
                parts.Add($"{InconclusiveCount} 未判定");
            }

            var detail = parts.Count == 0 ? "无步骤结论" : string.Join(" / ", parts);

            return $"{Steps.Count} 个步骤：{detail}";
        }
    }

    /// <summary>是否存在证据文件。</summary>
    public bool HasEvidence => Steps.Any(static s => s.HasEvidence);
}
