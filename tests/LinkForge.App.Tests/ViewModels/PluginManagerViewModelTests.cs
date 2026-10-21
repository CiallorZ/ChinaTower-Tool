using System.IO;
using LinkForge.App.Presentation;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Hosting;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// LF-012 插件管理视图模型测试。
/// </summary>
/// <remarks>
/// 验收重点全部落在"不许伪装"上：没扫描时不得声称有插件，被阻断的插件不得可运行，
/// 未确认来源不得启动进程，取消不得算失败，导出文本必须经过脱敏。
/// 真实进程外执行由 <c>LinkForge.Plugin.Hosting.Tests</c> 用真子进程覆盖，
/// 本类只验证页面层的判定与状态语义。
/// </remarks>
public class PluginManagerViewModelTests
{
    [Fact]
    public void Default_state_should_be_a_true_empty_state()
    {
        var harness = new PluginManagerHarness();
        var viewModel = harness.ViewModel;

        viewModel.HasScanned.ShouldBeFalse();
        viewModel.IsInitialEmpty.ShouldBeTrue();
        viewModel.IsEmptyAfterScan.ShouldBeFalse();
        viewModel.HasPlugins.ShouldBeFalse();
        viewModel.HasSelectedPlugin.ShouldBeFalse();
        viewModel.CanRunPlugin.ShouldBeFalse();
        viewModel.State.ShouldBe(TaskRunState.Pending);
        viewModel.Plugins.ShouldBeEmpty();
    }

    [Fact]
    public void Plugins_directory_should_be_shown_so_users_know_where_to_install()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.PluginsDirectory = @"C:\temp\plugins";

