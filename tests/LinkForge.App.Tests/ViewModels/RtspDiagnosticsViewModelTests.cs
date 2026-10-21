using LinkForge.Adapters.Video;
using LinkForge.App.Controls;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

public class RtspDiagnosticsViewModelTests
{
    [Fact]
    public void Redacted_url_should_hide_password_and_authorization_should_follow_host()
    {
        using var harness = new RtspDiagnosticsHarness();
        var vm = harness.ViewModel;

        vm.Url = "rtsp://admin:SuperSecret123@10.0.0.8:554/live";

        vm.RedactedUrl.ShouldContain("***:***@");
        vm.RedactedUrl.ShouldNotContain("SuperSecret123");
        vm.RequiresAuthorization.ShouldBeTrue();

        vm.Url = "rtsp://127.0.0.1:554/live";
        vm.RequiresAuthorization.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://camera/live")]
    [InlineData("rtsp://")]
    public async Task Invalid_url_should_not_call_the_probe(string url)
    {
        using var harness = new RtspDiagnosticsHarness();
        harness.ViewModel.Url = url;

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.Probe.CallCount.ShouldBe(0);
        harness.ViewModel.UrlError.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task External_target_should_require_authorization_before_probe()
    {
        using var harness = new RtspDiagnosticsHarness();
        harness.ViewModel.Url = "rtsp://10.0.0.8:554/live";

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.Probe.CallCount.ShouldBe(0);
        harness.ViewModel.HasResult.ShouldBeTrue();
        harness.ViewModel.Conclusion.ShouldContain("授权");
    }

    [Fact]
    public async Task Success_should_populate_real_steps_and_not_claim_playback()
    {
        using var harness = new RtspDiagnosticsHarness();
        harness.ViewModel.Url = "rtsp://127.0.0.1:554/live";

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.ViewModel.State.ShouldBe(TaskRunState.Completed);
        harness.ViewModel.Steps.ShouldNotBeEmpty();
        harness.ViewModel.Conclusion.ShouldContain("媒体接收/解码未执行");
        harness.ViewModel.Conclusion.ShouldNotContain("已播放");
        harness.ViewModel.SummaryItems.ShouldContain(i => i.Label == "URL");
    }

    [Fact]
    public async Task Failure_should_preserve_failed_stage_semantics()
    {
        using var harness = new RtspDiagnosticsHarness();
        harness.Probe.Behavior = (_, _, _) => Task.FromResult(new RtspObservation(
            RtspObservationStatus.Failed,
            "认证失败。",
            [new RtspStageObservation(
                "authentication",
                "认证",
                RtspObservationStatus.Failed,
                "DESCRIBE 返回 401。",
                [],
                null,
                "核对凭据。")],
            [],
            "核对凭据。"));
        harness.ViewModel.Url = "rtsp://127.0.0.1:554/live";

        await harness.ViewModel.RunCommand.ExecuteAsync(null);

        harness.ViewModel.State.ShouldBe(TaskRunState.Failed);
        harness.ViewModel.Steps.ShouldContain(s => s.Status == DiagnosticStatus.Failed);
        harness.ViewModel.ConclusionNote.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cancellation_should_be_cancelled_not_failed()
    {
        using var harness = new RtspDiagnosticsHarness();
        harness.Probe.Behavior = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return FakeRtspProbe.DefaultSuccess();
        };
        harness.ViewModel.Url = "rtsp://127.0.0.1:554/live";

        var run = harness.ViewModel.RunCommand.ExecuteAsync(null);
        harness.ViewModel.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        harness.ViewModel.State.ShouldBe(TaskRunState.Cancelled);
        harness.ViewModel.Conclusion.ShouldContain("取消");
    }

    [Fact]
    public async Task Copy_and_export_should_be_redacted()
    {
        using var harness = new RtspDiagnosticsHarness();
        harness.ViewModel.Url = "rtsp://admin:SuperSecret123@127.0.0.1:554/live";

        await harness.ViewModel.RunCommand.ExecuteAsync(null);
        harness.ViewModel.CopySummaryCommand.Execute(null);
        await harness.ViewModel.ExportCommand.ExecuteAsync(null);

        harness.Exporter.CopiedText.ShouldNotBeNull().ShouldNotContain("SuperSecret123");
        harness.Exporter.ExportedContent.ShouldNotBeNull().ShouldNotContain("SuperSecret123");
        harness.Exporter.ExportedContent.ShouldContain("mediaNotice");
    }
}
