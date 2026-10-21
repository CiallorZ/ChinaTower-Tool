using System.IO;
using LinkForge.Adapters.Network;
using LinkForge.App.Presentation;
using LinkForge.Application.Diagnostics;
using LinkForge.Application.Logging;
using LinkForge.Application.Storage;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Diagnostics;
using LinkForge.Domain.Projects;
using LinkForge.Persistence;
using LinkForge.Plugin.Abstractions;
using LinkForge.Reporting.Reports;

namespace LinkForge.App.Tests.Services;

/// <summary>
/// Cross-module workflow used by LF-013:
/// project -> asset -> network diagnosis -> structured log -> HTML report.
/// </summary>
/// <remarks>
/// Only the external network boundary is replaced. SQLite, the diagnostic
/// orchestrator, redaction, JSONL logging and report generation are production
/// implementations. The test does not access the public network.
/// </remarks>
public sealed class OperationsWorkflowEndToEndTests
{
    private const string Secret = "WorkflowSecret123";

    [Fact]
    public async Task Project_asset_diagnostic_log_and_report_should_complete_on_real_components()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "linkforge-workflow-" + Guid.NewGuid().ToString("N"));

        try
        {
            var databasePath = Path.Combine(directory, "linkforge.db");
            var factory = new SqliteContextFactory(databasePath);
            var initialization = await factory.InitializeAsync();

            initialization.Outcome.ShouldBe(DatabaseInitializationOutcome.Created);
            File.Exists(databasePath).ShouldBeTrue();

            var projects = new SqliteProjectStore(factory);
            var assets = new SqliteAssetStore(factory);

            var project = await projects.UpsertAsync(new Project
            {
                Id = "p-workflow",
                Name = "端到端验收项目",
                CreatedAt = DateTimeOffset.Now
            });

            await assets.UpsertAsync(new Asset
            {
                Id = "a-workflow",
                ProjectId = project.Id,
                Name = "东门摄像机",
                Host = "192.0.2.10",
                Port = 554,
                Kind = AssetKind.Camera,
                CredentialReference = "asset.east.password"
            });

            var asset = await assets.FindAsync("a-workflow");
            asset.ShouldNotBeNull();
            asset!.CredentialReference.ShouldBe("asset.east.password");

            var probe = new WorkflowNetworkProbe
            {
                TcpBehavior = (_, port, _, _) => Task.FromResult(new TcpObservation(
                    NetworkObservationStatus.Passed,
                    $"TCP 端口 {port} 连接成功；password={Secret}",
                    [new NetworkFact("目标", asset.TargetText)]))
            };

            var runner = new DiagnosticTaskRunner(new SecretTextRedactor());
            var request = new DiagnosticRequest(
                "run-workflow",
                asset.TargetText,
                new Dictionary<string, string?>
                {
                    [NetworkParameterNames.Tool] = NetworkTool.Tcp.ToString(),
                    [NetworkParameterNames.Target] = asset.Host,
                    [NetworkParameterNames.Port] = asset.Port?.ToString(),
                    [NetworkParameterNames.TimeoutSeconds] = "5"
                });

            var run = await runner.RunAsync(
                new NetworkDiagnosticAdapter(probe),
                request,
                [],
                DiagnosticRunOptions.Default);

            var step = run.Steps.ShouldHaveSingleItem();
            step.Status.ShouldBe(
                DiagnosticStatus.Passed,
                $"summary='{step.Summary}'; suggestion='{step.NextStepSuggestion}'");
            run.Steps[0].Summary.ShouldNotContain(Secret);
            run.State.ShouldBe(TaskRunState.Completed);

            var logDirectory = Path.Combine(directory, "logs");
            var logStore = new JsonLineStructuredLogStore(
                logDirectory,
                new SecretTextRedactor());
            var logEntry = new LogEntry(
                DateTimeOffset.Now,
                LfLogLevel.Information,
                "Workflow",
                run.RunId,
                run.TargetLabel,
                $"诊断完成：{run.Steps[0].Summary}",
                "workflow.completed");

            await logStore.AppendAsync(logEntry.ToStructuredRecord());
            var logs = await logStore.ReadRecentAsync(10);

            logs.ShouldHaveSingleItem().EventId.ShouldBe("workflow.completed");
            var logText = await File.ReadAllTextAsync(logStore.CurrentFilePath);
            logText.ShouldNotContain(Secret);

            var reportPath = Path.Combine(directory, "report.html");
            var report = await new ReportGenerator(new SecretTextRedactor()).GenerateAsync(
                new ReportDocument
                {
                    Metadata = new ReportMetadata
                    {
                        ProductVersion = "0.5.0-preview.1",
                        ProjectName = project.Name,
                        GeneratedAt = DateTimeOffset.Now,
                        EnvironmentFacts =
                        [
                            new DiagnosticFact("运行时", ".NET 10"),
                            new DiagnosticFact("操作系统", "Windows")
                        ]
                    },
                    Run = run
                },
                reportPath);

            report.Succeeded.ShouldBeTrue();
            File.Exists(reportPath).ShouldBeTrue();

            var html = await File.ReadAllTextAsync(reportPath);
            html.ShouldContain(project.Name);
            html.ShouldContain(asset.TargetText);
            html.ShouldNotContain(Secret);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class WorkflowNetworkProbe : INetworkProbe
    {
        public Func<string, int, TimeSpan, CancellationToken, Task<TcpObservation>>? TcpBehavior { get; init; }

        public Task<PingObservation> PingAsync(
            string target,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("This workflow only exercises TCP.");

        public Task<TcpObservation> TcpAsync(
            string target,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            TcpBehavior?.Invoke(target, port, timeout, cancellationToken)
            ?? throw new InvalidOperationException("TCP behavior was not configured.");

        public Task<DnsObservation> DnsAsync(
            string target,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("This workflow only exercises TCP.");

        public Task<LocalNetworkSnapshot> LocalAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("This workflow only exercises TCP.");

        public Task<TracerouteObservation> TracerouteAsync(
            string target,
            int maxHops,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("This workflow only exercises TCP.");
    }
}
