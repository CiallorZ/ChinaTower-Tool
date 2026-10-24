using System.IO;
using LinkForge.Adapters.Security;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// LF-020 安全中心视图模型测试。
/// </summary>
/// <remarks>
/// 验收重点是显式范围、授权门禁、状态语义、取消以及复制/导出脱敏，
/// 不是只验证命令是否存在。
/// </remarks>
public class SecurityCenterViewModelTests
{
    [Fact]
    public void Empty_target_should_show_field_error_and_not_probe()
    {
        using var harness = new SecurityCenterHarness();
        var viewModel = harness.ViewModel;
        viewModel.Target = " ";

        viewModel.RunCommand.Execute(null);

        viewModel.TargetError.ShouldNotBeNullOrWhiteSpace();
        harness.Probe.CallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("https://example.test")]
    [InlineData("example.test/path")]
    [InlineData("example test")]
    public void Invalid_target_should_be_rejected(string target)
    {
        using var harness = new SecurityCenterHarness();
        harness.ViewModel.Target = target;

        harness.ViewModel.RunCommand.Execute(null);

        harness.ViewModel.TargetError.ShouldNotBeNullOrWhiteSpace();
        harness.Probe.CallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public void Invalid_tcp_ports_should_be_rejected(string ports)
    {
        using var harness = new SecurityCenterHarness();
        harness.ViewModel.PortsText = ports;

        harness.ViewModel.RunCommand.Execute(null);

        harness.ViewModel.PortsError.ShouldNotBeNullOrWhiteSpace();
        harness.Probe.CallCount.ShouldBe(0);
    }

    [Fact]
    public void Tls_ports_outside_tcp_scope_should_be_rejected()
    {
        using var harness = new SecurityCenterHarness();
        harness.ViewModel.PortsText = "443";
        harness.ViewModel.TlsPortsText = "8443";

        harness.ViewModel.RunCommand.Execute(null);

        harness.ViewModel.TlsPortsError.ShouldNotBeNull();
        harness.ViewModel.TlsPortsError.Contains("TCP 端口范围内", StringComparison.Ordinal).ShouldBeTrue();
        harness.Probe.CallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("timeout", "0", "2", "200")]
    [InlineData("concurrency", "5", "5", "200")]
    [InlineData("delay", "5", "2", "-1")]
    public void Invalid_advanced_parameters_should_be_rejected(
        string field,
        string timeout,
        string concurrency,
        string delay)
    {
        using var harness = new SecurityCenterHarness();
        var viewModel = harness.ViewModel;
        viewModel.TimeoutText = timeout;
        viewModel.ConcurrencyText = concurrency;
        viewModel.DelayMillisecondsText = delay;

        viewModel.RunCommand.Execute(null);

        var error = field switch
        {
            "timeout" => viewModel.TimeoutError,
            "concurrency" => viewModel.ConcurrencyError,
            _ => viewModel.DelayError
        };

        error.ShouldNotBeNullOrWhiteSpace();
        harness.Probe.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task External_target_without_authorization_should_not_probe()
    {
        using var harness = new SecurityCenterHarness();
        var viewModel = harness.ViewModel;
        viewModel.Target = "device.example.test";

        viewModel.RequiresAuthorization.ShouldBeTrue();
        await viewModel.RunCommand.ExecuteAsync(null);

        harness.Probe.CallCount.ShouldBe(0);
        viewModel.State.ShouldBe(TaskRunState.Pending);
        viewModel.Conclusion.ShouldContain("授权");
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Loopback_should_not_require_authorization(string target)
    {
        using var harness = new SecurityCenterHarness();
        harness.ViewModel.Target = target;

        harness.ViewModel.RequiresAuthorization.ShouldBeFalse();
    }

    [Fact]
    public async Task Successful_run_should_populate_real_steps_and_scope()
    {
        using var harness = new SecurityCenterHarness();
        var viewModel = harness.ViewModel;

        await viewModel.RunCommand.ExecuteAsync(null);

        harness.Probe.CallCount.ShouldBe(1);
        viewModel.State.ShouldBe(TaskRunState.Completed);
        viewModel.Steps.ShouldHaveSingleItem();
        viewModel.Steps[0].FactItems.ShouldContain(static fact => fact.Label == "实际解析地址");
        viewModel.SummaryItems.ShouldContain(static item => item.Label == "目标");
        viewModel.SummaryItems.ShouldContain(static item => item.Label == "检查范围");
        viewModel.RawOutputText.ShouldContain("TCP 443");
    }

    [Fact]
    public async Task Certificate_warning_should_remain_visible_not_be_called_a_vulnerability()
    {
        using var harness = new SecurityCenterHarness();
        harness.Probe.Behavior = (request, _) => Task.FromResult(
            new SecurityBaselineObservation(
                SecurityObservationStatus.Warning,
                "TLS 证书未通过默认校验。",
                [new SecurityFact("证书校验", "RemoteCertificateNameMismatch", SecurityFactStatus.Warning)],
                [
                    new SecurityPortResult(
                        443,
                        SecurityPortState.Open,
                        "TCP 443 开放；证书校验失败。",
                        TimeSpan.FromMilliseconds(10),
                        new SecurityTlsResult(
                            443,
                            SecurityTlsState.CertificateRejected,
                            "TLS 443 证书未通过默认校验。",
                            [new SecurityFact("证书校验", "主机名不匹配", SecurityFactStatus.Warning)]))
                ],
                "TLS 443: 证书未通过默认校验。",
                "核对证书信任链与主机名；证书告警不等于已确认可利用漏洞。"));
        harness.ViewModel.Target = "127.0.0.1";

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.ViewModel.Steps[0].Status.ShouldBe(DiagnosticStatus.Warning);
        harness.ViewModel.Conclusion.ShouldNotContain("可利用漏洞已确认");
        harness.ViewModel.ConclusionNote.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Failed_observation_should_map_to_failed()
    {
        using var harness = new SecurityCenterHarness();
        harness.Probe.Behavior = (_, _) => Task.FromResult(
            new SecurityBaselineObservation(
                SecurityObservationStatus.Failed,
                "目标名称未解析到任何地址。",
                [],
                [],
                Suggestion: "检查 DNS 与目标名称。"));

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.ViewModel.State.ShouldBe(TaskRunState.Failed);
        harness.ViewModel.Steps[0].Status.ShouldBe(DiagnosticStatus.Failed);
    }

    [Fact]
    public async Task Unknown_observation_should_not_be_promoted_to_completed()
    {
        using var harness = new SecurityCenterHarness();
        harness.Probe.Behavior = (_, _) => Task.FromResult(
            new SecurityBaselineObservation(
                SecurityObservationStatus.Unknown,
                "端口无响应，无法区分关闭与过滤。",
                [new SecurityFact("TCP 443", "过滤/无响应", SecurityFactStatus.Unknown)],
                [new SecurityPortResult(
                    443,
                    SecurityPortState.Filtered,
                    "TCP 443 在超时内未建立连接。",
                    TimeSpan.FromSeconds(5))],
                Suggestion: "无响应不能证明主机离线。"));

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.ViewModel.State.ShouldBe(TaskRunState.Inconclusive);
        harness.ViewModel.Steps[0].Status.ShouldBe(DiagnosticStatus.Unknown);
        harness.ViewModel.Conclusion.ShouldContain("不能证明");
    }

    [Fact]
    public async Task Cancellation_should_be_cancelled_not_failed()
    {
        using var harness = new SecurityCenterHarness();
        harness.Probe.Behavior = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return FakeSecurityProbe.DefaultSuccess(
                new SecurityBaselineRequest(
                    "127.0.0.1",
                    [443],
                    [443],
                    TimeSpan.FromSeconds(1),
                    1,
                    TimeSpan.Zero));
        };

        var run = harness.ViewModel.RunCommand.ExecuteAsync(null);
        harness.ViewModel.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        harness.ViewModel.State.ShouldBe(TaskRunState.Cancelled);
        harness.ViewModel.Conclusion.ShouldContain("取消");
        harness.ViewModel.ConclusionNote.ShouldContain("不会记为失败");
    }

    [Fact]
    public async Task Copy_and_export_should_redact_secrets()
    {
        using var harness = new SecurityCenterHarness();
        harness.Probe.Behavior = (request, _) => Task.FromResult(
            new SecurityBaselineObservation(
                SecurityObservationStatus.Warning,
                "证书检查失败：Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature",
                [
                    new SecurityFact(
                        "调试凭据",
                        "password=SuperSecret123",
                        SecurityFactStatus.Warning)
                ],
                [
                    new SecurityPortResult(
                        request.Ports[0],
                        SecurityPortState.Open,
                        "TCP 443 开放。",
                        TimeSpan.FromMilliseconds(5))
                ],
                "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature",
                "核对证书。"));

        await harness.ViewModel.RunCommand.ExecuteAsync(null);
        harness.ViewModel.CopySummaryCommand.Execute(null);
        await harness.ViewModel.ExportCommand.ExecuteAsync(null);

        harness.Exporter.CopiedText.ShouldNotBeNull();
        harness.Exporter.CopiedText.ShouldNotContain("SuperSecret123");
        harness.Exporter.CopiedText.ShouldNotContain("eyJhbGciOiJIUzI1NiJ9.payload.signature");

        harness.Exporter.ExportedContent.ShouldNotBeNull();
        harness.Exporter.ExportedContent.ShouldNotContain("SuperSecret123");
        harness.Exporter.ExportedContent.ShouldNotContain("eyJhbGciOiJIUzI1NiJ9.payload.signature");
        harness.Exporter.ExportedContent.ShouldContain("security-baseline");
    }

    [Fact]
    public async Task Export_cancellation_and_failure_should_be_reported_without_crashing()
    {
        using var harness = new SecurityCenterHarness();
        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.Exporter.ExportBehavior = (_, _, _) =>
            Task.FromException<string?>(new OperationCanceledException());
        await harness.ViewModel.ExportCommand.ExecuteAsync(null);
        harness.ViewModel.ActionFeedback.ShouldContain("取消");

        harness.Exporter.ExportBehavior = (_, _, _) =>
            Task.FromException<string?>(new UnauthorizedAccessException("拒绝写入"));
        await harness.ViewModel.ExportCommand.ExecuteAsync(null);
        harness.ViewModel.ActionFeedback.ShouldContain("导出失败");
    }

    [Fact]
    public async Task Clipboard_failure_should_be_reported_without_crashing()
    {
        using var harness = new SecurityCenterHarness();
        await harness.ViewModel.RunCommand.ExecuteAsync(null);
        harness.Exporter.CopyBehavior = _ =>
            throw new InvalidOperationException("剪贴板被占用");

        harness.ViewModel.CopySummaryCommand.Execute(null);

        harness.ViewModel.ActionFeedback.ShouldContain("复制失败");
    }
}
