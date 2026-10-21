using LinkForge.Domain.Diagnostics;
using LinkForge.Reporting.Reports;

namespace LinkForge.Reporting.Tests;

/// <summary>报告测试用的数据构造器。</summary>
internal static class TestData
{
    internal static readonly DateTimeOffset Start = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    internal static DiagnosticRunResult HealthyRun() =>
        new()
        {
            RunId = "run-0001",
            AdapterId = "network.diagnostics",
            DisplayName = "网络基础诊断",
            TargetLabel = "10.0.0.8:443",
            StartedAt = Start,
            EndedAt = Start.AddSeconds(3.5),
            Steps =
            [
                new DiagnosticStepResult(
                    "dns",
                    "DNS 解析",
                    DiagnosticStatus.Passed,
                    Start,
                    TimeSpan.FromMilliseconds(120),
                    "解析到 10.0.0.8。",
                    Facts: [new DiagnosticFact("解析地址", "10.0.0.8")],
                    RawOutput: "Server: 10.0.0.1\nAddress: 10.0.0.8"),
                new DiagnosticStepResult(
                    "tcp",
                    "TCP 连接",
                    DiagnosticStatus.Passed,
                    Start.AddMilliseconds(200),
                    TimeSpan.FromMilliseconds(80),
                    "443 端口连接成功。",
                    Evidence:
                    [
                        new EvidenceArtifact
                        {
                            FileName = "tcp.txt",
                            RelativePath = "evidence/tcp.txt",
                            Sha256 = "0123456789abcdef0123456789abcdef",
                            SizeBytes = 2048,
                            Description = "TCP 握手耗时采样"
                        }
                    ])
            ]
        };

    internal static DiagnosticRunResult MixedRun() =>
        new()
        {
            RunId = "run-0002",
            AdapterId = "video.rtsp",
            DisplayName = "RTSP 分阶段诊断",
            TargetLabel = "camera-01:554",
            StartedAt = Start,
            EndedAt = Start.AddSeconds(12),
            Steps =
            [
                new DiagnosticStepResult(
                    "tcp",
                    "TCP 连接",
                    DiagnosticStatus.Passed,
                    Start,
                    TimeSpan.FromMilliseconds(60),
                    "TCP 连接已建立。"),
                new DiagnosticStepResult(
                    "auth",
                    "RTSP 认证",
                    DiagnosticStatus.Failed,
                    Start.AddMilliseconds(100),
                    TimeSpan.FromMilliseconds(400),
                    "服务端返回 401，未提供凭据。",
                    "在 URL 中补充用户名与口令后重试；确认账号具备该通道权限。"),
                DiagnosticStepResults.Skipped("media", "RTP 接收", "本阶段未实现媒体接收。"),
                new DiagnosticStepResult(
                    "decode",
                    "首帧解码",
                    DiagnosticStatus.Unknown,
                    Start.AddSeconds(1),
                    TimeSpan.FromMilliseconds(50),
                    "缺少解码器，无法判定。",
                    "安装 FFmpeg 后重新运行。")
            ]
        };

    internal static ReportDocument Document(
        DiagnosticRunResult? run = null,
        string? projectName = "视联网接入验收",
        string? target = null)
    {
        var currentRun = run ?? HealthyRun();

        return new ReportDocument
        {
            Metadata = new ReportMetadata
            {
                ProductVersion = "1.0.0-test",
                ProjectName = projectName,
                GeneratedAt = Start.AddMinutes(5),
                EnvironmentFacts =
                [
                    new DiagnosticFact("运行时", ".NET 10.0.13"),
                    new DiagnosticFact("操作系统", "Windows 11 (build 26100)"),
                    new DiagnosticFact("进程架构", "X64")
                ]
            },
            Run = target is null ? currentRun : currentRun with { TargetLabel = target }
        };
    }
}