        harness.ViewModel.PluginsDirectory.ShouldBe(@"C:\temp\plugins");
    }

    [Fact]
    public async Task Scan_should_report_an_empty_directory_without_inventing_plugins()
    {
        var harness = new PluginManagerHarness();

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        harness.Catalog.ScanCallCount.ShouldBe(1);
        harness.ViewModel.HasScanned.ShouldBeTrue();
        harness.ViewModel.IsScanning.ShouldBeFalse();
        harness.ViewModel.Plugins.ShouldBeEmpty();
        harness.ViewModel.HasPlugins.ShouldBeFalse();
        harness.ViewModel.IsEmptyAfterScan.ShouldBeTrue();
        harness.ViewModel.ScanSummary.ShouldContain("没有");
        harness.ViewModel.ScanState.ShouldBe(TaskRunState.Completed);
    }

    [Fact]
    public async Task Scan_should_keep_blocked_plugins_visible_with_their_reason()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            id: "acme.good",
            displayName: "可用插件"));
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            id: "acme.bad",
            displayName: "哈希不符插件",
            status: PluginValidationStatus.HashMismatch,
            detail: "入口哈希不一致：清单 aaaaaaaa，实际 bbbbbbbb。"));

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.Plugins.Count.ShouldBe(2);
        viewModel.HasPlugins.ShouldBeTrue();

        // 阻断项必须留在列表里并给出原因：把它隐藏起来，用户就查不出为什么"明明放了却没出现"。
        var blocked = viewModel.Plugins.Single(static item => !item.IsLoadable);
        blocked.StatusText.ShouldBe("已阻断");
        blocked.ValidationText.ShouldContain("哈希不一致");
        blocked.HasValidationError.ShouldBeTrue();
        blocked.BadgeStatus.ShouldBe(DiagnosticStatus.Failed);

        // 默认选中应落在可加载的插件上，而不是随机选中一个跑不起来的。
        viewModel.SelectedPlugin.ShouldNotBeNull();
        viewModel.SelectedPlugin!.IsLoadable.ShouldBeTrue();
        viewModel.ScanState.ShouldBe(TaskRunState.PartiallySucceeded);
        viewModel.ScanSummary.ShouldContain("1 个已阻断");
    }

    [Fact]
    public async Task Blocked_plugin_should_never_become_runnable()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            status: PluginValidationStatus.IncompatibleApi,
            detail: "插件要求 API 1.1，宿主仅支持 1.0。"));

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.SelectedPlugin!.IsLoadable.ShouldBeFalse();
        viewModel.IsSourceConfirmed = true;

        // 即使勾选了来源确认，未通过清单校验的插件仍然不可运行。
        viewModel.CanRunPlugin.ShouldBeFalse();
        await viewModel.RunPluginCommand.ExecuteAsync(null);
        harness.Executor.RunCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Untrusted_plugin_should_not_start_a_process()
    {
        var harness = new PluginManagerHarness();
        harness.Executor.ThrowTrustNotConfirmed = true;
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.RequiresTrust.ShouldBeTrue();
        viewModel.IsSourceConfirmed.ShouldBeFalse();
        viewModel.CanRunPlugin.ShouldBeFalse();

        await viewModel.RunPluginCommand.ExecuteAsync(null);

        harness.Executor.RunCallCount.ShouldBe(0, "未确认来源时不得启动插件进程。");
    }

    [Fact]
    public async Task Confirmed_source_should_run_and_produce_a_real_result()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.IsSourceConfirmed = true;
        viewModel.CanRunPlugin.ShouldBeTrue();

        await viewModel.RunPluginCommand.ExecuteAsync(null);

        harness.Executor.RunCallCount.ShouldBe(1);
        harness.Executor.LastSourceConfirmed.ShouldBe(true);
        viewModel.State.ShouldBe(TaskRunState.Completed);
        viewModel.HasResult.ShouldBeTrue();
        viewModel.IsRunning.ShouldBeFalse();
        viewModel.Steps.Count.ShouldBe(1);
        viewModel.Conclusion.ShouldContain("全部步骤通过");
        viewModel.SummaryItems.ShouldContain(static item => item.Label == "签名");

        // 只能显示"声明/未签名"，不得写成"已验证"。
        viewModel.SummaryItems
            .Single(static item => item.Label == "签名")
            .Value
            .ShouldBe("未签名");
    }

    [Fact]
    public async Task Signature_must_stay_declared_and_unverified()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            signature: PluginSignatureStatus.DeclaredSignedUnverified));

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var plugin = harness.ViewModel.Plugins.Single();
        plugin.SignatureText.ShouldBe("声明已签名（未验证）");
        plugin.RequiresExplicitTrust.ShouldBeTrue();
    }

    [Fact]
    public async Task Changing_selection_should_withdraw_the_previous_trust_confirmation()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            id: "acme.one", displayName: "插件一"));
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            id: "acme.two", displayName: "插件二"));

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.IsSourceConfirmed = true;

        var other = viewModel.Plugins.First(static item => item.Id == "acme.two");
        viewModel.SelectedPlugin = other;

        // 确认只对具体插件生效：保留下来等于"确认了 A，顺手也信任了 B"。
        viewModel.IsSourceConfirmed.ShouldBeFalse();
        viewModel.CanRunPlugin.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelled_run_should_not_be_reported_as_failure()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());

        harness.Executor.Behavior = async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("不应走到这里。");
        };

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.IsSourceConfirmed = true;

        var run = viewModel.RunPluginCommand.ExecuteAsync(null);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!viewModel.IsRunning && !run.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        viewModel.IsRunning.ShouldBeTrue("运行状态未及时出现，取消入口无从验证。");
        viewModel.CancelRunCommand.Execute(null);
        await run;

        viewModel.State.ShouldBe(TaskRunState.Cancelled);
        viewModel.Conclusion.ShouldContain("取消");
        viewModel.ConclusionNote.ShouldContain("不等于失败");
    }

    [Fact]
    public async Task Plugin_crash_should_surface_as_failed_and_keep_the_page_usable()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());
        harness.Executor.Failure = new PluginProcessException(
            "插件进程异常：退出码 -1073741819",
            isCrash: true);

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.IsSourceConfirmed = true;
        await viewModel.RunPluginCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Failed);
        viewModel.Conclusion.ShouldContain("插件未能运行");
        viewModel.ConclusionNote.ShouldContain("主程序不受影响");

        // 崩溃后必须还能再次操作：如果命令卡在 Running，用户只能重启整个工具。
        viewModel.IsRunning.ShouldBeFalse();
        viewModel.CanRunPlugin.ShouldBeTrue();
        viewModel.CanScan.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_step_status_should_not_be_counted_as_completed()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());

        harness.Executor.Behavior = static _ => Task.FromResult(new DiagnosticRunResult
        {
            RunId = "run-1",
            AdapterId = "acme.vendor.sdk",
            DisplayName = "厂商 SDK 插件",
            TargetLabel = "厂商 SDK 插件",
            StartedAt = DateTimeOffset.Now,
            EndedAt = DateTimeOffset.Now,
            Steps =
            [
                DiagnosticStepResults.Unknown(
                    "probe",
                    "能力探针",
                    "插件返回了未知状态，未采纳为通过或失败。",
                    DateTimeOffset.Now,
                    TimeSpan.FromMilliseconds(10))
            ]
        });

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);
        var viewModel = harness.ViewModel;
        viewModel.IsSourceConfirmed = true;
        await viewModel.RunPluginCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Inconclusive);
        viewModel.Conclusion.ShouldContain("未给出确定性结论");
    }

    [Fact]
    public async Task Unreadable_directory_should_be_reported_as_a_dependency_problem_not_empty()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Behavior = static _ =>
            Task.FromException<IReadOnlyList<PluginDescriptor>>(
                new UnauthorizedAccessException("插件目录被系统拒绝访问。"));

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.ScanState.ShouldBe(TaskRunState.Failed);
        viewModel.HasScanFailure.ShouldBeTrue();
        viewModel.ScanError.ShouldContain("拒绝访问");

        // "读不到"和"没有插件"是两回事，不能同时出现。
        viewModel.IsEmptyAfterScan.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_a_scan_should_keep_the_previous_list()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());
        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.Plugins.Count.ShouldBe(1);

        // 让扫描真的处于进行中再由用户取消，而不是伪造一个已取消的 Task：
        // 后者会抛出与取消无关的 ArgumentException，测不到"取消保留旧列表"这条规则。
        harness.Catalog.Behavior = async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return Array.Empty<PluginDescriptor>();
        };

        var scan = viewModel.ScanCommand.ExecuteAsync(null);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!viewModel.IsScanning && !scan.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        viewModel.IsScanning.ShouldBeTrue("扫描状态未及时出现，取消入口无从验证。");
        viewModel.CancelScanCommand.Execute(null);
        await scan;

        viewModel.ScanState.ShouldBe(TaskRunState.Cancelled);
        viewModel.Plugins.Count.ShouldBe(1, "取消扫描不得清空上一次结果。");
        viewModel.ScanSummary.ShouldContain("保持上一次结果");
        viewModel.IsScanning.ShouldBeFalse();
        viewModel.HasScanFailure.ShouldBeFalse("取消不是失败。");
    }

    [Fact]
    public async Task Copied_and_exported_text_should_be_redacted()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());

        harness.Executor.Behavior = static _ => Task.FromResult(new DiagnosticRunResult
        {
            RunId = "run-1",
            AdapterId = "acme.vendor.sdk",
            DisplayName = "厂商 SDK 插件",
            TargetLabel = "admin:SuperSecret123@10.0.0.8",
            StartedAt = DateTimeOffset.Now,
            EndedAt = DateTimeOffset.Now,
            Steps =
            [
                DiagnosticStepResults.Passed(
                    "probe",
                    "能力探针",
                    "登录成功 password=SuperSecret123",
                    DateTimeOffset.Now,
                    TimeSpan.FromMilliseconds(20))
            ]
        });

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);
        var viewModel = harness.ViewModel;
        viewModel.IsSourceConfirmed = true;
        await viewModel.RunPluginCommand.ExecuteAsync(null);

        viewModel.CopySummaryCommand.Execute(null);
        harness.Exporter.CopiedText.ShouldNotBeNull();
        harness.Exporter.CopiedText.ShouldNotContain("SuperSecret123");

        await viewModel.ExportCommand.ExecuteAsync(null);
        harness.Exporter.ExportedContent.ShouldNotBeNull();
        harness.Exporter.ExportedContent.ShouldNotContain("SuperSecret123");

        // 按 JSON 语义而不是原始文本断言：序列化器默认把非 ASCII 转义成 \uXXXX，
        // 直接对文本 ShouldContain 中文会因编码细节失败，而那与内容是否正确无关。
        using var document = System.Text.Json.JsonDocument.Parse(harness.Exporter.ExportedContent!);
        var root = document.RootElement;

        root.GetProperty("target").GetString()!.ShouldNotContain("SuperSecret123");
        root.GetProperty("signatureNotice").GetString()!.ShouldContain("未验证证书链");
        root.GetProperty("isolation").GetString()!.ShouldContain("独立子进程");

        var steps = root.GetProperty("steps").EnumerateArray().ToArray();
        steps.ShouldHaveSingleItem();
        steps[0].GetProperty("summary").GetString()!.ShouldNotContain("SuperSecret123");
    }

    [Fact]
    public async Task Export_cancel_should_be_reported_as_cancelled_not_failed()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());
        harness.Exporter.ExportBehavior = static (_, _, _) => Task.FromResult<string?>(null);

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);
        var viewModel = harness.ViewModel;
        viewModel.IsSourceConfirmed = true;
        await viewModel.RunPluginCommand.ExecuteAsync(null);

        await viewModel.ExportCommand.ExecuteAsync(null);

        viewModel.ActionFeedback.ShouldBe("已取消导出。");
        viewModel.State.ShouldNotBe(TaskRunState.Failed);
    }

    [Fact]
    public async Task Selecting_a_plugin_should_clear_the_previous_run_result()
    {
        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            id: "acme.one", displayName: "插件一"));
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            id: "acme.two", displayName: "插件二"));

        await harness.ViewModel.ScanCommand.ExecuteAsync(null);

        var viewModel = harness.ViewModel;
        viewModel.IsSourceConfirmed = true;
        await viewModel.RunPluginCommand.ExecuteAsync(null);
        viewModel.HasResult.ShouldBeTrue();

        viewModel.SelectedPlugin = viewModel.Plugins.First(static item => item.Id == "acme.two");

        // 把"插件一"的结论留在"插件二"页面上是错误信息，不是便捷。
        viewModel.HasResult.ShouldBeFalse();
        viewModel.Steps.ShouldBeEmpty();
        viewModel.Conclusion.ShouldBeEmpty();
    }

    [Fact]
    public void Run_should_be_unavailable_without_a_selected_plugin()
    {
        var harness = new PluginManagerHarness();

        harness.ViewModel.CanRunPlugin.ShouldBeFalse();
        harness.ViewModel.RunPluginCommand.CanExecute(null).ShouldBeFalse();
    }
}
