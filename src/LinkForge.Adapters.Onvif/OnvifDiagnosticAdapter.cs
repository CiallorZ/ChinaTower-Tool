using System.Globalization;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Adapters.Onvif;

/// <summary>ONVIF 标准发现与媒体能力诊断适配器。</summary>
public sealed class OnvifDiagnosticAdapter : IDiagnosticAdapter
{
    /// <summary>适配器稳定标识。</summary>
    public const string AdapterId = "video.onvif";

    private readonly IOnvifProbe _probe;

    /// <summary>创建适配器。</summary>
    public OnvifDiagnosticAdapter(IOnvifProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    /// <inheritdoc />
    public string Id => AdapterId;

    /// <inheritdoc />
    public string DisplayName => "ONVIF 标准接入诊断";

    /// <inheritdoc />
    public Version ApiVersion => new(1, 0);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Capabilities =>
        ["video.onvif", "video.onvif.discovery", "video.onvif.media", "video.rtsp.uri"];

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticStepPlan> Plan(DiagnosticRequest request)
    {
        var timeout = ParseTimeout(request);

        return ParseTool(request.GetParameter(OnvifParameterNames.Tool)) == OnvifTool.Discover
            ?
            [
                new DiagnosticStepPlan("onvif.discovery", "WS-Discovery 设备发现", Timeout: timeout)
            ]
            :
            [
                new DiagnosticStepPlan("onvif.device", "设备信息", Timeout: timeout),
                new DiagnosticStepPlan(
                    "onvif.capabilities",
                    "服务能力",
                    DependsOnSteps: ["onvif.device"],
                    Timeout: timeout),
                new DiagnosticStepPlan(
                    "onvif.media",
                    "媒体 Profile 与 Stream URI",
                    DependsOnSteps: ["onvif.capabilities"],
                    Timeout: timeout)
            ];
    }

    /// <inheritdoc />
    public async Task<DiagnosticStepResult> ExecuteStepAsync(
        DiagnosticStepContext context,
        IProgress<DiagnosticProgress> progress,
        CancellationToken cancellationToken)
    {
        var tool = ParseTool(context.GetParameter(OnvifParameterNames.Tool));
        var timeout = ParseTimeout(context);

        if (tool == OnvifTool.Discover)
        {
            progress.Report(new DiagnosticProgress(
                context.RunId,
                context.StepId,
                DiagnosticStepPhase.Running,
                "正在通过 WS-Discovery 查找 ONVIF 设备…",
                DateTimeOffset.Now,
                DateTimeOffset.Now - context.StartedAt));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var discovery = await _probe.DiscoverAsync(timeout, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            return MapDiscovery(context, discovery, timeout, watch.Elapsed);
        }

        if (!OnvifEndpoint.TryParse(
                context.GetParameter(OnvifParameterNames.ServiceUrl),
                out var endpoint,
                out var endpointError))
        {
            return DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                endpointError ?? "ONVIF 设备服务 URL 无效。",
                context.StartedAt,
                TimeSpan.Zero,
                "请输入 http://host/onvif/device_service 形式的服务地址。");
        }

        var credentials = ReadCredentials(context);
        progress.Report(new DiagnosticProgress(
            context.RunId,
            context.StepId,
            DiagnosticStepPhase.Running,
            $"正在查询 {endpoint.DisplayUrl}：{context.Name}…",
            DateTimeOffset.Now,
            DateTimeOffset.Now - context.StartedAt));

        var operationWatch = System.Diagnostics.Stopwatch.StartNew();
        var observation = context.StepId switch
        {
            "onvif.device" => await _probe
                .GetDeviceInformationAsync(endpoint, credentials, timeout, cancellationToken)
                .ConfigureAwait(false),
            "onvif.capabilities" => await _probe
                .GetCapabilitiesAsync(endpoint, credentials, timeout, cancellationToken)
                .ConfigureAwait(false),
            "onvif.media" => await _probe
                .GetMediaProfilesAsync(
                    endpoint,
                    credentials,
                    context.GetParameter(OnvifParameterNames.ProfileToken),
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => new OnvifOperationObservation(
                OnvifObservationStatus.Unknown,
                "未识别的 ONVIF 步骤。",
                [],
                Suggestion: "请重新选择发现或检查操作。")
        };
        operationWatch.Stop();

        return MapOperation(context, endpoint, observation, timeout, operationWatch.Elapsed);
    }

    private static DiagnosticStepResult MapDiscovery(
        DiagnosticStepContext context,
        OnvifDiscoveryObservation observation,
        TimeSpan timeout,
        TimeSpan duration)
    {
        var facts = new List<DiagnosticFact>();

        for (var index = 0; index < observation.Devices.Count; index++)
        {
            var device = observation.Devices[index];
            var name = string.IsNullOrWhiteSpace(device.Name)
                ? $"设备 {index + 1}"
                : device.Name;

            facts.Add(new DiagnosticFact($"{name} 地址", device.Endpoint));

            if (device.Scopes.Count > 0)
            {
                facts.Add(new DiagnosticFact($"{name} Scope", string.Join(" ", device.Scopes)));
            }
        }

        return observation.Status switch
        {
            OnvifObservationStatus.Passed => DiagnosticStepResults.Passed(
                context.StepId,
                context.Name,
                observation.Summary,
                context.StartedAt,
                duration,
                facts: facts,
                rawOutput: observation.RawOutput),
            OnvifObservationStatus.Failed => DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                observation.Summary,
                context.StartedAt,
                duration,
                observation.Suggestion,
                facts: facts,
                rawOutput: observation.RawOutput),
            OnvifObservationStatus.TimedOut => DiagnosticStepResults.TimedOut(
                context.StepId,
                context.Name,
                timeout,
                context.StartedAt,
                duration,
                observation.Suggestion),
            _ => DiagnosticStepResults.Unknown(
                context.StepId,
                context.Name,
                observation.Summary,
                context.StartedAt,
                duration,
                observation.Suggestion,
                facts,
                observation.RawOutput)
        };
    }

    private static DiagnosticStepResult MapOperation(
        DiagnosticStepContext context,
        OnvifEndpoint endpoint,
        OnvifOperationObservation observation,
        TimeSpan timeout,
        TimeSpan duration)
    {
        var facts = new List<DiagnosticFact>
        {
            new("设备服务", endpoint.DisplayUrl)
        };
        facts.AddRange(observation.Facts.Select(static fact =>
            new DiagnosticFact(fact.Label, fact.Value)));

        return observation.Status switch
        {
            OnvifObservationStatus.Passed => DiagnosticStepResults.Passed(
                context.StepId,
                context.Name,
                observation.Summary,
                context.StartedAt,
                duration,
                facts: facts,
                rawOutput: observation.RawOutput),
            OnvifObservationStatus.Warning => DiagnosticStepResults.Warning(
                context.StepId,
                context.Name,
                observation.Summary,
                context.StartedAt,
                duration,
                observation.Suggestion,
                facts: facts,
                rawOutput: observation.RawOutput),
            OnvifObservationStatus.Failed => DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                observation.Summary,
                context.StartedAt,
                duration,
                observation.Suggestion,
                facts: facts,
                rawOutput: observation.RawOutput),
            OnvifObservationStatus.TimedOut => DiagnosticStepResults.TimedOut(
                context.StepId,
                context.Name,
                timeout,
                context.StartedAt,
                duration,
                observation.Suggestion),
            _ => DiagnosticStepResults.Unknown(
                context.StepId,
                context.Name,
                observation.Summary,
                context.StartedAt,
                duration,
                observation.Suggestion,
                facts,
                observation.RawOutput)
        };
    }

    private static OnvifCredentials? ReadCredentials(DiagnosticStepContext context)
    {
        var username = context.GetParameter(OnvifParameterNames.Username);
        var password = context.GetParameter(OnvifParameterNames.Password);

        return string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password)
            ? null
            : new OnvifCredentials(username ?? string.Empty, password ?? string.Empty);
    }

    private static OnvifTool ParseTool(string? value) =>
        Enum.TryParse<OnvifTool>(value, ignoreCase: true, out var tool)
            ? tool
            : OnvifTool.Inspect;

    private static TimeSpan ParseTimeout(DiagnosticRequest request) =>
        ParseTimeout(request.GetParameter(OnvifParameterNames.TimeoutSeconds));

    private static TimeSpan ParseTimeout(DiagnosticStepContext context) =>
        ParseTimeout(context.GetParameter(OnvifParameterNames.TimeoutSeconds));

    private static TimeSpan ParseTimeout(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
        && seconds is >= 1 and <= 60
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(10);
}
