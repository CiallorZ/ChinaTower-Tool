using LinkForge.Application.Diagnostics;
using LinkForge.Domain.Diagnostics;
using LinkForge.Domain.Environment;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Application.Tests.Diagnostics;

/// <summary>
/// 任务编排器测试（LF-003 验收项）。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖验收要求：状态转换、取消、超时、依赖步骤跳过，外加异常映射与脱敏。
/// </para>
/// <para>
/// 所有测试都用假时钟推进时间，不依赖真实的 <c>Task.Delay</c> 等待：
/// 一个靠 sleep 触发超时的测试在慢机器上会偶发失败，
/// 而偶发失败会被当成“重跑就好”，最终没人再看它。
/// </para>
/// </remarks>
public class DiagnosticTaskRunnerTests
{
    private static DiagnosticTaskRunner Runner(TimeProvider? time = null) =>
        new(new PassThroughTextRedactor(), time);

    private static DiagnosticRequest Request(string target = "本机") =>
        new("run-1", target);

    private static DiagnosticRunOptions Options(int seconds = 5) =>
        new(TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task All_steps_passing_should_produce_a_completed_run()
    {
        var adapter = new FakeAdapter(
            FakeAdapter.Passed(),
            [
                new DiagnosticStepPlan("a", "步骤 A"),
                new DiagnosticStepPlan("b", "步骤 B")
            ]);

        var result = await Runner().RunAsync(
            adapter, Request(), [], Options(), cancellationToken: CancellationToken.None);

        result.State.ShouldBe(TaskRunState.Completed);
        result.Steps.Count.ShouldBe(2);
        result.Steps.ShouldAllBe(s => s.Status == DiagnosticStatus.Passed);
    }

    [Fact]
    public async Task One_failure_among_passes_should_be_partially_succeeded()
    {
        var adapter = new FakeAdapter(
            (context, _) => Task.FromResult(
                context.StepId == "b"
                    ? DiagnosticStepResults.Failed(context.StepId, context.Name, "失败", context.StartedAt, TimeSpan.Zero)
                    : DiagnosticStepResults.Passed(context.StepId, context.Name, "通过", context.StartedAt, TimeSpan.Zero)),
            [
                new DiagnosticStepPlan("a", "步骤 A"),
                new DiagnosticStepPlan("b", "步骤 B")
            ]);

        var result = await Runner().RunAsync(
            adapter, Request(), [], Options(), cancellationToken: CancellationToken.None);

        result.State.ShouldBe(TaskRunState.PartiallySucceeded);
    }

    [Fact]
    public async Task Missing_dependency_should_skip_the_step_with_a_reason()
    {
        var adapter = new FakeAdapter(
            FakeAdapter.Passed(),
            [
                new DiagnosticStepPlan("a", "步骤 A"),
                new DiagnosticStepPlan("capture", "抓包取证", RequiredDependencies: ["npcap"])
            ]);

        IReadOnlyCollection<DependencyProbe> dependencies =
        [
            new("npcap", "Npcap", false, "未检测到")
        ];

        var result = await Runner().RunAsync(
            adapter, Request(), dependencies, Options(), cancellationToken: CancellationToken.None);

        var capture = result.Steps.Single(s => s.StepId == "capture");

        capture.Status.ShouldBe(DiagnosticStatus.Skipped);
        capture.Status.IsFailure().ShouldBeFalse("依赖缺失必须与失败区分。");
        capture.SkipReason.ShouldNotBeNull().ShouldContain("Npcap");
        capture.Summary.ShouldContain("Npcap");
    }

    [Fact]
    public async Task A_skipped_step_must_not_be_executed()
    {
        var adapter = new FakeAdapter(
            FakeAdapter.Passed(),
            [
                new DiagnosticStepPlan("capture", "抓包取证", RequiredDependencies: ["npcap"])
            ]);

        IReadOnlyCollection<DependencyProbe> dependencies = [new("npcap", "Npcap", false, "未检测到")];

        await Runner().RunAsync(
            adapter, Request(), dependencies, Options(), cancellationToken: CancellationToken.None);

        adapter.ExecutedSteps.ShouldBeEmpty("被跳过的步骤不该真的被执行。");
    }

    [Fact]
    public async Task Available_dependency_should_not_skip_the_step()
    {
        var adapter = new FakeAdapter(
            FakeAdapter.Passed(),
            [new DiagnosticStepPlan("capture", "抓包取证", RequiredDependencies: ["npcap"])]);

        IReadOnlyCollection<DependencyProbe> dependencies = [new("npcap", "Npcap", true, "已安装")];

        var result = await Runner().RunAsync(
            adapter, Request(), dependencies, Options(), cancellationToken: CancellationToken.None);

        result.Steps.Single().Status.ShouldBe(DiagnosticStatus.Passed);
        adapter.ExecutedSteps.ShouldBe(["capture"]);
    }

    [Fact]
    public async Task Failed_prerequisite_should_skip_the_dependent_step()
    {
        var adapter = new FakeAdapter(
            (context, _) => Task.FromResult(
                DiagnosticStepResults.Failed(context.StepId, context.Name, "握手失败", context.StartedAt, TimeSpan.Zero)),
            [
                new DiagnosticStepPlan("handshake", "RTSP 握手"),
                new DiagnosticStepPlan("sdp", "解析 SDP", DependsOnSteps: ["handshake"])
            ]);

        var result = await Runner().RunAsync(
            adapter, Request(), [], Options(), cancellationToken: CancellationToken.None);

        result.Steps.Single(s => s.StepId == "sdp").Status.ShouldBe(DiagnosticStatus.Skipped);
        adapter.ExecutedSteps.ShouldBe(["handshake"], "前置失败后不应继续执行依赖步骤。");
    }

    [Fact]
    public async Task Warning_prerequisite_should_not_skip_the_dependent_step()
    {
        // 警告是“通过但需关注”，它不构成阻塞条件；
        // 若把警告也当成阻塞，用户会看到一串莫名其妙的跳过。
        var adapter = new FakeAdapter(
            (context, _) => Task.FromResult(
                DiagnosticStepResults.Warning(context.StepId, context.Name, "有告警但可继续", context.StartedAt, TimeSpan.Zero)),
            [
                new DiagnosticStepPlan("a", "步骤 A"),
                new DiagnosticStepPlan("b", "步骤 B", DependsOnSteps: ["a"])
            ]);

        var result = await Runner().RunAsync(
            adapter, Request(), [], Options(), cancellationToken: CancellationToken.None);

        result.Steps.Single(s => s.StepId == "b").Status.ShouldBe(DiagnosticStatus.Warning);
        adapter.ExecutedSteps.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task Unknown_prerequisite_reference_should_skip_with_an_explanation()
    {
        // 计划写错（引用了不存在的步骤）时不能静默执行：
        // 那会让“依赖顺序写错”这类缺陷永远查不出来。
        var adapter = new FakeAdapter(
            FakeAdapter.Passed(),
            [new DiagnosticStepPlan("b", "步骤 B", DependsOnSteps: ["does-not-exist"])]);

        var result = await Runner().RunAsync(
            adapter, Request(), [], Options(), cancellationToken: CancellationToken.None);

        var step = result.Steps.Single();

        step.Status.ShouldBe(DiagnosticStatus.Skipped);
        step.SkipReason.ShouldNotBeNull().ShouldContain("does-not-exist");
    }

    [Fact]
    public async Task User_cancellation_should_mark_the_step_cancelled_and_the_run_cancelled()
    {
        var adapter = new FakeAdapter(FakeAdapter.Hangs(), [new DiagnosticStepPlan("slow", "慢步骤")]);

        using var cts = new CancellationTokenSource();

        var runTask = Runner().RunAsync(adapter, Request(), [], Options(30), cancellationToken: cts.Token);

        await cts.CancelAsync();

        var result = await runTask;

        result.Steps.Single().Status.ShouldBe(DiagnosticStatus.Cancelled);
        result.State.ShouldBe(TaskRunState.Cancelled);
        result.WasCancelled.ShouldBeTrue();
    }

    [Fact]
    public async Task Remaining_steps_should_be_skipped_after_cancellation()
    {
        var adapter = new FakeAdapter(
            FakeAdapter.Hangs(),
            [
                new DiagnosticStepPlan("slow", "慢步骤"),
                new DiagnosticStepPlan("later", "后续步骤")
            ]);

        using var cts = new CancellationTokenSource();

        var runTask = Runner().RunAsync(adapter, Request(), [], Options(30), cancellationToken: cts.Token);

        await cts.CancelAsync();

        var result = await runTask;

        var later = result.Steps.Single(s => s.StepId == "later");

        later.Status.ShouldBe(DiagnosticStatus.Skipped, "取消后未执行的步骤是“未执行”，不是失败。");
        later.SkipReason.ShouldNotBeNull().ShouldContain("取消");
        adapter.ExecutedSteps.ShouldBe(["slow"]);
    }

    [Fact]
    public async Task Step_timeout_should_be_reported_as_timed_out_not_failed()
    {
        var time = new FakeTimeProvider();
        var adapter = new FakeAdapter(
            FakeAdapter.Hangs(),
            [new DiagnosticStepPlan("slow", "慢步骤", Timeout: TimeSpan.FromSeconds(3))]);

        var runTask = Runner(time).RunAsync(
            adapter, Request(), [], Options(60), cancellationToken: CancellationToken.None);

        // 让步骤真正进入挂起状态，再推进时间触发超时。
        await WaitUntilAsync(() => adapter.ExecutedSteps.Count == 1);

        time.Advance(TimeSpan.FromSeconds(4));

        var result = await runTask;
        var step = result.Steps.Single();

        step.Status.ShouldBe(DiagnosticStatus.TimedOut);
        step.Status.ShouldNotBe(DiagnosticStatus.Failed, "超时不得与失败混用（DEV_STANDARDS §2.4）。");
        step.Summary.ShouldContain("单步超时");
        result.WasCancelled.ShouldBeFalse("超时不是用户取消。");
    }

    [Fact]
    public async Task Step_timeout_should_not_cancel_the_whole_run()
    {
        var time = new FakeTimeProvider();
        var adapter = new FakeAdapter(
            (context, token) => context.StepId == "slow"
                ? FakeAdapter.Hangs()(context, token)
                : FakeAdapter.Passed()(context, token),
            [
                new DiagnosticStepPlan("slow", "慢步骤", Timeout: TimeSpan.FromSeconds(3)),
                new DiagnosticStepPlan("fast", "快步骤")
            ]);

        var runTask = Runner(time).RunAsync(
            adapter, Request(), [], Options(60), cancellationToken: CancellationToken.None);

        await WaitUntilAsync(() => adapter.ExecutedSteps.Count == 1);
        time.Advance(TimeSpan.FromSeconds(4));

        var result = await runTask;

        result.Steps.Single(s => s.StepId == "fast").Status.ShouldBe(DiagnosticStatus.Passed);
        adapter.ExecutedSteps.ShouldBe(["slow", "fast"], "单步超时只中止该步骤，任务应继续。");
    }

    [Fact]
    public async Task Adapter_exception_should_be_mapped_to_a_failed_step_with_a_readable_reason()
    {
        var adapter = new FakeAdapter(
            (_, _) => throw new TimeoutException("连接目标超时"));

        var result = await Runner().RunAsync(
            adapter, Request(), [], Options(), cancellationToken: CancellationToken.None);

        var step = result.Steps.Single();

        step.Status.ShouldBe(DiagnosticStatus.Failed);
        step.Summary.ShouldContain(nameof(TimeoutException));
        step.Summary.ShouldContain("连接目标超时");
        step.HasSuggestion.ShouldBeTrue();
    }

    [Fact]
    public async Task Adapter_exception_should_not_stop_remaining_steps()
    {
        var adapter = new FakeAdapter(
            (context, _) => context.StepId == "boom"
                ? throw new InvalidOperationException("炸了")
                : Task.FromResult(DiagnosticStepResults.Passed(
                    context.StepId, context.Name, "通过", context.StartedAt, TimeSpan.Zero)),
            [
                new DiagnosticStepPlan("boom", "会抛异常的步骤"),
                new DiagnosticStepPlan("next", "后续步骤")
            ]);

        var result = await Runner().RunAsync(
            adapter, Request(), [], Options(), cancellationToken: CancellationToken.None);

        result.Steps.Single(s => s.StepId == "next").Status.ShouldBe(DiagnosticStatus.Passed);
    }

    [Fact]
    public async Task Overall_timeout_should_be_reported_with_the_task_scope()
    {
        var time = new FakeTimeProvider();
        var adapter = new FakeAdapter(
            FakeAdapter.Hangs(),
            [new DiagnosticStepPlan("slow", "慢步骤", Timeout: TimeSpan.FromMinutes(10))]);

        // 默认单步超时（5 s）必须小于总超时（20 s），否则 DiagnosticRunOptions.Validated 会拒绝；
        // 步骤自己的 10 分钟超时由计划覆盖，从而确保触发的是“任务总超时”而不是单步超时。
        var options = new DiagnosticRunOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));

        var runTask = Runner(time).RunAsync(
            adapter, Request(), [], options, cancellationToken: CancellationToken.None);

        await WaitUntilAsync(() => adapter.ExecutedSteps.Count == 1);
        time.Advance(TimeSpan.FromSeconds(25));

        var result = await runTask;
        var step = result.Steps.Single();

        step.Status.ShouldBe(DiagnosticStatus.TimedOut);
        step.Summary.ShouldContain("任务总超时");
    }

