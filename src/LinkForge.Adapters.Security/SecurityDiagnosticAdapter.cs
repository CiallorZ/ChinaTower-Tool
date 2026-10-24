using System.Globalization;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Adapters.Security;

/// <summary>安全基线参数的稳定键名。</summary>
public static class SecurityParameterNames
{
    /// <summary>目标主机。</summary>
    public const string Target = "target";

    /// <summary>TCP 端口列表。</summary>
    public const string Ports = "ports";

    /// <summary>TLS 端口列表；空白表示不做 TLS 握手。</summary>
    public const string TlsPorts = "tlsPorts";

    /// <summary>单个连接/握手超时秒数。</summary>
    public const string TimeoutSeconds = "timeoutSeconds";

    /// <summary>并发上限。</summary>
    public const string Concurrency = "concurrency";

    /// <summary>相邻检查启动延迟毫秒。</summary>
    public const string DelayMilliseconds = "delayMilliseconds";
}

/// <summary>授权范围内的安全基线与 TLS 检查适配器（LF-020）。</summary>
public sealed class SecurityDiagnosticAdapter : IDiagnosticAdapter
{
    /// <summary>稳定适配器标识。</summary>
    public const string AdapterId = "security.baseline";

    private readonly ISecurityProbe _probe;

    /// <summary>创建安全基线适配器。</summary>
    public SecurityDiagnosticAdapter(ISecurityProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    /// <inheritdoc />
    public string Id => AdapterId;

    /// <inheritdoc />
    public string DisplayName => "安全基线与 TLS 检查";

    /// <inheritdoc />
    public Version ApiVersion => new(1, 0);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Capabilities =>
        ["security.tcp-baseline", "security.tls-certificate"];

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticStepPlan> Plan(DiagnosticRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return
        [
            new DiagnosticStepPlan(
                "baseline",
                "TCP 暴露面与 TLS 证书基线",
                Timeout: TimeSpan.FromMinutes(2))
        ];
    }

    /// <inheritdoc />
    public async Task<DiagnosticStepResult> ExecuteStepAsync(
        DiagnosticStepContext context,
        IProgress<DiagnosticProgress> progress,
        CancellationToken cancellationToken)
    {
        var target = context.GetParameter(SecurityParameterNames.Target)?.Trim() ?? string.Empty;
        var timeout = ParseTimeout(context);
        var concurrency = ParseInt(
            context.GetParameter(SecurityParameterNames.Concurrency),
            1,
            4,
            2);
        var delayMs = ParseInt(
            context.GetParameter(SecurityParameterNames.DelayMilliseconds),
            0,
            2_000,
            200);

        if (!SecurityPortParser.TryParse(
                context.GetParameter(SecurityParameterNames.Ports),
                32,
                out var ports,
                out var portError))
        {
            return DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                portError ?? "端口范围无效。",
                context.StartedAt,
                TimeSpan.Zero,
                "请把检查范围限制为一个主机和最多 32 个显式端口。");
        }

        var tlsText = context.GetParameter(SecurityParameterNames.TlsPorts);
        IReadOnlyList<int> tlsPorts = [];

        if (!string.IsNullOrWhiteSpace(tlsText)
            && !SecurityPortParser.TryParse(tlsText, 8, out tlsPorts, out var tlsError))
        {
            return DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                tlsError ?? "TLS 端口范围无效。",
                context.StartedAt,
                TimeSpan.Zero,
                "TLS 端口必须包含在 TCP 端口范围内，且最多 8 个。");
        }

        if (tlsPorts.Any(port => !ports.Contains(port)))
        {
            return DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                "TLS 端口必须包含在本次 TCP 端口范围内。",
                context.StartedAt,
                TimeSpan.Zero,
                "不要执行范围外探测；请先把 TLS 端口加入 TCP 端口列表。");
        }

        var startedAt = context.StartedAt;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var request = new SecurityBaselineRequest(
            target,
            ports,
            tlsPorts,
            timeout,
            concurrency,
            TimeSpan.FromMilliseconds(delayMs));

        var observation = await _probe
            .RunAsync(
                request,
                new ProbeProgressRelay(progress, context, startedAt),
                cancellationToken)
            .ConfigureAwait(false);

        watch.Stop();
        var facts = observation.Facts
            .Select(static fact => new DiagnosticFact(
                fact.Label,
                fact.Value,
                ToDiagnosticStatus(fact.Status)))
            .ToArray();

        return observation.Status switch
        {
            SecurityObservationStatus.Passed => DiagnosticStepResults.Passed(
                context.StepId,
                context.Name,
                observation.Summary,
                startedAt,
                watch.Elapsed,
                facts: facts,
                rawOutput: observation.RawOutput),

            SecurityObservationStatus.Warning => DiagnosticStepResults.Warning(
                context.StepId,
                context.Name,
                observation.Summary,
                startedAt,
                watch.Elapsed,
                observation.Suggestion,
                facts: facts,
                rawOutput: observation.RawOutput),

            SecurityObservationStatus.Failed => DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                observation.Summary,
                startedAt,
                watch.Elapsed,
                observation.Suggestion,
                facts: facts,
                rawOutput: observation.RawOutput),

            SecurityObservationStatus.TimedOut => DiagnosticStepResults.TimedOut(
                context.StepId,
                context.Name,
                timeout,
                startedAt,
                watch.Elapsed,
                observation.Suggestion) with
            {
                Facts = facts,
                RawOutput = observation.RawOutput
            },

            _ => DiagnosticStepResults.Unknown(
                context.StepId,
                context.Name,
                observation.Summary,
                startedAt,
                watch.Elapsed,
                observation.Suggestion,
                facts,
                observation.RawOutput)
        };
    }

    private static DiagnosticStatus? ToDiagnosticStatus(SecurityFactStatus status) => status switch
    {
        SecurityFactStatus.Passed => DiagnosticStatus.Passed,
        SecurityFactStatus.Warning => DiagnosticStatus.Warning,
        SecurityFactStatus.Failed => DiagnosticStatus.Failed,
        SecurityFactStatus.Unknown => DiagnosticStatus.Unknown,
        _ => null
    };

    private static TimeSpan ParseTimeout(DiagnosticStepContext context)
    {
        var seconds = ParseInt(
            context.GetParameter(SecurityParameterNames.TimeoutSeconds),
            1,
            60,
            5);

        return TimeSpan.FromSeconds(seconds);
    }

    private static int ParseInt(string? raw, int minimum, int maximum, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= minimum
        && value <= maximum
            ? value
            : fallback;

    private sealed class ProbeProgressRelay(
        IProgress<DiagnosticProgress> progress,
        DiagnosticStepContext context,
        DateTimeOffset startedAt) : IProgress<SecurityProbeProgress>
    {
        public void Report(SecurityProbeProgress value)
        {
            progress.Report(new DiagnosticProgress(
                context.RunId,
                context.StepId,
                DiagnosticStepPhase.Running,
                value.Message,
                DateTimeOffset.Now,
                DateTimeOffset.Now - startedAt));
        }
    }
}
