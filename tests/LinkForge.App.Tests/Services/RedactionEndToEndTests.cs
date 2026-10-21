using System.IO;
using LinkForge.App.Presentation;
using LinkForge.Application.Logging;
using LinkForge.Domain.Diagnostics;
using LinkForge.Reporting.Reports;

namespace LinkForge.App.Tests.Services;

/// <summary>
/// UI、磁盘日志与报告三条出口的脱敏边界。
/// </summary>
/// <remarks>
/// 测试刻意从同一条原始敏感文本出发：
/// 如果某个出口漏用统一脱敏规则，就能在这里直接观察到明文。
/// </remarks>
public sealed class RedactionEndToEndTests : IDisposable
{
    private const string Secret = "CrossPathSecret123";
    private const string RawText =
        """{"outer":{"password":"CrossPathSecret123"},"url":"rtsp://admin:CrossPathSecret123@10.0.0.8/live"}""";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "linkforge-redaction-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Same_secret_should_not_survive_ui_log_or_disk_log()
    {
        var entry = new LogEntry(
            DateTimeOffset.Now,
            LfLogLevel.Information,
            "Network",
            "run-1",
            "rtsp://admin:CrossPathSecret123@10.0.0.8/live",
            RawText,
            "network.redaction");

        entry.Message.ShouldNotContain(Secret);
        entry.TargetId.ShouldNotBeNull().ShouldNotContain(Secret);

        var store = new JsonLineStructuredLogStore(
            _directory,
            new SecretTextRedactor());

        await store.AppendAsync(new StructuredLogRecord(
            DateTimeOffset.Now,
            StructuredLogLevel.Information,
            "Network",
            "run-1",
            "rtsp://admin:CrossPathSecret123@10.0.0.8/live",
            "network.redaction",
            RawText));

        var content = await File.ReadAllTextAsync(store.CurrentFilePath);
        content.ShouldNotContain(Secret);
        content.ShouldContain("***");
    }

    [Fact]
    public async Task Report_path_should_block_unredacted_content_instead_of_writing_it()
    {
        var started = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.FromHours(8));
        var run = new DiagnosticRunResult
        {
            RunId = "run-1",
            AdapterId = "network.diagnostics",
            DisplayName = "网络基础诊断",
            TargetLabel = "10.0.0.8",
            StartedAt = started,
            EndedAt = started.AddSeconds(1),
            Steps =
            [
                DiagnosticStepResults.Failed(
                    "connection",
                    "连接",
                    RawText,
                    started,
                    TimeSpan.FromSeconds(1))
            ]
        };

        var document = new ReportDocument
        {
            Metadata = new ReportMetadata
            {
                ProductVersion = "test",
                GeneratedAt = started
            },
            Run = run
        };

        var generator = new ReportGenerator(new SecretTextRedactor());
        var path = Path.Combine(_directory, "report.html");

        var result = await generator.GenerateAsync(document, path);

        result.Succeeded.ShouldBeFalse();
        result.FailureKind.ShouldBe(ReportFailureKind.SensitiveDataDetected);
        File.Exists(path).ShouldBeFalse();
        File.Exists(path + ".tmp").ShouldBeFalse();
        result.Message.ShouldNotContain(Secret);
    }
}
