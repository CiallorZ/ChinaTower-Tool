using LinkForge.Domain.Diagnostics;

namespace LinkForge.Domain.Tests.Diagnostics;

/// <summary>
/// 任务结果聚合测试（LF-003）。
/// </summary>
/// <remarks>
/// 重点验证“任务层状态由步骤结论聚合得到”这一约束：
/// 结果对象不缓存状态字段，因此不可能出现“步骤改了、任务状态没跟着改”的漂移。
/// </remarks>
public class DiagnosticRunResultTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    private static DiagnosticStepResult Step(string id, DiagnosticStatus status) =>
        new(id, id, status, Start, TimeSpan.FromSeconds(1), status.ToString());

    private static DiagnosticRunResult Run(params DiagnosticStatus[] statuses) =>
        new()
        {
            RunId = "run-1",
            AdapterId = "test",
            DisplayName = "测试适配器",
            TargetLabel = "本机",
            StartedAt = Start,
            EndedAt = Start.AddSeconds(5),
            Steps = [.. statuses.Select((s, i) => Step($"s{i}", s))]
        };

    [Fact]
    public void All_passed_should_be_completed()
    {
        Run(DiagnosticStatus.Passed, DiagnosticStatus.Passed).State.ShouldBe(TaskRunState.Completed);
    }

    [Fact]
    public void Mixed_pass_and_fail_should_be_partially_succeeded()
    {
        Run(DiagnosticStatus.Passed, DiagnosticStatus.Failed).State.ShouldBe(TaskRunState.PartiallySucceeded);
    }

    [Fact]
    public void Only_failures_should_be_failed()
    {
        Run(DiagnosticStatus.Failed, DiagnosticStatus.TimedOut).State.ShouldBe(TaskRunState.Failed);
    }

    [Fact]
    public void Skipped_only_should_be_inconclusive_not_completed_and_not_failed()
    {
        // 真实缺陷形态：所有步骤都因缺依赖被跳过时，
        // 若聚合写成“无失败即完成”，用户会以为“检查过了，没问题”。
        var state = Run(DiagnosticStatus.Skipped, DiagnosticStatus.Skipped).State;

        state.ShouldBe(TaskRunState.Inconclusive);
        state.ShouldNotBe(TaskRunState.Completed);
        state.ShouldNotBe(TaskRunState.Failed);
    }

    [Fact]
    public void Passed_plus_skipped_should_be_partially_succeeded()
    {
        Run(DiagnosticStatus.Passed, DiagnosticStatus.Skipped).State.ShouldBe(TaskRunState.PartiallySucceeded);
    }

    [Fact]
    public void Cancelled_should_win_over_other_step_statuses()
    {
        var run = Run(DiagnosticStatus.Passed, DiagnosticStatus.Running) with { WasCancelled = true };

        run.State.ShouldBe(TaskRunState.Cancelled);
    }

    [Fact]
    public void Unknown_should_not_be_treated_as_completed()
    {
        Run(DiagnosticStatus.Unknown).State.ShouldBe(TaskRunState.Inconclusive);
    }

    [Fact]
    public void Counts_should_match_the_step_statuses()
    {
        var run = Run(
            DiagnosticStatus.Passed,
            DiagnosticStatus.Warning,
            DiagnosticStatus.Failed,
            DiagnosticStatus.Skipped,
            DiagnosticStatus.Unknown);

        run.PassedCount.ShouldBe(1);
        run.WarningCount.ShouldBe(1);
        run.FailedCount.ShouldBe(1);
        run.SkippedCount.ShouldBe(1);
        run.InconclusiveCount.ShouldBe(1);
    }

    [Fact]
    public void Step_summary_text_should_omit_zero_categories()
    {
        var text = Run(DiagnosticStatus.Passed, DiagnosticStatus.Passed).StepSummaryText;

        text.ShouldContain("2 通过");
        text.ShouldNotContain("失败");
        text.ShouldNotContain("跳过");
    }

    [Fact]
    public void Step_summary_text_should_say_so_when_there_are_no_steps()
    {
        Run().StepSummaryText.ShouldContain("无步骤结论");
    }

    [Fact]
    public void Duration_should_be_null_while_the_run_has_not_ended()
    {
        var running = Run(DiagnosticStatus.Running) with { EndedAt = null };

        running.Duration.ShouldBeNull();
        running.DurationText.ShouldBe("进行中");
    }

    [Fact]
    public void Duration_text_should_be_readable_for_finished_runs()
    {
        Run(DiagnosticStatus.Passed).DurationText.ShouldBe("5.0 s");
    }

    [Fact]
    public void Has_evidence_should_reflect_step_evidence()
    {
        var artifact = new EvidenceArtifact
        {
            FileName = "environment.txt",
            RelativePath = "run-1/environment.txt",
            Sha256 = new string('a', 64),
            SizeBytes = 2048
        };

        var withEvidence = Run(DiagnosticStatus.Passed) with
        {
            Steps = [Step("s0", DiagnosticStatus.Passed) with { Evidence = [artifact] }]
        };

        withEvidence.HasEvidence.ShouldBeTrue();
        Run(DiagnosticStatus.Passed).HasEvidence.ShouldBeFalse();
    }
}