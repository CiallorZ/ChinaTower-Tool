namespace LinkForge.Adapters.Video.Tests;

public class RtspTransportTests
{
    [Fact]
    public async Task Real_local_basic_auth_and_sdp_should_succeed()
    {
        await using var server = LocalRtspServer.Start(requireBasicAuth: true);
        var probe = new WindowsRtspProbe();
        var ok = RtspUrlParser.TryParse(
            $"rtsp://admin:secret@127.0.0.1:{server.Port}/live",
            out var endpoint,
            out var error);

        ok.ShouldBeTrue(error);
        var observation = await probe.ProbeAsync(endpoint!, TimeSpan.FromSeconds(5), CancellationToken.None);

        observation.Status.ShouldBe(RtspObservationStatus.Passed);
        observation.Stages.ShouldContain(s => s.StageId == "authentication" && s.Status == RtspObservationStatus.Passed);
        observation.Stages.ShouldContain(s => s.StageId == "sdp" && s.Status == RtspObservationStatus.Passed);
        observation.Stages.ShouldContain(s => s.StageName.Contains("RTP") && s.Status == RtspObservationStatus.Skipped);
        server.Requests.ShouldContain(r => r.StartsWith("OPTIONS ", StringComparison.Ordinal));
        server.Requests.ShouldContain(r => r.StartsWith("DESCRIBE ", StringComparison.Ordinal));
        server.Requests.ShouldContain(r => r.Contains("Authorization: Basic ", StringComparison.Ordinal));
        observation.RawOutput.ShouldNotBeNull().ShouldNotContain("secret");
    }

    [Fact]
    public async Task Missing_credentials_for_protected_server_should_fail_at_authentication()
    {
        await using var server = LocalRtspServer.Start(requireBasicAuth: true);
        var probe = new WindowsRtspProbe();
        RtspUrlParser.TryParse($"rtsp://127.0.0.1:{server.Port}/live", out var endpoint, out _)
            .ShouldBeTrue();

        var observation = await probe.ProbeAsync(endpoint!, TimeSpan.FromSeconds(5), CancellationToken.None);

        observation.Status.ShouldBe(RtspObservationStatus.Failed);
        observation.Stages.ShouldContain(s =>
            s.StageId == "authentication" && s.Status == RtspObservationStatus.Failed);
        observation.Suggestion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Invalid_sdp_should_fail_and_not_claim_playback()
    {
        await using var server = LocalRtspServer.Start(sdp: "not an sdp");
        var probe = new WindowsRtspProbe();
        RtspUrlParser.TryParse($"rtsp://127.0.0.1:{server.Port}/live", out var endpoint, out _)
            .ShouldBeTrue();

        var observation = await probe.ProbeAsync(endpoint!, TimeSpan.FromSeconds(5), CancellationToken.None);

        observation.Status.ShouldBe(RtspObservationStatus.Failed);
        observation.Stages.ShouldContain(s => s.StageId == "sdp" && s.Status == RtspObservationStatus.Failed);
        observation.Summary.ShouldNotContain("已播放");
    }

    [Fact]
    public async Task Silent_server_should_timeout()
    {
        await using var server = LocalRtspServer.Start(silent: true);
        var probe = new WindowsRtspProbe();
        RtspUrlParser.TryParse($"rtsp://127.0.0.1:{server.Port}/live", out var endpoint, out _)
            .ShouldBeTrue();

        var observation = await probe.ProbeAsync(endpoint!, TimeSpan.FromMilliseconds(250), CancellationToken.None);

        observation.Status.ShouldBe(RtspObservationStatus.TimedOut);
    }

    [Fact]
    public async Task Cancellation_should_propagate()
    {
        await using var server = LocalRtspServer.Start(silent: true);
        var probe = new WindowsRtspProbe();
        RtspUrlParser.TryParse($"rtsp://127.0.0.1:{server.Port}/live", out var endpoint, out _)
            .ShouldBeTrue();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            probe.ProbeAsync(endpoint!, TimeSpan.FromSeconds(10), cts.Token));
    }

    [Fact]
    public async Task Adapter_should_map_success_and_keep_redacted_target()
    {
        await using var server = LocalRtspServer.Start();
        var adapter = new RtspDiagnosticAdapter(new WindowsRtspProbe());
        var url = $"rtsp://admin:secret@127.0.0.1:{server.Port}/live";
        var request = new Plugin.Abstractions.DiagnosticRequest(
            "run",
            url,
            new Dictionary<string, string?>
            {
                [RtspParameterNames.Url] = url,
                [RtspParameterNames.TimeoutSeconds] = "5"
            });
        var context = new Plugin.Abstractions.DiagnosticStepContext(
            "run",
            "rtsp",
            "RTSP 诊断",
            url,
            request.ParameterMap,
            DateTimeOffset.Now);

        var result = await adapter.ExecuteStepAsync(
            context,
            new Progress<Plugin.Abstractions.DiagnosticProgress>(),
            CancellationToken.None);

        result.Status.ShouldBe(Domain.Diagnostics.DiagnosticStatus.Passed);
        result.Summary.ShouldNotContain("已播放");
        result.FactItems.ShouldContain(f => f.Value.Contains("***:***@", StringComparison.Ordinal));
        result.FactItems.ShouldNotContain(f => f.Value.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Adapter_invalid_url_should_fail_before_network()
    {
        var adapter = new RtspDiagnosticAdapter(new WindowsRtspProbe());
        var request = new Plugin.Abstractions.DiagnosticRequest(
            "run",
            "bad",
            new Dictionary<string, string?> { [RtspParameterNames.Url] = "http://bad" });
        var context = new Plugin.Abstractions.DiagnosticStepContext(
            "run",
            "rtsp",
            "RTSP 诊断",
            "bad",
            request.ParameterMap,
            DateTimeOffset.Now);

        var result = await adapter.ExecuteStepAsync(
            context,
            new Progress<Plugin.Abstractions.DiagnosticProgress>(),
            CancellationToken.None);

        result.Status.ShouldBe(Domain.Diagnostics.DiagnosticStatus.Failed);
        result.Summary.ShouldContain("rtsp");
    }
}
