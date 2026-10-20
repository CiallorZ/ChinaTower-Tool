using System.IO;
using LinkForge.Adapters.Network;
using LinkForge.App.ViewModels;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// LF-006 网络诊断视图模型测试。
/// </summary>
/// <remarks>
/// 验收重点不是“命令存在”，而是参数校验、授权门禁、状态映射、
/// 取消语义以及复制/导出内容真的经过脱敏。
/// </remarks>
public class NetworkDiagnosticsViewModelTests
{
    [Fact]
    public void Empty_target_should_show_a_field_error_and_not_start()
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;

        viewModel.RunCommand.Execute(null);

        viewModel.TargetError.ShouldNotBeNullOrWhiteSpace();
        viewModel.IsRunning.ShouldBeFalse();
        harness.Probe.PingCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("http://camera.example.com")]
    [InlineData("10.0.0.8/path")]
    [InlineData("camera example.com")]
    public void Invalid_target_formats_should_be_rejected(string target)
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;

        viewModel.Target = target;
        viewModel.RunCommand.Execute(null);

        viewModel.TargetError.ShouldNotBeNullOrWhiteSpace();
        harness.Probe.PingCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public void Invalid_port_should_be_rejected_for_tcp(string port)
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;

        viewModel.SelectedTool = viewModel.Tools.Single(static t => t.Tool == NetworkTool.Tcp);
        viewModel.Target = "127.0.0.1";
        viewModel.PortText = port;
        viewModel.RunCommand.Execute(null);

        viewModel.PortError.ShouldNotBeNullOrWhiteSpace();
        harness.Probe.TcpCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("abc")]
    public void Invalid_timeout_should_be_rejected(string timeout)
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;

        viewModel.Target = "127.0.0.1";
        viewModel.TimeoutText = timeout;
        viewModel.RunCommand.Execute(null);

        viewModel.TimeoutError.ShouldNotBeNullOrWhiteSpace();
        harness.Probe.PingCallCount.ShouldBe(0);
    }

    [Fact]
    public void External_target_should_require_explicit_authorization()
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;

        viewModel.Target = "camera.example.com";

        viewModel.RequiresAuthorization.ShouldBeTrue();

        viewModel.RunCommand.Execute(null);

        viewModel.State.ShouldBe(TaskRunState.Pending);
        viewModel.Conclusion.ShouldContain("授权");
        viewModel.HasResult.ShouldBeTrue();
        harness.Probe.PingCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Loopback_target_should_not_require_authorization(string target)
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;

        viewModel.Target = target;

        viewModel.RequiresAuthorization.ShouldBeFalse();
    }

    [Fact]
    public async Task Successful_run_should_populate_real_steps_summary_and_logs()
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;

        viewModel.Target = "127.0.0.1";
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.IsRunning.ShouldBeFalse();
        viewModel.State.ShouldBe(TaskRunState.Completed);
        viewModel.HasResult.ShouldBeTrue();
        viewModel.Steps.ShouldHaveSingleItem();
        viewModel.Steps[0].Status.ShouldBe(DiagnosticStatus.Passed);
        viewModel.SummaryItems.ShouldContain(static item => item.Label == "工具");
        viewModel.SummaryItems.ShouldContain(static item => item.Label == "目标");
        viewModel.Logs.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Failed_tcp_connection_should_be_reported_as_failed()
    {
        using var harness = new NetworkDiagnosticsHarness();
        harness.Probe.TcpBehavior = (target, port, _, _) => Task.FromResult(new TcpObservation(
            NetworkObservationStatus.Failed,
            $"TCP {target}:{port} 连接被拒绝。",
            [new NetworkFact("目标", $"{target}:{port}")],
            "检查服务监听与防火墙。"));

        var viewModel = harness.ViewModel;
        viewModel.SelectedTool = viewModel.Tools.Single(static t => t.Tool == NetworkTool.Tcp);
        viewModel.Target = "127.0.0.1";

        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Failed);
        viewModel.Steps[0].Status.ShouldBe(DiagnosticStatus.Failed);
        viewModel.Conclusion.ShouldContain("拒绝");
    }

    [Fact]
    public async Task Unknown_ping_should_remain_inconclusive_not_failed()
    {
        using var harness = new NetworkDiagnosticsHarness();
        harness.Probe.PingBehavior = (target, _, _) => Task.FromResult(new PingObservation(
            NetworkObservationStatus.Unknown,
            "ICMP 无响应。这不能单独证明主机不可达。",
            [new NetworkFact("目标", target)],
            "继续用 TCP 或应用层协议验证。"));

        var viewModel = harness.ViewModel;
        viewModel.Target = "127.0.0.1";

        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Inconclusive);
        viewModel.Steps[0].Status.ShouldBe(DiagnosticStatus.Unknown);
        viewModel.Conclusion.ShouldContain("不能单独证明");
    }

    [Fact]
    public async Task Cancellation_should_be_reported_as_cancelled_not_failed()
    {
        using var harness = new NetworkDiagnosticsHarness();
        harness.Probe.PingBehavior = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("不应到达这里。");
        };

        var viewModel = harness.ViewModel;
        viewModel.Target = "127.0.0.1";

        var run = viewModel.RunCommand.ExecuteAsync(null);
        viewModel.CancelCommand.Execute(null);
        await run;

        viewModel.IsRunning.ShouldBeFalse();
        viewModel.State.ShouldBe(TaskRunState.Cancelled);
        viewModel.Conclusion.ShouldContain("取消");
    }

    [Fact]
    public async Task Local_mode_should_not_require_a_target_and_should_read_snapshot()
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;

        viewModel.SelectedTool = viewModel.Tools.Single(static t => t.Tool == NetworkTool.Local);

        viewModel.RequiresTarget.ShouldBeFalse();
        viewModel.RequiresAuthorization.ShouldBeFalse();

        await viewModel.RunCommand.ExecuteAsync(null);

        harness.Probe.LocalCallCount.ShouldBe(1);
        viewModel.State.ShouldBe(TaskRunState.Completed);
        viewModel.Steps[0].FactItems.ShouldContain(static f => f.Label.Contains("网卡"));
    }

    [Fact]
    public async Task Copy_summary_should_redact_secrets_before_reaching_clipboard()
    {
        using var harness = new NetworkDiagnosticsHarness();
        harness.Probe.PingBehavior = (target, _, _) => Task.FromResult(new PingObservation(
            NetworkObservationStatus.Failed,
            "请求失败：Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature",
            [new NetworkFact("目标", target)],
            "检查 password=SuperSecret123 配置。"));

        var viewModel = harness.ViewModel;
        viewModel.Target = "127.0.0.1";
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.CopySummaryCommand.Execute(null);

        harness.Exporter.CopiedText.ShouldNotBeNull();
        harness.Exporter.CopiedText.ShouldNotContain("SuperSecret123");
        harness.Exporter.CopiedText.ShouldNotContain("eyJhbGciOiJIUzI1NiJ9");
    }

    [Fact]
    public async Task Export_json_should_redact_facts_and_raw_output()
    {
        using var harness = new NetworkDiagnosticsHarness();
        harness.Probe.LocalBehavior = _ => Task.FromResult(new LocalNetworkSnapshot(
            [new NetworkFact("凭据", "token=abcdef123456")],
            "raw password=SuperSecret123"));

        var viewModel = harness.ViewModel;
        viewModel.SelectedTool = viewModel.Tools.Single(static t => t.Tool == NetworkTool.Local);
        await viewModel.RunCommand.ExecuteAsync(null);

        await viewModel.ExportCommand.ExecuteAsync(null);

        harness.Exporter.ExportedContent.ShouldNotBeNull();
        harness.Exporter.ExportedContent.ShouldNotContain("abcdef123456");
        harness.Exporter.ExportedContent.ShouldNotContain("SuperSecret123");
    }

    [Fact]
    public async Task Export_cancellation_should_be_reported_as_cancelled()
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;
        viewModel.Target = "127.0.0.1";
        await viewModel.RunCommand.ExecuteAsync(null);

        harness.Exporter.ExportBehavior = (_, _, _) => Task.FromResult<string?>(null);

        await viewModel.ExportCommand.ExecuteAsync(null);

        viewModel.ActionFeedback.ShouldContain("取消");
    }

    [Fact]
    public async Task Export_failure_should_be_reported_without_crashing()
    {
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = harness.ViewModel;
        viewModel.Target = "127.0.0.1";
        await viewModel.RunCommand.ExecuteAsync(null);

        harness.Exporter.ExportBehavior = (_, _, _) =>
            Task.FromException<string?>(new IOException("磁盘只读。"));

        await viewModel.ExportCommand.ExecuteAsync(null);

        viewModel.ActionFeedback.ShouldContain("导出失败");
        viewModel.ActionFeedback.ShouldContain("磁盘只读");
    }
}
