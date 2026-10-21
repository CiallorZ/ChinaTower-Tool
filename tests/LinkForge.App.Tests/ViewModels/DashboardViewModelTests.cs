using System.IO;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.Domain.Diagnostics;
using LinkForge.Domain.Projects;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// 工作台视图模型测试（规范 §4.1、LF-003 验收）。
/// </summary>
/// <remarks>
/// 覆盖七类状态：空 / 加载 / 运行 / 成功 / 部分成功 / 失败 / 取消 / 依赖缺失。
/// 全部走真实对象（真实编排器、真实自检适配器、真实文件历史），
/// 不用假的 <c>IDiagnosticAdapter</c> —— 换成假适配器等于把被测对象换掉。
/// </remarks>
public class DashboardViewModelTests
{
    [Fact]
    public void Environment_facts_should_all_be_readable()
    {
        using var harness = new DashboardHarness();
        var facts = harness.ViewModel.EnvironmentFacts;

        facts.ShouldNotBeEmpty();

        // 环境事实不允许出现空值：空值在界面上表现为"这一行只有标签"，
        // 会被读成"这项没有配置"，而实际上可能只是没读到。
        facts.ShouldAllBe(f => !string.IsNullOrWhiteSpace(f.Value));

        facts.Select(static f => f.Label).ShouldContain("应用版本");
        facts.Select(static f => f.Label).ShouldContain("数据目录");
        facts.Single(static f => f.Label == "数据目录").Value.ShouldBe(harness.DataDirectory);
    }

    [Fact]
    public async Task Loading_should_clear_its_flag_and_populate_real_dependencies()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        // 首屏必须是加载态：显示"0 个依赖"会被读成"环境干净"。
        viewModel.IsLoading.ShouldBeTrue();

        await viewModel.LoadAsync();

        viewModel.IsLoading.ShouldBeFalse();
        viewModel.LoadErrorMessage.ShouldBeNull();
        viewModel.Dependencies.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Missing_dependencies_should_be_reported_separately()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        await viewModel.LoadAsync();

        // 缺失列表必须等于"全部依赖里不可用的那些"，
        // 两处各算一遍就会出现"提示区说缺 2 项、列表里有 3 项"的矛盾。
        var expected = viewModel.Dependencies.Where(static d => !d.IsAvailable).ToList();

        viewModel.MissingDependencies.Select(static d => d.Id)
            .ShouldBe(expected.Select(static d => d.Id), ignoreOrder: true);