    [Fact]
    public async Task Progress_events_should_carry_the_run_id_and_be_ordered()
    {
        var adapter = new FakeAdapter(
            FakeAdapter.Passed(),
            [
                new DiagnosticStepPlan("a", "步骤 A"),
                new DiagnosticStepPlan("b", "步骤 B")
            ]);

        var progress = new RecordingProgress();

        await Runner().RunAsync(
            adapter, Request(), [], Options(), progress, CancellationToken.None);

        progress.Events.ShouldNotBeEmpty();
        progress.Events.ShouldAllBe(e => e.RunId == "run-1");

        // 每个步骤都应有 Starting 在前、Finished 在后。
        foreach (var stepId in new[] { "a", "b" })
        {
            var forStep = progress.Events.Where(e => e.StepId == stepId).ToList();
            forStep.First().Phase.ShouldBe(DiagnosticStepPhase.Starting);
            forStep.Last().Phase.ShouldBe(DiagnosticStepPhase.Finished);
        }
    }

    [Fact]
    public async Task Progress_messages_should_not_leak_credentials_through_the_adapter_summary()
    {
        // 适配器返回的摘要里带凭据时，编排器必须脱敏后再交给调用方。
        var redactor = new ReplacingRedactor();
        var runner = new DiagnosticTaskRunner(redactor);

        var adapter = new FakeAdapter(
            (context, _) => Task.FromResult(DiagnosticStepResults.Passed(
                context.StepId, context.Name, "password=hunter2", context.StartedAt, TimeSpan.Zero)));

        var progress = new RecordingProgress();

        var result = await runner.RunAsync(
            adapter, Request(), [], Options(), progress, CancellationToken.None);

        result.Steps.Single().Summary.ShouldNotContain("hunter2");
        progress.Events.ShouldAllBe(e => !e.Message.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void Runner_should_reject_null_redactor()
    {
        // 脱敏实现缺失必须在构造时就暴露，而不是等到泄漏之后。
        Should.Throw<ArgumentNullException>(() => new DiagnosticTaskRunner(null!));
    }

    [Fact]
    public async Task Invalid_options_should_be_rejected_before_any_step_runs()
    {
        var adapter = new FakeAdapter(FakeAdapter.Passed());

        var invalid = new DiagnosticRunOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Runner().RunAsync(
            adapter, Request(), [], invalid, cancellationToken: CancellationToken.None));

        adapter.ExecutedSteps.ShouldBeEmpty();
    }

    [Fact]
    public async Task Zero_steps_should_produce_pending_state_without_throwing()
    {
        var adapter = new FakeAdapter(FakeAdapter.Passed(), []);

        var result = await Runner().RunAsync(
            adapter, Request(), [], Options(), cancellationToken: CancellationToken.None);

        result.Steps.ShouldBeEmpty();
        result.State.ShouldBe(TaskRunState.Pending, "什么都没跑就不该报“已完成”。");
    }

    /// <summary>轮询等待条件成立（仅用于让异步步骤真正进入挂起状态）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new InvalidOperationException("等待条件超时。");
    }

    /// <summary>把 "hunter2" 替换成掩码的最小脱敏实现，用于验证编排器确实调用了脱敏。</summary>
    private sealed class ReplacingRedactor : ITextRedactor
    {
        public string? Redact(string? input) =>
            input?.Replace("hunter2", "***", StringComparison.Ordinal);
    }
}