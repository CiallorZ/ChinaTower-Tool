using LinkForge.Domain.Diagnostics;

namespace LinkForge.Domain.Tests.Diagnostics;

/// <summary>
/// 步骤层状态分类测试（ADR-016）。
/// </summary>
public class DiagnosticStatusTests
{
    [Theory]
    [InlineData(DiagnosticStatus.Failed)]
    [InlineData(DiagnosticStatus.TimedOut)]
    public void Failure_statuses_should_be_classified_as_failure(DiagnosticStatus status)
    {
        status.IsFailure().ShouldBeTrue();
        status.IsSuccess().ShouldBeFalse();
        status.IsInconclusive().ShouldBeFalse();
    }

    [Fact]
    public void Passed_should_be_success_but_not_failure()
    {
        DiagnosticStatus.Passed.IsSuccess().ShouldBeTrue();
        DiagnosticStatus.Passed.IsFailure().ShouldBeFalse();
    }

    [Theory]
    [InlineData(DiagnosticStatus.Skipped)]
    [InlineData(DiagnosticStatus.Unknown)]
    public void Skipped_and_unknown_should_be_inconclusive_not_failure(DiagnosticStatus status)
    {
        // ADR-016 红线：Skipped 不得计入失败；Unknown 不得归入 Completed。
        status.IsInconclusive().ShouldBeTrue();
        status.IsFailure().ShouldBeFalse();
        status.IsSuccess().ShouldBeFalse();
    }

    [Fact]
    public void Warning_should_be_neither_success_nor_failure()
    {
        // 警告是"通过但需关注"：不能当作失败，也不参与"全部通过"判定之外的特殊处理。
        DiagnosticStatus.Warning.IsFailure().ShouldBeFalse();
        DiagnosticStatus.Warning.IsSuccess().ShouldBeFalse();
        DiagnosticStatus.Warning.IsInconclusive().ShouldBeFalse();
    }

    [Fact]
    public void Cancelled_should_not_be_treated_as_failure_or_success()
    {
        DiagnosticStatus.Cancelled.IsFailure().ShouldBeFalse();
        DiagnosticStatus.Cancelled.IsSuccess().ShouldBeFalse();
    }

    [Fact]
    public void Terminal_should_cover_exactly_the_finished_statuses()
    {
        var terminal = Enum.GetValues<DiagnosticStatus>().Where(s => s.IsTerminal()).ToArray();

        terminal.ShouldBe(
        [
            DiagnosticStatus.Passed,
            DiagnosticStatus.Failed,
            DiagnosticStatus.Warning,
            DiagnosticStatus.Skipped,
            DiagnosticStatus.Unknown,
            DiagnosticStatus.Cancelled,
            DiagnosticStatus.TimedOut
        ], ignoreOrder: true);
    }

    [Fact]
    public void NotRun_and_running_should_not_be_terminal()
    {
        DiagnosticStatus.NotRun.IsTerminal().ShouldBeFalse();
        DiagnosticStatus.Running.IsTerminal().ShouldBeFalse();
        DiagnosticStatus.Running.IsInProgress().ShouldBeTrue();
        DiagnosticStatus.NotRun.IsPending().ShouldBeTrue();
    }

    [Fact]
    public void Status_classification_should_partition_every_value_exactly_once()
    {
        // 防止后续新增状态时忘记归入某一类，导致 UI 映射出现"无匹配分支"。
        foreach (var status in Enum.GetValues<DiagnosticStatus>())
        {
            var buckets = new[]
            {
                status.IsSuccess(),
                status.IsFailure(),
                status.IsInconclusive(),
                status.IsInProgress(),
                status.IsPending(),
                status == DiagnosticStatus.Warning,
                status == DiagnosticStatus.Cancelled
            }.Count(static b => b);

            buckets.ShouldBe(
                1,
                $"状态 {status} 必须且只能归入一个语义类别，实际命中 {buckets} 类。");
        }
    }
}
