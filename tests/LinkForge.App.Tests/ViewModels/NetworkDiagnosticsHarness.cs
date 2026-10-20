using System.IO;
using LinkForge.Adapters.Network;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.Application.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// 构造 <see cref="NetworkDiagnosticsViewModel"/> 的测试夹具。
/// </summary>
/// <remarks>
/// 只替换系统网络边界（<see cref="INetworkProbe"/>）与文件对话框/剪贴板，
/// 编排器、适配器、脱敏与状态聚合仍使用生产实现。
/// </remarks>
internal sealed class NetworkDiagnosticsHarness : IDisposable
{
    public NetworkDiagnosticsHarness(TimeProvider? timeProvider = null)
    {
        Probe = new FakeNetworkProbe();
        Adapter = new NetworkDiagnosticAdapter(Probe);
        Runner = new DiagnosticTaskRunner(new SecretTextRedactor(), timeProvider);
        Exporter = new FakeNetworkResultExporter();

        ViewModel = new NetworkDiagnosticsViewModel(
            Adapter,
            Runner,
            Exporter,
            timeProvider);
    }

    public FakeNetworkProbe Probe { get; }

    public NetworkDiagnosticAdapter Adapter { get; }

    public DiagnosticTaskRunner Runner { get; }

    public FakeNetworkResultExporter Exporter { get; }

    public NetworkDiagnosticsViewModel ViewModel { get; }

    public void Dispose()
    {
    }
}

/// <summary>可编排的假网络探测。</summary>
internal sealed class FakeNetworkProbe : INetworkProbe
{
    public Func<string, TimeSpan, CancellationToken, Task<PingObservation>>? PingBehavior { get; set; }

    public Func<string, int, TimeSpan, CancellationToken, Task<TcpObservation>>? TcpBehavior { get; set; }

    public Func<string, CancellationToken, Task<DnsObservation>>? DnsBehavior { get; set; }

    public Func<CancellationToken, Task<LocalNetworkSnapshot>>? LocalBehavior { get; set; }

    public Func<string, int, TimeSpan, CancellationToken, Task<TracerouteObservation>>? TracerouteBehavior { get; set; }

    public int PingCallCount { get; private set; }

    public int TcpCallCount { get; private set; }

    public int DnsCallCount { get; private set; }

    public int LocalCallCount { get; private set; }

    public int TracerouteCallCount { get; private set; }

    public Task<PingObservation> PingAsync(
        string target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        PingCallCount++;

        return PingBehavior?.Invoke(target, timeout, cancellationToken)
            ?? Task.FromResult(new PingObservation(
                NetworkObservationStatus.Passed,
                "收到 ICMP 回应。",
                [new NetworkFact("目标", target)]));
    }

    public Task<TcpObservation> TcpAsync(
        string target,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TcpCallCount++;

        return TcpBehavior?.Invoke(target, port, timeout, cancellationToken)
            ?? Task.FromResult(new TcpObservation(
                NetworkObservationStatus.Passed,
                $"TCP {target}:{port} 连接成功。",
                [new NetworkFact("目标", $"{target}:{port}")]));
    }

    public Task<DnsObservation> DnsAsync(
        string target,
        CancellationToken cancellationToken)
    {
        DnsCallCount++;

        return DnsBehavior?.Invoke(target, cancellationToken)
            ?? Task.FromResult(new DnsObservation(
                NetworkObservationStatus.Passed,
                "DNS 解析成功。",
                [new NetworkFact("名称", target)]));
    }

    public Task<LocalNetworkSnapshot> LocalAsync(CancellationToken cancellationToken)
    {
        LocalCallCount++;

        return LocalBehavior?.Invoke(cancellationToken)
            ?? Task.FromResult(new LocalNetworkSnapshot(
                [new NetworkFact("网卡 Ethernet", "192.0.2.10")],
                "Ethernet 192.0.2.10"));
    }

    public Task<TracerouteObservation> TracerouteAsync(
        string target,
        int maxHops,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TracerouteCallCount++;

        return TracerouteBehavior?.Invoke(target, maxHops, timeout, cancellationToken)
            ?? Task.FromResult(new TracerouteObservation(
                NetworkObservationStatus.Passed,
                "路径探测完成。",
                [new NetworkFact("第 1 跳", "192.0.2.1")]));
    }
}

/// <summary>记录复制/导出内容，不触碰剪贴板与文件对话框。</summary>
internal sealed class FakeNetworkResultExporter : INetworkResultExporter
{
    public string? CopiedText { get; private set; }

    public string? ExportedFileName { get; private set; }

    public string? ExportedContent { get; private set; }

    public Func<string, string, CancellationToken, Task<string?>>? ExportBehavior { get; set; }

    public void Copy(string text) => CopiedText = text;

    public Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        ExportedFileName = suggestedFileName;
        ExportedContent = content;

        return ExportBehavior?.Invoke(suggestedFileName, content, cancellationToken)
            ?? Task.FromResult<string?>(
                Path.Combine(Path.GetTempPath(), suggestedFileName));
    }
}