        viewModel.HasMissingDependencies.ShouldBe(viewModel.MissingDependencies.Count > 0);
    }

    [Fact]
    public async Task Recent_runs_should_be_empty_and_labelled_before_any_run()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        await viewModel.LoadAsync();

        viewModel.HasRecentRuns.ShouldBeFalse();
        viewModel.RecentRuns.ShouldBeEmpty();

        // 空状态必须说明"为什么空 + 下一步做什么"，只写"暂无数据"是无效提示。
        viewModel.RunsEmptyDescription.ShouldContain("运行环境自检");
    }

    [Fact]
    public async Task Self_check_should_produce_real_steps_and_persist_history()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        await viewModel.LoadAsync();
        await viewModel.RunSelfCheckCommand.ExecuteAsync(null);

        viewModel.HasSelfCheckResult.ShouldBeTrue();
        viewModel.SelfCheckSteps.ShouldNotBeEmpty();
        viewModel.SelfCheckLogs.ShouldNotBeEmpty();

        // 真实自检的四个步骤都要有结论；任一缺失都是编排器或适配器的缺陷。
        viewModel.SelfCheckSteps.Select(static s => s.StepId)
            .ShouldBe(["workspace", "disk", "capture", "evidence"], ignoreOrder: true);

        // 每个步骤都必须有可读摘要：空白摘要会让"结论区"变成空壳。
        viewModel.SelfCheckSteps.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.Summary));

        // 历史必须真的落盘，否则重启后工作台又变回空状态。
        File.Exists(harness.HistoryPath).ShouldBeTrue("自检结果未写入历史文件。");
        viewModel.HasRecentRuns.ShouldBeTrue();
        viewModel.RecentRuns.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Self_check_should_export_real_evidence()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        await viewModel.LoadAsync();
        await viewModel.RunSelfCheckCommand.ExecuteAsync(null);

        var evidenceStep = viewModel.SelfCheckSteps.Single(static s => s.StepId == "evidence");

        // 证据步骤要么真的产出了文件（通过），要么明确说明为什么没产出（跳过/失败）。
        // 不允许"显示通过但没有任何证据文件" —— 那等于报告里引用了不存在的证据。
        if (evidenceStep.Status == DiagnosticStatus.Passed)
        {
            evidenceStep.HasEvidence.ShouldBeTrue("证据步骤通过但没有证据文件。");

            var artifact = evidenceStep.EvidenceArtifacts[0];

            artifact.IsRedacted.ShouldBeTrue("证据必须标记为已脱敏。");
            artifact.Sha256.Length.ShouldBe(64);

            var fullPath = Path.Combine(harness.DataDirectory, "evidence",
                artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            File.Exists(fullPath).ShouldBeTrue($"证据文件不存在：{fullPath}");
        }
        else
        {
            evidenceStep.Status.ShouldBeOneOf(DiagnosticStatus.Skipped, DiagnosticStatus.Failed, DiagnosticStatus.Unknown);
        }
    }

    [Fact]
    public async Task Skipped_steps_must_never_be_counted_as_failures()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        await viewModel.LoadAsync();
        await viewModel.RunSelfCheckCommand.ExecuteAsync(null);

        var skipped = viewModel.SelfCheckSteps.Where(static s => s.Status == DiagnosticStatus.Skipped).ToList();

        // ADR-016 红线：跳过不计入失败。
        viewModel.SelfCheckSteps.ShouldAllBe(s =>
            s.Status != DiagnosticStatus.Skipped || !s.Status.IsFailure());

        // 跳过的步骤必须写明原因，否则用户无法区分"有意设计"与"缺陷"。
        skipped.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.SkipReason));

        // 结论补充里必须能看到未执行原因：用户不会逐条展开步骤去找。
        if (skipped.Count > 0)
        {
            viewModel.ConclusionNote.ShouldContain("未执行原因");
        }
    }

    [Fact]
    public async Task Cancelled_run_should_be_reported_as_cancelled_not_failed()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        await viewModel.LoadAsync();

        // 自检很短，直接取消可能来不及；先启动再取消，
        // 断言"取消后状态是 Cancelled 而不是 Failed"（ADR-016）。
        var run = viewModel.RunSelfCheckCommand.ExecuteAsync(null);
        viewModel.CancelSelfCheckCommand.Execute(null);
        await run;

        viewModel.IsSelfCheckRunning.ShouldBeFalse();

        if (viewModel.SelfCheckState == TaskRunState.Cancelled)
        {
            viewModel.Conclusion.ShouldContain("取消");
        }

        // 无论是否赶上取消，状态都不得是"没有结论的空壳"。
        viewModel.SelfCheckState.ShouldNotBe(TaskRunState.Pending);
        viewModel.Conclusion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Self_check_should_be_rejected_while_already_running()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        await viewModel.LoadAsync();

        var first = viewModel.RunSelfCheckCommand.ExecuteAsync(null);

        // 规范 §4.2：不允许用户误以为重复点击会启动第二个任务。
        viewModel.CanStartSelfCheck.ShouldBeFalse();
        viewModel.RunSelfCheckCommand.CanExecute(null).ShouldBeFalse();

        await first;

        viewModel.CanStartSelfCheck.ShouldBeTrue();
        viewModel.RunSelfCheckCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Logs_should_never_contain_plaintext_secrets()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        await viewModel.LoadAsync();

        // 注入一条含凭据的日志，验证 LogEntry 在构造时即脱敏。
        // 自检本身不含凭据，这里是守住"任何进入日志的文本都不带明文"这条边界。
        const string secret = "password=SuperSecret123";

        viewModel.SelfCheckLogs.Add(new LogEntry(
            DateTimeOffset.Now, LfLogLevel.Debug, "test", "run", null, secret));

        var logged = viewModel.SelfCheckLogs[^1].Message;

        logged.ShouldNotContain("SuperSecret123");
        logged.ShouldContain("***");
    }

    [Fact]
    public async Task Clearing_history_should_empty_the_list_and_the_file()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        await viewModel.LoadAsync();
        await viewModel.RunSelfCheckCommand.ExecuteAsync(null);
        viewModel.HasRecentRuns.ShouldBeTrue();

        await viewModel.ClearHistoryCommand.ExecuteAsync(null);

        viewModel.HasRecentRuns.ShouldBeFalse();
        File.Exists(harness.HistoryPath).ShouldBeFalse();
    }

    [Fact]
    public void Elapsed_text_should_be_monospaced_friendly()
    {
        // 耗时文本要放在等宽列里，格式必须定长，否则列会跳动。
        DashboardViewModel.FormatElapsed(TimeSpan.Zero).ShouldBe("00:00.0");
        DashboardViewModel.FormatElapsed(TimeSpan.FromSeconds(12.4)).ShouldBe("00:12.4");
        DashboardViewModel.FormatElapsed(TimeSpan.FromMinutes(3)).ShouldBe("03:00.0");

        // 负耗时（时钟回拨）不能显示成 "-00:01"，那会被读成错误数据。
        DashboardViewModel.FormatElapsed(TimeSpan.FromSeconds(-5)).ShouldBe("00:00.0");
    }

    [Fact]
    public async Task Project_state_should_reflect_the_real_store_instead_of_a_hardcoded_false()
    {
        // 回归：LF-004 交付项目持久化之前，工作台的 HasProjects 恒为 false，
        // 空状态文案还写着"项目管理功能将在后续阶段交付"——
        // 功能交付后那句话就是假的，必须跟着真实数据走。
        using var harness = new DashboardHarness();

        harness.ViewModel.HasProjects.ShouldBeFalse("还没有项目时必须是空状态。");
        harness.ViewModel.ProjectsEmptyDescription
            .Contains("将在后续阶段交付", StringComparison.Ordinal)
            .ShouldBeFalse("空状态文案不得再宣称项目管理未交付。");

        harness.ProjectStore.Items.Add(new Project
        {
            Id = "prj-1",
            Name = "东门视频项目",
            CreatedAt = DateTimeOffset.Now
        });

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.HasProjects.ShouldBeTrue("库里已经有项目，工作台不应再说“没有项目”。");
    }

    [Fact]
    public async Task Load_failure_should_be_surfaced_not_swallowed()
    {
        using var harness = new DashboardHarness();
        var viewModel = harness.ViewModel;

        // 把数据目录改成非法路径，让历史读取失败：
        // 失败必须出现在 LoadErrorMessage 里并给出重试入口，而不是静默留空。
        var failing = new DashboardViewModel(
            new AppEnvironment(harness.SettingsPath),
            new LinkForge.Application.Environment.DependencyCatalog(),
            new FailingRunHistoryStore(),
            harness.ProjectStore,
            harness.Adapter,
            harness.Runner);

        await failing.LoadAsync();

        failing.LoadErrorMessage.ShouldNotBeNull();
        failing.LoadErrorMessage.ShouldContain("读取工作台数据失败");
    }
}

/// <summary>
/// 读取/写入一律失败的历史存储（用于验证失败路径会被如实呈现）。
/// </summary>
internal sealed class FailingRunHistoryStore : LinkForge.Application.Diagnostics.IRunHistoryStore
{
    public Task<IReadOnlyList<DiagnosticRunResult>> LoadAsync(int? limit = null, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<DiagnosticRunResult>>(new IOException("模拟历史文件不可读。"));

    public Task AppendAsync(DiagnosticRunResult result, CancellationToken cancellationToken = default) =>
        Task.FromException(new IOException("模拟历史文件不可写。"));

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new IOException("模拟历史文件不可写。"));
}
