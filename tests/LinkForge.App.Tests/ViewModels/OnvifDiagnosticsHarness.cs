using System.IO;
using LinkForge.Adapters.Onvif;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.Application.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>ONVIF 视图模型测试夹具；替换 UDP、HTTP 与剪贴板边界。</summary>
internal sealed class OnvifDiagnosticsHarness : IDisposable
{
    public OnvifDiagnosticsHarness(TimeProvider? timeProvider = null)
    {
        Probe = new FakeOnvifProbe();
        Adapter = new OnvifDiagnosticAdapter(Probe);
        Runner = new DiagnosticTaskRunner(new SecretTextRedactor(), timeProvider);
        Exporter = new FakeOnvifResultExporter();
        ViewModel = new OnvifDiagnosticsViewModel(Adapter, Runner, Exporter, timeProvider);
    }

    public FakeOnvifProbe Probe { get; }

    public OnvifDiagnosticAdapter Adapter { get; }

    public DiagnosticTaskRunner Runner { get; }

    public FakeOnvifResultExporter Exporter { get; }

    public OnvifDiagnosticsViewModel ViewModel { get; }

    public void Dispose()
    {
    }
}

/// <summary>可编排的 ONVIF 系统边界。</summary>
internal sealed class FakeOnvifProbe : IOnvifProbe
{
    public OnvifDiscoveryObservation Discovery { get; set; } =
        new(
            OnvifObservationStatus.Passed,
            "发现 1 个设备。",
            [
                new OnvifDiscoveredDevice(
                    "http://127.0.0.1/onvif/device_service",
                    "测试摄像机",
                    ["onvif://www.onvif.org/name/测试摄像机"],
                    ["dn:NetworkVideoTransmitter"])
            ]);

    public OnvifOperationObservation Device { get; set; } =
        new(
            OnvifObservationStatus.Passed,
            "已读取设备信息。",
            [new OnvifFact("厂商", "Acme"), new OnvifFact("型号", "Cam-X1")]);

    public OnvifOperationObservation Capabilities { get; set; } =
        new(
            OnvifObservationStatus.Passed,
            "已读取服务能力。",
            [new OnvifFact("Media", "http://127.0.0.1/onvif/media_service")]);

    public OnvifOperationObservation Media { get; set; } =
        new(
            OnvifObservationStatus.Passed,
            "已读取媒体 Profile 与 Stream URI。",
            [
                new OnvifFact("Profile Token 1", "profile-1"),
                new OnvifFact("编码", "H264"),
                new OnvifFact("RTSP Stream URI", "rtsp://127.0.0.1:554/live")
            ]);

    public Func<CancellationToken, Task<OnvifDiscoveryObservation>>? DiscoveryBehavior { get; set; }

    public Func<CancellationToken, Task<OnvifOperationObservation>>? DeviceBehavior { get; set; }

    public OnvifCredentials? LastCredentials { get; private set; }

    public string? LastProfileToken { get; private set; }

    public int CallCount { get; private set; }

    public Task<OnvifDiscoveryObservation> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return DiscoveryBehavior?.Invoke(cancellationToken) ?? Task.FromResult(Discovery);
    }

    public Task<OnvifOperationObservation> GetDeviceInformationAsync(
        OnvifEndpoint endpoint,
        OnvifCredentials? credentials,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastCredentials = credentials;
        return DeviceBehavior?.Invoke(cancellationToken) ?? Task.FromResult(Device);
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
        LastProfileToken = profileToken;
        return Task.FromResult(Media);
    }
}

/// <summary>记录 ONVIF 复制/导出内容。</summary>
internal sealed class FakeOnvifResultExporter : IOnvifResultExporter
{
    public string? CopiedText { get; private set; }

    public string? ExportedContent { get; private set; }

    public Func<string, string, CancellationToken, Task<string?>>? ExportBehavior { get; set; }

    public void Copy(string text) => CopiedText = text;

    public Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        ExportedContent = content;

        return ExportBehavior?.Invoke(suggestedFileName, content, cancellationToken)
            ?? Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), suggestedFileName));
    }
}
