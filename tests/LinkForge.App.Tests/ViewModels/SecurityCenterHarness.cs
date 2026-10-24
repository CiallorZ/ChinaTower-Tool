using System.IO;
using LinkForge.Adapters.Security;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.Application.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>安全中心测试夹具；只替换系统网络探测与剪贴板/文件对话框边界。</summary>
internal sealed class SecurityCenterHarness : IDisposable
{
    public SecurityCenterHarness(TimeProvider? timeProvider = null)
    {
        Probe = new FakeSecurityProbe();
        Adapter = new SecurityDiagnosticAdapter(Probe);
        Runner = new DiagnosticTaskRunner(new SecretTextRedactor(), timeProvider);
        Exporter = new FakeSecurityResultExporter();
        ViewModel = new SecurityCenterViewModel(Adapter, Runner, Exporter, timeProvider);
    }

    public FakeSecurityProbe Probe { get; }

    public SecurityDiagnosticAdapter Adapter { get; }

    public DiagnosticTaskRunner Runner { get; }

    public FakeSecurityResultExporter Exporter { get; }

    public SecurityCenterViewModel ViewModel { get; }

    public void Dispose()
    {
    }
}

/// <summary>可编排的安全基线探测边界。</summary>
internal sealed class FakeSecurityProbe : ISecurityProbe
{
    public Func<SecurityBaselineRequest, CancellationToken, Task<SecurityBaselineObservation>>? Behavior { get; set; }

    public int CallCount { get; private set; }

    public Task<SecurityBaselineObservation> RunAsync(
        SecurityBaselineRequest request,
        IProgress<SecurityProbeProgress>? progress,
        CancellationToken cancellationToken)
    {
        CallCount++;

        return Behavior?.Invoke(request, cancellationToken)
            ?? Task.FromResult(DefaultSuccess(request));
    }

    public static SecurityBaselineObservation DefaultSuccess(SecurityBaselineRequest request) =>
        new(
            SecurityObservationStatus.Passed,
            "TCP 暴露面与 TLS 证书基线检查完成。",
            [
                new SecurityFact("目标", request.Target),
                new SecurityFact("实际解析地址", "127.0.0.1"),
                new SecurityFact("范围说明", "仅检查显式列出的端口。")
            ],
            [
                new SecurityPortResult(
                    443,
                    SecurityPortState.Open,
                    "TCP 443 开放。",
                    TimeSpan.FromMilliseconds(12),
                    new SecurityTlsResult(
                        443,
                        SecurityTlsState.Valid,
                        "TLS 443 证书通过默认校验。",
                        [
                            new SecurityFact("TLS 状态", "证书校验通过", SecurityFactStatus.Passed),
                            new SecurityFact("证书主体", "CN=example.test")
                        ]))
            ],
            "TCP 443: 开放\nTLS 443: 证书通过默认校验",
            "端口开放不等于漏洞；证书告警需要进一步核验。");
}

/// <summary>记录复制/导出内容，不触碰真实剪贴板和文件对话框。</summary>
internal sealed class FakeSecurityResultExporter : ISecurityResultExporter
{
    public string? CopiedText { get; private set; }

    public string? ExportedFileName { get; private set; }

    public string? ExportedContent { get; private set; }

    public Func<string, string, CancellationToken, Task<string?>>? ExportBehavior { get; set; }

    public Action<string>? CopyBehavior { get; set; }

    public void Copy(string text)
    {
        CopiedText = text;
        CopyBehavior?.Invoke(text);
    }

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
