using LinkForge.Domain.Diagnostics;

namespace LinkForge.Domain.Tests.Diagnostics;

/// <summary>
/// 步骤结论构造入口的契约测试（LF-003）。
/// </summary>
/// <remarks>
/// 这些断言看起来琐碎，但每一条都对应一个真实的口径风险：
/// “跳过被写成失败”、“取消被写成超时”、“异常把堆栈铺进用户可见摘要”。
/// 集中在构造入口上验证，比在 UI 层逐页检查可靠得多。
/// </remarks>
public class DiagnosticStepResultsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(1.5);

    [Fact]
    public void Skipped_should_have_no_start_time_and_zero_duration()
    {
        var result = DiagnosticStepResults.Skipped("s1", "抓包取证", "缺少 Npcap");

        result.Status.ShouldBe(DiagnosticStatus.Skipped);
        result.StartedAt.ShouldBeNull("跳过的步骤没有执行，不该有开始时间。");
        result.Duration.ShouldBe(TimeSpan.Zero);
        result.SkipReason.ShouldBe("缺少 Npcap");
        result.Summary.ShouldContain("缺少 Npcap");
    }

    [Fact]
    public void Skipped_should_require_a_reason()
    {
        var result = DiagnosticStepResults.Skipped("s1", "抓包取证", "缺少 Npcap");

        result.RequiresSkipReason.ShouldBeTrue();
    }

    [Fact]
    public void Skipped_should_not_be_classified_as_failure()
    {
        // 这是 ADR-016 / ADR-019 的核心红线。
        var result = DiagnosticStepResults.Skipped("s1", "抓包取证", "缺少 Npcap");

        result.Status.IsFailure().ShouldBeFalse("跳过不是失败。");
        result.Status.IsInconclusive().ShouldBeTrue("跳过属于“未得出结论”。");
    }

    [Fact]
    public void TimedOut_should_be_distinct_from_failed()
    {
        var result = DiagnosticStepResults.TimedOut("s1", "TCP 连接", TimeSpan.FromSeconds(5), Start, Duration);

        result.Status.ShouldBe(DiagnosticStatus.TimedOut);
        result.Status.IsFailure().ShouldBeTrue("超时计入失败类，但状态值必须与 Failed 区分。");
        result.Summary.ShouldContain("5");
        result.Summary.ShouldContain("秒");
    }

    [Fact]
    public void TimedOut_should_name_the_scope_so_users_do_not_tune_the_wrong_timeout()
    {
        var step = DiagnosticStepResults.TimedOut("s1", "TCP 连接", TimeSpan.FromSeconds(5), Start, Duration);
        var overall = DiagnosticStepResults.TimedOut(
            "s1", "TCP 连接", TimeSpan.FromSeconds(30), Start, Duration, timeoutScope: "任务总");

        step.Summary.ShouldContain("单步超时");
        overall.Summary.ShouldContain("任务总超时");
        overall.Summary.ShouldNotContain("单步超时");
    }

    [Fact]
    public void Cancelled_should_be_neither_success_nor_failure()
    {
        var result = DiagnosticStepResults.Cancelled("s1", "TCP 连接", Start, Duration);

        result.Status.ShouldBe(DiagnosticStatus.Cancelled);
        result.Status.IsFailure().ShouldBeFalse();
        result.Status.IsSuccess().ShouldBeFalse();
    }

    [Fact]
    public void Unknown_should_not_be_success_or_failure()
    {
        var result = DiagnosticStepResults.Unknown("s1", "RTP 接收", "未收到媒体包", Start, Duration);

        result.Status.IsSuccess().ShouldBeFalse("无法判定不得渲染为通过。");
        result.Status.IsFailure().ShouldBeFalse("无法判定不得渲染为失败。");
        result.Status.IsInconclusive().ShouldBeTrue();
    }

    [Fact]
    public void FromException_should_report_type_and_message_but_not_stack_trace()
    {
        var exception = new InvalidOperationException("目标主机不可达");

        var result = DiagnosticStepResults.FromException("s1", "TCP 连接", exception, Start, Duration);

        result.Status.ShouldBe(DiagnosticStatus.Failed);
        result.Summary.ShouldContain(nameof(InvalidOperationException));
        result.Summary.ShouldContain("目标主机不可达");
        result.Summary.ShouldNotContain("   at ");
        result.HasSuggestion.ShouldBeTrue("错误必须给出下一步建议（UI 规范 §5.6）。");
    }

    [Fact]
    public void Warning_should_not_count_as_failure()
    {
        var result = DiagnosticStepResults.Warning("s1", "磁盘空间", "剩余空间偏少", Start, Duration);

        result.Status.IsFailure().ShouldBeFalse("警告是“通过但需关注”，不是失败。");
        result.Status.IsSuccess().ShouldBeFalse();
        result.Status.IsInconclusive().ShouldBeFalse();
    }

    [Fact]
    public void DurationText_should_be_dash_for_unexecuted_steps()
    {
        DiagnosticStepResults.Skipped("s1", "抓包取证", "缺少 Npcap").DurationText.ShouldBe("-");
        DiagnosticStepResults.Passed("s1", "检查", "ok", Start, Duration).DurationText.ShouldBe("1.5 s");
    }

    [Fact]
    public void DurationText_should_switch_to_milliseconds_below_one_second()
    {
        var result = DiagnosticStepResults.Passed("s1", "检查", "ok", Start, TimeSpan.FromMilliseconds(250));

        result.DurationText.ShouldBe("250 ms");
    }

    [Fact]
    public void Evidence_should_default_to_empty_not_null()
    {
        var result = DiagnosticStepResults.Passed("s1", "检查", "ok", Start, Duration);

        result.EvidenceArtifacts.ShouldBeEmpty();
        result.HasEvidence.ShouldBeFalse();
    }
}