using LinkForge.Domain.Diagnostics;

namespace LinkForge.Domain.Tests.Diagnostics;

/// <summary>
/// 任务层状态聚合测试（ADR-016、ADR-019）。
/// </summary>
public class DiagnosticRunStateMapperTests
{
    private static DiagnosticRunSnapshot Snapshot(
        DiagnosticStatus[] steps,
        bool isStarted = true,
        bool isPreparing = false,
        bool isCancelled = false,
        bool isRunning = false) =>
        new(steps, isStarted, isPreparing, isCancelled, isRunning);

    [Fact]
    public void Not_started_should_be_pending_even_with_steps_present()
    {
        var state = DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Passed, DiagnosticStatus.Failed], isStarted: false));

        state.ShouldBe(TaskRunState.Pending);
    }

    [Fact]
    public void Started_running_without_steps_and_preparing_should_be_preparing()
    {
        var state = DiagnosticRunStateMapper.Map(
            Snapshot([], isPreparing: true, isRunning: true));

        state.ShouldBe(TaskRunState.Preparing);
    }

    [Fact]
    public void Started_running_should_be_running()
    {
        DiagnosticRunStateMapper.Map(Snapshot([DiagnosticStatus.Running], isRunning: true))
            .ShouldBe(TaskRunState.Running);
    }

    [Fact]
    public void Cancelled_should_win_over_running()
    {
        var state = DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Running], isCancelled: true, isRunning: true));

        state.ShouldBe(TaskRunState.Cancelled);
    }

    [Fact]
    public void All_passed_should_be_completed()
    {
        DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Passed, DiagnosticStatus.Passed]))
            .ShouldBe(TaskRunState.Completed);
    }

    [Fact]
    public void Passed_with_warning_should_be_completed()
    {
        // 警告不是失败：全部通过且仅有警告时，任务仍算完成。
        DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Passed, DiagnosticStatus.Warning]))
            .ShouldBe(TaskRunState.Completed);
    }

    [Fact]
    public void Passed_with_failure_should_be_partially_succeeded()
    {
        DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Passed, DiagnosticStatus.Failed]))
            .ShouldBe(TaskRunState.PartiallySucceeded);
    }

    [Fact]
    public void Only_failures_should_be_failed()
    {
        DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Failed, DiagnosticStatus.TimedOut]))
            .ShouldBe(TaskRunState.Failed);
    }

    [Fact]
    public void Only_inconclusive_steps_should_not_be_failed_or_completed()
    {
        // ADR-016 红线：Skipped/Unknown 既不是失败也不是完成。
        var state = DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Skipped, DiagnosticStatus.Unknown]));

        state.ShouldBe(TaskRunState.Inconclusive);
        state.ShouldNotBe(TaskRunState.Failed);
        state.ShouldNotBe(TaskRunState.Completed);
    }

    [Fact]
    public void Passed_with_skipped_step_should_be_partially_succeeded_not_completed()
    {
        // 有步骤被跳过时不得宣称"全部完成"，否则等于把未覆盖的部分当作已验证。
        DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Passed, DiagnosticStatus.Skipped]))
            .ShouldBe(TaskRunState.PartiallySucceeded);
    }

    [Fact]
    public void Passed_with_unknown_step_should_be_partially_succeeded_not_completed()
    {
        DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Passed, DiagnosticStatus.Unknown]))
            .ShouldBe(TaskRunState.PartiallySucceeded);
    }

    [Fact]
    public void Interrupted_execution_should_be_inconclusive_not_completed()
    {
        // 已不在运行，但仍有步骤停留在 Running/NotRun：属于被中断，不得宣称完成或失败。
        var state = DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Passed, DiagnosticStatus.Running]));

        state.ShouldBe(TaskRunState.Inconclusive);
    }

    [Fact]
    public void Empty_steps_without_running_should_be_pending()
    {
        DiagnosticRunStateMapper.Map(Snapshot([])).ShouldBe(TaskRunState.Pending);
    }

    [Fact]
    public void Failed_with_skipped_step_should_still_be_failed()
    {
        // 有失败且无通过：失败结论优先，跳过项不改变"整体失败"的判定。
        DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Failed, DiagnosticStatus.Skipped]))
            .ShouldBe(TaskRunState.Failed);
    }

    [Fact]
    public void Null_steps_should_be_treated_as_empty()
    {
        var state = DiagnosticRunStateMapper.Map(new DiagnosticRunSnapshot(null!, true, false, false, false));

        state.ShouldBe(TaskRunState.Pending);
    }

    [Fact]
    public void Cancelled_with_only_skipped_steps_should_be_cancelled()
    {
        // 用户取消是明确结论，优先于"无有效结论"。
        DiagnosticRunStateMapper.Map(
            Snapshot([DiagnosticStatus.Skipped], isCancelled: true))
            .ShouldBe(TaskRunState.Cancelled);
    }

    [Fact]
    public void Every_step_status_should_map_to_a_defined_task_state()
    {
        // 穷举单个步骤状态，确保没有任何取值落到未定义分支。
        foreach (var status in Enum.GetValues<DiagnosticStatus>())
        {
            var state = DiagnosticRunStateMapper.Map(Snapshot([status]));

            Enum.IsDefined(state).ShouldBeTrue($"步骤状态 {status} 聚合出了未定义的 {state}。");
        }
    }
}
