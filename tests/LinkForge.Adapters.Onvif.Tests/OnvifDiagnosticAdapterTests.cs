using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Adapters.Onvif.Tests;

public class OnvifDiagnosticAdapterTests
{
    [Fact]
    public void Plan_should_minimize_scope_for_each_tool()
    {
        var adapter = new OnvifDiagnosticAdapter(new FakeOnvifProbe());

        var discovery = adapter.Plan(Request(OnvifTool.Discover));
        var inspect = adapter.Plan(Request(OnvifTool.Inspect));

        discovery.ShouldHaveSingleItem().StepId.ShouldBe("onvif.discovery");
        inspect.Select(static step => step.StepId)
            .ShouldBe(["onvif.device", "onvif.capabilities", "onvif.media"]);
        inspect[1].Prerequisites.ShouldContain("onvif.device");
        inspect[2].Prerequisites.ShouldContain("onvif.capabilities");
    }

    [Fact]
    public async Task Discovery_should_expose_real_endpoints_without_credentials()
    {
        var probe = new FakeOnvifProbe
        {
            Discovery = new OnvifDiscoveryObservation(
                OnvifObservationStatus.Passed,
                "发现 1 个设备。",
                [new OnvifDiscoveredDevice(
                    "http://10.0.0.8/onvif/device_service",
                    "前门摄像机",
                    ["onvif://www.onvif.org/type/video_encoder"],
                    ["dn:NetworkVideoTransmitter"])])
        };
        var adapter = new OnvifDiagnosticAdapter(probe);
        var context = Context(OnvifTool.Discover);
        var progress = new List<DiagnosticProgress>();

        var result = await adapter.ExecuteStepAsync(
            context,
            new CollectingProgress(progress),
            CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Passed);
        result.FactItems.ShouldContain(f =>
            f.Label.Contains("前门摄像机", StringComparison.Ordinal)
            && f.Value == "http://10.0.0.8/onvif/device_service");
        result.FactItems.ShouldNotContain(f => f.Value.Contains("secret", StringComparison.OrdinalIgnoreCase));
        progress.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Inspect_should_chain_device_capabilities_and_media()
    {
        var probe = new FakeOnvifProbe();
        var adapter = new OnvifDiagnosticAdapter(probe);
        var runId = Guid.NewGuid().ToString("N");

        var first = await ExecuteInspectStepAsync(adapter, "onvif.device", runId);
        var second = await ExecuteInspectStepAsync(adapter, "onvif.capabilities", runId);
        var third = await ExecuteInspectStepAsync(adapter, "onvif.media", runId);

        first.Status.ShouldBe(DiagnosticStatus.Passed);
        second.Status.ShouldBe(DiagnosticStatus.Passed);
        third.Status.ShouldBe(DiagnosticStatus.Passed);
        third.FactItems.ShouldContain(f => f.Label == "RTSP Stream URI");
        probe.LastCredentials.ShouldNotBeNull().Password.ShouldBe("secret");
        third.FactItems.ShouldNotContain(f =>
            f.Value.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Missing_service_url_should_fail_before_network_call()
    {
        var probe = new FakeOnvifProbe();
        var adapter = new OnvifDiagnosticAdapter(probe);
        var context = Context(OnvifTool.Inspect) with
        {
            Parameters = Parameters(OnvifTool.Inspect, serviceUrl: string.Empty)
        };

        var result = await adapter.ExecuteStepAsync(
            context,
            new CollectingProgress([]),
            CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Failed);
        result.Summary.ShouldContain("URL");
        probe.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Userinfo_in_service_url_should_be_rejected_to_prevent_credential_leakage()
    {
        var adapter = new OnvifDiagnosticAdapter(new FakeOnvifProbe());
        var context = Context(OnvifTool.Inspect) with
        {
            Parameters = Parameters(
                OnvifTool.Inspect,
                serviceUrl: "http://admin:secret@10.0.0.8/onvif/device_service")
        };

        var result = await adapter.ExecuteStepAsync(
            context,
            new CollectingProgress([]),
            CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Failed);
        result.Summary.ShouldContain("userinfo");
        result.Summary.ShouldNotContain("secret");
    }

    [Fact]
    public async Task Warning_and_timeout_should_keep_their_semantics()
    {
        var probe = new FakeOnvifProbe
        {
            Device = new OnvifOperationObservation(
                OnvifObservationStatus.Warning,
                "设备信息不完整。",
                [new OnvifFact("型号", "未知")],
                Suggestion: "检查设备固件。")
        };
        var adapter = new OnvifDiagnosticAdapter(probe);
        var warning = await ExecuteInspectStepAsync(adapter, "onvif.device", Guid.NewGuid().ToString("N"));

        probe.Device = new OnvifOperationObservation(
            OnvifObservationStatus.TimedOut,
            "超时。",
            []);
        var timeout = await ExecuteInspectStepAsync(adapter, "onvif.device", Guid.NewGuid().ToString("N"));

        warning.Status.ShouldBe(DiagnosticStatus.Warning);
        timeout.Status.ShouldBe(DiagnosticStatus.TimedOut);
    }

    [Fact]
    public void Endpoint_validation_should_reject_non_http_and_userinfo()
    {
        OnvifEndpoint.TryParse("rtsp://camera/onvif", out _, out var schemeError).ShouldBeFalse();
        schemeError.ShouldNotBeNullOrWhiteSpace();

        OnvifEndpoint.TryParse("http://admin:secret@camera/onvif/device_service", out _, out var userInfoError)
            .ShouldBeFalse();
        userInfoError.ShouldNotBeNullOrWhiteSpace();

        OnvifEndpoint.TryParse("http://camera/onvif/device_service", out var endpoint, out _).ShouldBeTrue();
        endpoint.DisplayUrl.ShouldBe("http://camera/onvif/device_service");
    }

    private static async Task<DiagnosticStepResult> ExecuteInspectStepAsync(
        OnvifDiagnosticAdapter adapter,
        string stepId,
        string runId)
    {
        var context = Context(OnvifTool.Inspect) with
        {
            RunId = runId,
            StepId = stepId
        };

        return await adapter.ExecuteStepAsync(
            context,
            new CollectingProgress([]),
            CancellationToken.None);
    }

    private static DiagnosticRequest Request(OnvifTool tool) =>
        new(Guid.NewGuid().ToString("N"), "test", Parameters(tool));

    private static DiagnosticStepContext Context(OnvifTool tool) =>
        new(
            Guid.NewGuid().ToString("N"),
            tool == OnvifTool.Discover ? "onvif.discovery" : "onvif.device",
            tool == OnvifTool.Discover ? "发现" : "设备信息",
            "test",
            Parameters(tool),
            DateTimeOffset.Now);

    private static IReadOnlyDictionary<string, string?> Parameters(
        OnvifTool tool,
        string? serviceUrl = null) =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [OnvifParameterNames.Tool] = tool.ToString(),
            [OnvifParameterNames.ServiceUrl] =
                serviceUrl ?? "http://127.0.0.1/onvif/device_service",
            [OnvifParameterNames.Username] = "admin",
            [OnvifParameterNames.Password] = "secret",
            [OnvifParameterNames.TimeoutSeconds] = "5"
        };

    private sealed class CollectingProgress(List<DiagnosticProgress> items) : IProgress<DiagnosticProgress>
    {
        public void Report(DiagnosticProgress value) => items.Add(value);
    }

    private sealed class FakeOnvifProbe : IOnvifProbe
    {
        public OnvifDiscoveryObservation Discovery { get; set; } =
            new(OnvifObservationStatus.Passed, "发现设备。", []);

        public OnvifOperationObservation Device { get; set; } =
            new(
                OnvifObservationStatus.Passed,
                "设备信息。",
                [new OnvifFact("厂商", "测试厂商")]);

        public OnvifOperationObservation Capabilities { get; set; } =
            new(
                OnvifObservationStatus.Passed,
                "能力。",
                [new OnvifFact("Media", "http://127.0.0.1/media")]);

        public OnvifOperationObservation Media { get; set; } =
            new(
                OnvifObservationStatus.Passed,
                "媒体。",
                [
                    new OnvifFact("Profile Token 1", "profile-1"),
                    new OnvifFact("RTSP Stream URI", "rtsp://127.0.0.1:554/live")
                ]);

        public OnvifCredentials? LastCredentials { get; private set; }

        public int CallCount { get; private set; }

        public Task<OnvifDiscoveryObservation> DiscoverAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Discovery);
        }

        public Task<OnvifOperationObservation> GetDeviceInformationAsync(
            OnvifEndpoint endpoint,
            OnvifCredentials? credentials,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastCredentials = credentials;
            return Task.FromResult(Device);
        }

        public Task<OnvifOperationObservation> GetCapabilitiesAsync(
            OnvifEndpoint endpoint,
            OnvifCredentials? credentials,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastCredentials = credentials;
            return Task.FromResult(Capabilities);
        }

        public Task<OnvifOperationObservation> GetMediaProfilesAsync(
            OnvifEndpoint endpoint,
            OnvifCredentials? credentials,
            string? profileToken,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastCredentials = credentials;
            return Task.FromResult(Media);
        }
    }
}
