namespace LinkForge.Domain.Diagnostics;

/// <summary>
/// 任务层状态的聚合输入。
/// </summary>
/// <param name="Steps">各步骤的当前结论。</param>
/// <param name="IsStarted">任务是否已开始执行。</param>
/// <param name="IsPreparing">任务是否处于准备阶段（仅在已开始且尚无步骤结论时生效）。</param>
/// <param name="IsCancelled">任务是否被用户取消。</param>
/// <param name="IsRunning">任务是否仍在执行。</param>
public readonly record struct DiagnosticRunSnapshot(
    IReadOnlyList<DiagnosticStatus> Steps,
    bool IsStarted,
    bool IsPreparing,
    bool IsCancelled,
    bool IsRunning);

/// <summary>
/// 把步骤层结论聚合为任务层状态。
/// </summary>
/// <remarks>
/// 依据 <c>docs/DECISIONS.md</c> ADR-016（映射规则）与 ADR-019（补充"无有效结论"）。
///
/// 该聚合是纯函数，不依赖 WPF，因此可用单元测试穷举所有组合，
/// 保证 UI 呈现的口径与领域规则一致——而不是各页面各写一套 if/else。
/// </remarks>
public static class DiagnosticRunStateMapper
{
    /// <summary>
    /// 计算任务层状态。
    /// </summary>
    /// <param name="snapshot">任务与步骤的当前快照。</param>
    /// <returns>聚合后的任务状态。</returns>
    public static TaskRunState Map(DiagnosticRunSnapshot snapshot)
    {
        var steps = snapshot.Steps ?? [];

        // 未开始：无论步骤列表如何，都还没跑。
        if (!snapshot.IsStarted)
        {
            return TaskRunState.Pending;
        }

        // 取消优先于"运行中"：用户取消后即使尚有步骤处于 Running 也不应继续显示运行。
        if (snapshot.IsCancelled)
        {
            return TaskRunState.Cancelled;
        }

        if (snapshot.IsRunning)
        {
            return snapshot.IsPreparing && steps.Count == 0
                ? TaskRunState.Preparing
                : TaskRunState.Running;
        }

        var hasPassed = steps.Any(static s => s.IsSuccess());
        var hasFailure = steps.Any(static s => s.IsFailure());
        var hasInconclusive = steps.Any(static s => s.IsInconclusive());
        var hasPendingOrRunning = steps.Any(static s => s.IsPending() || s.IsInProgress());

        // 已判定为"未运行中"，但仍有步骤未到达终态：说明执行被中断，
        // 此时不得宣称完成或失败（例如进程崩溃、外部中断）。
        if (hasPendingOrRunning)
        {
            return TaskRunState.Inconclusive;
        }

        if (hasFailure)
        {
            return hasPassed ? TaskRunState.PartiallySucceeded : TaskRunState.Failed;
        }

        if (hasPassed)
        {
            // 存在通过、且无失败：若还有跳过/未判定项，结论是"部分成功"而非"全部完成"，
            // 避免把"未覆盖的部分"当作"已验证通过"。
            return hasInconclusive ? TaskRunState.PartiallySucceeded : TaskRunState.Completed;
        }

        // 无通过、无失败。
        if (hasInconclusive)
        {
            return TaskRunState.Inconclusive;
        }

        // 空步骤列表且未运行：没有做过任何事，视为尚未开始。
        return TaskRunState.Pending;
    }
}
