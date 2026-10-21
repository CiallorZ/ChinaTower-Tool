using System.Globalization;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Adapters.Video;

/// <summary>RTSP 分阶段诊断适配器（LF-009）。</summary>
public sealed class RtspDiagnosticAdapter : IDiagnosticAdapter
{
    /// <summary>适配器稳定标识。</summary>
    public const string AdapterId = "video.rtsp";

    private readonly IRtspProbe _probe;

    /// <summary>创建 RTSP 适配器。</summary>
    public RtspDiagnosticAdapter(IRtspProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    /// <inheritdoc />
    public string Id => AdapterId;

    /// <inheritdoc />
    public string DisplayName => "RTSP 信令诊断";

    /// <inheritdoc />
    public Version ApiVersion => new(1, 0);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Capabilities =>
        ["video.rtsp", "video.rtsp.describe", "video.sdp"];

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticStepPlan> Plan(DiagnosticRequest request)
    {
        var timeout = ParseTimeout(request);

        return
        [
            new DiagnosticStepPlan(
                "rtsp",
                "RTSP 分阶段信令诊断",
                Timeout: timeout,
                IsOptional: false)
        ];
    }

    /// <inheritdoc />
    public async Task<DiagnosticStepResult> ExecuteStepAsync(
        DiagnosticStepContext context,
        IProgress<DiagnosticProgress> progress,
        CancellationToken cancellationToken)
    {
        var rawUrl = context.GetParameter(RtspParameterNames.Url);

        if (!RtspUrlParser.TryParse(rawUrl, out var endpoint, out var error))
        {
            return DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                error ?? "RTSP URL 无效。",
                context.StartedAt,
                TimeSpan.Zero,
                "请输入 rtsp://host:554/path 形式的地址。");
        }

        progress.Report(new DiagnosticProgress(
            context.RunId,
            context.StepId,
            DiagnosticStepPhase.Running,
            $"正在诊断 {endpoint.Redacted}…",
            DateTimeOffset.Now,
            DateTimeOffset.Now - context.StartedAt));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timeout = ParseTimeout(context);
        var observation = await _probe
            .ProbeAsync(endpoint, timeout, cancellationToken)
            .ConfigureAwait(false);
        watch.Stop();

        var facts = new List<DiagnosticFact>
        {
            new("目标", endpoint.Redacted)
        };

        foreach (var stage in observation.Stages)
        {
            facts.Add(new DiagnosticFact(
                stage.StageName,
                stage.Summary,
                ToDiagnosticStatus(stage.Status)));

            foreach (var fact in stage.Facts)
            {
                facts.Add(new DiagnosticFact(fact.Label, fact.Value));
            }
        }

        var summary = BuildSummary(observation);
        var suggestion = observation.Suggestion;

        return observation.Status switch
        {
            RtspObservationStatus.Passed => DiagnosticStepResults.Passed(
                context.StepId,
                context.Name,
                summary,
                context.StartedAt,
                watch.Elapsed,
                facts: facts,
                rawOutput: observation.RawOutput),

            RtspObservationStatus.Failed => DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                summary,
                context.StartedAt,
                watch.Elapsed,
                suggestion,
                facts: facts,
                rawOutput: observation.RawOutput),

            RtspObservationStatus.TimedOut => DiagnosticStepResults.TimedOut(
                context.StepId,
                context.Name,
                timeout,
                context.StartedAt,
                watch.Elapsed,
                suggestion),

            _ => DiagnosticStepResults.Unknown(
                context.StepId,
                context.Name,
                summary,
                context.StartedAt,
                watch.Elapsed,
                suggestion,
                facts,
                observation.RawOutput)
        };
    }

    private static DiagnosticStatus ToDiagnosticStatus(RtspObservationStatus status) => status switch
    {
        RtspObservationStatus.Passed => DiagnosticStatus.Passed,
        RtspObservationStatus.Failed => DiagnosticStatus.Failed,
        RtspObservationStatus.TimedOut => DiagnosticStatus.TimedOut,
        RtspObservationStatus.Skipped => DiagnosticStatus.Skipped,
        _ => DiagnosticStatus.Unknown
    };

    private static string BuildSummary(RtspObservation observation)
    {
        var failed = observation.Stages.FirstOrDefault(static s =>
            s.Status is RtspObservationStatus.Failed or RtspObservationStatus.TimedOut);

        if (failed is not null)
        {
            return $"{failed.StageName}：{failed.Summary}";
        }

        return observation.Stages.Any(static s => s.Status == RtspObservationStatus.Unknown)
            ? "RTSP 信令与 SDP 已完成；媒体接收/解码未执行，当前不能宣称已播放。"
            : $"RTSP 信令诊断完成：{observation.Summary}";
    }

    private static TimeSpan ParseTimeout(DiagnosticRequest request) =>
        ParseTimeout(request.GetParameter(RtspParameterNames.TimeoutSeconds));

    private static TimeSpan ParseTimeout(DiagnosticStepContext context) =>
        ParseTimeout(context.GetParameter(RtspParameterNames.TimeoutSeconds));

    private static TimeSpan ParseTimeout(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
        && seconds is >= 1 and <= 60
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(8);
}
