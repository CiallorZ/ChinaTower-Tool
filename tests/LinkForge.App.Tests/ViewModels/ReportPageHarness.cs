using System.IO;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.Application.Diagnostics;
using LinkForge.Domain.Diagnostics;
using LinkForge.Reporting.Reports;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>报告页测试夹具；替换历史、对话框与文件系统边界。</summary>
internal sealed class ReportPageHarness : IDisposable
{
    public ReportPageHarness(TimeProvider? timeProvider = null)
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "linkforge-report-page-tests",
            Guid.NewGuid().ToString("N"));

        System.IO.Directory.CreateDirectory(_directory);

        History = new FakeRunHistoryStore();
        Generator = new ReportGenerator(new SecretTextRedactor(), timeProvider);
        Exporter = new FakeReportFileExporter(Path.Combine(_directory, "report.html"));
        Environment = new AppEnvironment(Path.Combine(_directory, "settings.json"));
        ViewModel = new ReportPageViewModel(
            History,
            Generator,
            Exporter,
            Environment,
            timeProvider);
    }

    private readonly string _directory;

    public FakeRunHistoryStore History { get; }

    public ReportGenerator Generator { get; }

    public FakeReportFileExporter Exporter { get; }

    public AppEnvironment Environment { get; }

    public ReportPageViewModel ViewModel { get; }

    public string RootDirectory => _directory;

    public static DiagnosticRunResult SampleRun(string target = "10.0.0.8:443") =>
        new()
        {
            RunId = "run-" + Guid.NewGuid().ToString("N")[..8],
            AdapterId = "network.diagnostics",
            DisplayName = "网络基础诊断",
            TargetLabel = target,
            StartedAt = new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 10, 21, 6, 0, 3, TimeSpan.Zero),
            Steps =
            [
                new DiagnosticStepResult(
                    "tcp",
                    "TCP 连接",
                    DiagnosticStatus.Passed,
                    new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero),
                    TimeSpan.FromMilliseconds(90),
                    "443 端口连接成功。"),
                DiagnosticStepResults.Skipped("media", "RTP 接收", "本阶段未实现媒体接收。")
            ]
        };

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(_directory))
            {
                System.IO.Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
    }
}

/// <summary>内存历史存储。</summary>
internal sealed class FakeRunHistoryStore : IRunHistoryStore
{
    public List<DiagnosticRunResult> Entries { get; } = [];

    public Exception? LoadException { get; set; }

    public Task<IReadOnlyList<DiagnosticRunResult>> LoadAsync(
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (LoadException is not null)
        {
            return Task.FromException<IReadOnlyList<DiagnosticRunResult>>(LoadException);
        }

        IReadOnlyList<DiagnosticRunResult> result = limit is { } max
            ? [.. Entries.OrderByDescending(static e => e.StartedAt).Take(max)]
            : [.. Entries.OrderByDescending(static e => e.StartedAt)];

        return Task.FromResult(result);
    }

    public Task AppendAsync(DiagnosticRunResult result, CancellationToken cancellationToken = default)
    {
        Entries.Add(result);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Entries.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>可编排的保存对话框。</summary>
internal sealed class FakeReportFileExporter : IReportFileExporter
{
    public FakeReportFileExporter(string? destination)
    {
        Destination = destination;
    }

    public string? Destination { get; set; }

    public string? RevealedPath { get; private set; }

    public Exception? RevealException { get; set; }

    public int ChooseCount { get; private set; }

    public string? ChooseDestination(string suggestedFileName)
    {
        ChooseCount++;
        return Destination;
    }

    public void RevealInExplorer(string path)
    {
        if (RevealException is not null)
        {
            throw RevealException;
        }

        RevealedPath = path;
    }
}
