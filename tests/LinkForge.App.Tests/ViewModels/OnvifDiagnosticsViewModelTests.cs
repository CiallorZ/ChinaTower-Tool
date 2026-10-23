using System.IO;
using LinkForge.Adapters.Onvif;
using LinkForge.App.ViewModels;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

public class OnvifDiagnosticsViewModelTests
{
    [Fact]
    public void Endpoint_and_authorization_should_follow_validated_scope()
    {
        using var harness = new OnvifDiagnosticsHarness();
        var vm = harness.ViewModel;

        vm.ServiceUrl = "http://10.0.0.8/onvif/device_service";
        vm.RedactedServiceUrl.ShouldBe("http://10.0.0.8/onvif/device_service");
        vm.RequiresAuthorization.ShouldBeTrue();

        vm.ServiceUrl = "http://127.0.0.1/onvif/device_service";
        vm.RequiresAuthorization.ShouldBeFalse();

        vm.SelectedTool = vm.Tools.Single(tool => tool.Tool == OnvifTool.Discover);
        vm.RequiresAuthorization.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("rtsp://camera/onvif/device_service")]
    [InlineData("http://admin:secret@camera/onvif/device_service")]
    public async Task Invalid_service_url_should_not_call_the_probe(string url)
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.ServiceUrl = url;

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.Probe.CallCount.ShouldBe(0);
        harness.ViewModel.ServiceUrlError.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("abc")]
    public async Task Invalid_timeout_should_not_call_the_probe(string timeout)
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.ServiceUrl = "http://127.0.0.1/onvif/device_service";
        harness.ViewModel.TimeoutText = timeout;

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.Probe.CallCount.ShouldBe(0);
        harness.ViewModel.TimeoutError.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task External_inspect_should_require_authorization_before_probe()
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.ServiceUrl = "http://10.0.0.8/onvif/device_service";

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.Probe.CallCount.ShouldBe(0);
        harness.ViewModel.HasResult.ShouldBeTrue();
        harness.ViewModel.Conclusion.ShouldContain("授权");
    }

    [Fact]
    public async Task Discover_should_require_authorization_even_for_local_network()
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.SelectedTool =
            harness.ViewModel.Tools.Single(tool => tool.Tool == OnvifTool.Discover);

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.Probe.CallCount.ShouldBe(0);
        harness.ViewModel.Conclusion.ShouldContain("授权");
    }

    [Fact]
    public async Task Inspect_success_should_chain_real_steps_and_not_claim_playback()
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.ServiceUrl = "http://127.0.0.1/onvif/device_service";
        harness.ViewModel.Username = "admin";
        harness.ViewModel.Password = "LocalSecret42";
        harness.ViewModel.ProfileToken = "profile-9";

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.ViewModel.State.ShouldBe(TaskRunState.Completed);
        harness.ViewModel.Steps.Select(static step => step.StepId)
            .ShouldBe(["onvif.device", "onvif.capabilities", "onvif.media"]);
        harness.Probe.CallCount.ShouldBe(3);
        harness.Probe.LastCredentials.ShouldNotBeNull().Password.ShouldBe("LocalSecret42");
        harness.Probe.LastProfileToken.ShouldBe("profile-9");
        harness.ViewModel.Conclusion.ShouldContain("未接收 RTP");
        harness.ViewModel.Conclusion.ShouldNotContain("已播放");
        harness.ViewModel.SummaryItems.ShouldContain(item => item.Label == "目标");
    }

    [Fact]
    public async Task Discovery_without_devices_should_remain_unknown()
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.SelectedTool =
            harness.ViewModel.Tools.Single(tool => tool.Tool == OnvifTool.Discover);
        harness.ViewModel.IsAuthorized = true;
        harness.Probe.Discovery = new OnvifDiscoveryObservation(
            OnvifObservationStatus.Unknown,
            "等待窗口内未收到响应。",
            []);

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.ViewModel.State.ShouldBe(TaskRunState.Inconclusive);
        harness.ViewModel.Steps.ShouldContain(step => step.Status == DiagnosticStatus.Unknown);
        harness.ViewModel.Conclusion.ShouldContain("不代表网络中没有 ONVIF 设备");
    }

    [Fact]
    public async Task Failure_should_stop_the_chain_and_preserve_failed_semantics()
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.ServiceUrl = "http://127.0.0.1/onvif/device_service";
        harness.Probe.Device = new OnvifOperationObservation(
            OnvifObservationStatus.Failed,
            "设备返回 401。",
            [],
            Suggestion: "核对 WS-Security 凭据。");

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.ViewModel.State.ShouldBe(TaskRunState.Failed);
        harness.ViewModel.Steps.ShouldContain(step => step.Status == DiagnosticStatus.Failed);
        harness.ViewModel.Steps.ShouldContain(step => step.Status == DiagnosticStatus.Skipped);
        harness.Probe.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Cancellation_should_be_cancelled_not_failed()
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.SelectedTool =
            harness.ViewModel.Tools.Single(tool => tool.Tool == OnvifTool.Discover);
        harness.ViewModel.IsAuthorized = true;
        harness.Probe.DiscoveryBehavior = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new OnvifDiscoveryObservation(OnvifObservationStatus.Unknown, "不应到达。", []);
        };

        var run = harness.ViewModel.RunCommand.ExecuteAsync(null);
        harness.ViewModel.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        harness.ViewModel.State.ShouldBe(TaskRunState.Cancelled);
        harness.ViewModel.Conclusion.ShouldContain("取消");
    }

    [Fact]
    public async Task Copy_and_export_should_not_include_credentials()
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.ServiceUrl = "http://127.0.0.1/onvif/device_service";
        harness.ViewModel.Username = "AdminSecretName";
        harness.ViewModel.Password = "LocalSecret42";

        await harness.ViewModel.RunCommand.ExecuteAsync(null);
        harness.ViewModel.CopySummaryCommand.Execute(null);
        await harness.ViewModel.ExportCommand.ExecuteAsync(null);

        var copied = harness.Exporter.CopiedText.ShouldNotBeNull();
        copied.ShouldNotContain("AdminSecretName");
        copied.ShouldNotContain("LocalSecret42");

        var exported = harness.Exporter.ExportedContent.ShouldNotBeNull();
        exported.ShouldNotContain("AdminSecretName");
        exported.ShouldNotContain("LocalSecret42");
        harness.Exporter.ExportedContent.ShouldContain("mediaNotice");
        harness.Exporter.ExportedContent.ShouldContain("credentialsNotice");
    }

    [Fact]
    public async Task Export_cancel_and_failure_should_be_reported_without_false_success()
    {
        using var harness = new OnvifDiagnosticsHarness();
        harness.ViewModel.ServiceUrl = "http://127.0.0.1/onvif/device_service";
        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.Exporter.ExportBehavior = static (_, _, _) =>
            Task.FromResult<string?>(null);
        await harness.ViewModel.ExportCommand.ExecuteAsync(null);
        harness.ViewModel.ActionFeedback.ShouldContain("取消");

        harness.Exporter.ExportBehavior = static (_, _, _) =>
            Task.FromException<string?>(new IOException("磁盘只读"));
        await harness.ViewModel.ExportCommand.ExecuteAsync(null);
        harness.ViewModel.ActionFeedback.ShouldContain("失败");
        harness.ViewModel.ActionFeedback.ShouldContain("磁盘只读");
    }
}
