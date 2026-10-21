using System.IO;
using LinkForge.Adapters.Video;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.Application.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>RTSP 视图模型测试夹具；替换系统网络与剪贴板边界。</summary>
internal sealed class RtspDiagnosticsHarness : IDisposable
{
    public RtspDiagnosticsHarness(TimeProvider? timeProvider = null)
    {
        Probe = new FakeRtspProbe();
        Adapter = new RtspDiagnosticAdapter(Probe);
        Runner = new DiagnosticTaskRunner(new SecretTextRedactor(), timeProvider);
        Exporter = new FakeRtspResultExporter();
        ViewModel = new RtspDiagnosticsViewModel(Adapter, Runner, Exporter, timeProvider);
    }

    public FakeRtspProbe Probe { get; }

    public RtspDiagnosticAdapter Adapter { get; }

    public DiagnosticTaskRunner Runner { get; }

    public FakeRtspResultExporter Exporter { get; }

    public RtspDiagnosticsViewModel ViewModel { get; }

    public void Dispose()
    {
    }
}

/// <summary>可编排的 RTSP 探测边界。</summary>
internal sealed class FakeRtspProbe : IRtspProbe
{
    public Func<RtspEndpoint, TimeSpan, CancellationToken, Task<RtspObservation>>? Behavior { get; set; }

    public int CallCount { get; private set; }

    public Task<RtspObservation> ProbeAsync(
        RtspEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        CallCount++;

        return Behavior?.Invoke(endpoint, timeout, cancellationToken)
            ?? Task.FromResult(DefaultSuccess());
    }

    public static RtspObservation DefaultSuccess() =>
        new(
            RtspObservationStatus.Passed,
            "RTSP 信令诊断完成。",
            [
                new RtspStageObservation(
                    "network",
                    "TCP 连接",
                    RtspObservationStatus.Passed,
                    "TCP 连接已建立。",
                    [new RtspFact("目标", "127.0.0.1:554")]),
                new RtspStageObservation(
                    "sdp",
                    "SDP 解析",
                    RtspObservationStatus.Passed,
                    "SDP 有效。",
                    [new RtspFact("编码", "H264/90000")]),
                new RtspStageObservation(
                    "media",
                    "RTP / 解码 / 首帧",
                    RtspObservationStatus.Skipped,
                    "未执行媒体接收/解码。",
                    [])
            ],
            [new RtspFact("目标", "127.0.0.1:554")],
            null,
            "RTSP/1.0 200 OK");
}

/// <summary>记录复制/导出内容。</summary>
internal sealed class FakeRtspResultExporter : IRtspResultExporter
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
