using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Adapters.Security.Tests;

public class SecurityDiagnosticAdapterTests
{
    [Fact]
    public async Task Passed_observation_should_map_to_passed_with_facts_and_raw_output()
    {
        var fake = new FakeSecurityProbe
        {
            Observation = new SecurityBaselineObservation(
                SecurityObservationStatus.Passed,
                "TCP 443 开放。",
                [new SecurityFact("实际解析地址", "192.0.2.10")],
                [],
                "raw observation")
        };
        var adapter = new SecurityDiagnosticAdapter(fake);

        var result = await adapter.ExecuteStepAsync(
            Context(target: "example.test", ports: "443", tlsPorts: "443"),
            new RecordingProgress(),
            CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Passed);
        result.FactItems.ShouldContain(fact => fact.Label == "实际解析地址");
        result.RawOutput.ShouldBe("raw observation");
    }

    [Theory]
    [InlineData(SecurityObservationStatus.Warning, DiagnosticStatus.Warning)]
    [InlineData(SecurityObservationStatus.Failed, DiagnosticStatus.Failed)]
    [InlineData(SecurityObservationStatus.Unknown, DiagnosticStatus.Unknown)]
    [InlineData(SecurityObservationStatus.TimedOut, DiagnosticStatus.TimedOut)]
    public async Task Observation_status_should_map_without_upgrading_unknown_to_success(
        SecurityObservationStatus source,
        DiagnosticStatus expected)
    {
        var fake = new FakeSecurityProbe
        {
            Observation = new SecurityBaselineObservation(
                source,
                "test summary",
                [],
                [],
                Suggestion: "test suggestion")
        };
        var adapter = new SecurityDiagnosticAdapter(fake);

        var result = await adapter.ExecuteStepAsync(
            Context(),
            new RecordingProgress(),
            CancellationToken.None);

        result.Status.ShouldBe(expected);
    }

    [Fact]
    public async Task Tls_ports_outside_the_tcp_scope_should_be_rejected_before_probe()
    {
        var fake = new FakeSecurityProbe();
        var adapter = new SecurityDiagnosticAdapter(fake);

        var result = await adapter.ExecuteStepAsync(
            Context(ports: "443", tlsPorts: "8443"),
            new RecordingProgress(),
            CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Failed);
        result.Summary.ShouldContain("TLS 端口必须包含");
        fake.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task More_than_eight_tls_ports_should_be_rejected_before_probe()
    {
        var fake = new FakeSecurityProbe();
        var adapter = new SecurityDiagnosticAdapter(fake);

        var result = await adapter.ExecuteStepAsync(
            Context(ports: "1-9", tlsPorts: "1-9"),
            new RecordingProgress(),
            CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Failed);
        result.Summary.ShouldContain("最多包含 8");
        fake.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Cancellation_should_propagate_instead_of_becoming_failed()
    {
        var fake = new FakeSecurityProbe
        {
            Behavior = async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("不应到达这里。");
            }
        };
        var adapter = new SecurityDiagnosticAdapter(fake);
        using var cts = new CancellationTokenSource();

        var run = adapter.ExecuteStepAsync(
            Context(),
            new RecordingProgress(),
            cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Progress_should_use_the_step_identity()
    {
        var fake = new FakeSecurityProbe
        {
            Behavior = (_, progress, _) =>
            {
                progress?.Report(new SecurityProbeProgress(1, 2, "已完成 1/2 个端口检查。"));
                return Task.FromResult(new SecurityBaselineObservation(
                    SecurityObservationStatus.Passed,
                    "done",
                    [],
                    []));
            }
        };
        var adapter = new SecurityDiagnosticAdapter(fake);
        var progress = new RecordingProgress();

        await adapter.ExecuteStepAsync(
            Context(ports: "80,443", tlsPorts: ""),
            progress,
            CancellationToken.None);

        progress.Events.ShouldContain(item => item.StepId == "security-step");
        progress.Events.ShouldContain(item => item.Message.Contains("1/2", StringComparison.Ordinal));
    }

    private static DiagnosticStepContext Context(
        string target = "192.0.2.10",
        string ports = "443",
        string tlsPorts = "443") =>
        new(
            "security-run",
            "security-step",
            "安全基线",
            target,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [SecurityParameterNames.Target] = target,
                [SecurityParameterNames.Ports] = ports,
                [SecurityParameterNames.TlsPorts] = tlsPorts,
                [SecurityParameterNames.TimeoutSeconds] = "2",
                [SecurityParameterNames.Concurrency] = "2",
                [SecurityParameterNames.DelayMilliseconds] = "0"
            },
            DateTimeOffset.Now);

    private sealed class FakeSecurityProbe : ISecurityProbe
    {
        public SecurityBaselineObservation Observation { get; init; } =
            new(SecurityObservationStatus.Passed, "passed", [], []);

        public Func<SecurityBaselineRequest, IProgress<SecurityProbeProgress>?, CancellationToken, Task<SecurityBaselineObservation>>? Behavior
        {
            get;
            init;
        }

        public int CallCount { get; private set; }

        public Task<SecurityBaselineObservation> RunAsync(
            SecurityBaselineRequest request,
            IProgress<SecurityProbeProgress>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Behavior?.Invoke(request, progress, cancellationToken)
                   ?? Task.FromResult(Observation);
        }
    }

    private sealed class RecordingProgress : IProgress<DiagnosticProgress>
    {
        public List<DiagnosticProgress> Events { get; } = [];

        public void Report(DiagnosticProgress value) => Events.Add(value);
    }
}
