using System.Globalization;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Adapters.Network;

/// <summary>
/// 网络基础诊断适配器（LF-006）。
/// </summary>
/// <remarks>
/// <para>
/// 一次请求只执行用户选择的一个网络工具，而不是默认把所有探测都跑一遍：
/// 扫描/探测范围必须可见、最小化，并且用户应当能预期即将发生的网络动作。
/// </para>
/// <para>
/// 适配器只负责“观察”，超时、取消、异常映射和脱敏由统一编排器完成。
/// 这样 ICMP 无响应、TCP 拒绝、DNS 不存在等结论不会在页面之间出现不同口径。
/// </para>
/// </remarks>
public sealed class NetworkDiagnosticAdapter : IDiagnosticAdapter
{
    /// <summary>稳定适配器标识。</summary>
    public const string AdapterId = "network.basic";

    private readonly INetworkProbe _probe;

    /// <summary>创建网络诊断适配器。</summary>
    /// <param name="probe">底层探测实现。</param>
    public NetworkDiagnosticAdapter(INetworkProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    /// <inheritdoc />
    public string Id => AdapterId;

    /// <inheritdoc />
    public string DisplayName => "网络基础诊断";

    /// <inheritdoc />
    public Version ApiVersion => new(1, 0);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Capabilities =>
        ["network.ping", "network.tcp", "network.dns", "network.local", "network.traceroute"];

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticStepPlan> Plan(DiagnosticRequest request)
    {
        var tool = ParseTool(request);
        var name = ToolDisplayName(tool);

        return
        [
            new DiagnosticStepPlan(
                tool.ToString().ToLowerInvariant(),
                name,
                Timeout: tool == NetworkTool.Traceroute
                    ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromSeconds(10))
        ];
    }

    /// <inheritdoc />
    public async Task<DiagnosticStepResult> ExecuteStepAsync(
        DiagnosticStepContext context,
        IProgress<DiagnosticProgress> progress,
        CancellationToken cancellationToken)
    {
        var tool = ParseTool(context);
        var target = context.GetParameter(NetworkParameterNames.Target)?.Trim() ?? string.Empty;
        var timeout = ParseTimeout(context);

        progress.Report(new DiagnosticProgress(
            context.RunId,
            context.StepId,
            DiagnosticStepPhase.Running,
            DescribeRunning(tool, target),
            DateTimeOffset.Now,
            DateTimeOffset.Now - context.StartedAt));

        var startedAt = context.StartedAt;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        NetworkObservation observation;

        switch (tool)
        {
            case NetworkTool.Ping:
                observation = ToObservation(await _probe.PingAsync(target, timeout, cancellationToken).ConfigureAwait(false));
                break;

            case NetworkTool.Tcp:
                var port = ParsePort(context);
                observation = ToObservation(
                    await _probe.TcpAsync(target, port, timeout, cancellationToken).ConfigureAwait(false));
                break;

            case NetworkTool.Dns:
                observation = ToObservation(await _probe.DnsAsync(target, cancellationToken).ConfigureAwait(false));
                break;

            case NetworkTool.Local:
                var local = await _probe.LocalAsync(cancellationToken).ConfigureAwait(false);
                observation = new NetworkObservation(
                    NetworkObservationStatus.Passed,
                    $"已读取 {local.Facts.Count} 项本机网络事实。",
                    local.Facts,
                    local.RawOutput);
                break;

            case NetworkTool.Traceroute:
                var maxHops = ParseMaxHops(context);
                var traceroute = await _probe
                    .TracerouteAsync(target, maxHops, timeout, cancellationToken)
                    .ConfigureAwait(false);

                observation = new NetworkObservation(
                    traceroute.Status,
                    traceroute.Summary,
                    traceroute.Facts,
                    traceroute.RawOutput,
                    traceroute.Suggestion);
                break;

            default:
                return DiagnosticStepResults.Unknown(
                    context.StepId,
                    context.Name,
                    "未识别的网络工具。",
                    startedAt,
                    watch.Elapsed,
                    "请选择 Ping、TCP、DNS、本机网络或 Traceroute。");
        }

        watch.Stop();
        var facts = observation.Facts
            .Select(static f => new DiagnosticFact(f.Label, f.Value))
            .ToArray();

        return observation.Status switch
        {
            NetworkObservationStatus.Passed => DiagnosticStepResults.Passed(
                context.StepId,
                context.Name,
                observation.Summary,
                startedAt,
                watch.Elapsed,
                facts: facts,
                rawOutput: observation.RawOutput),

            NetworkObservationStatus.Failed => DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                observation.Summary,
                startedAt,
                watch.Elapsed,
                observation.Suggestion,
                facts: facts,
                rawOutput: observation.RawOutput),

            NetworkObservationStatus.TimedOut => DiagnosticStepResults.TimedOut(
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

    private static NetworkObservation ToObservation(PingObservation observation) =>
        new(
            observation.Status,
            observation.Summary,
            observation.Facts,
            Suggestion: observation.Suggestion);

    private static NetworkObservation ToObservation(TcpObservation observation) =>
        new(
            observation.Status,
            observation.Summary,
            observation.Facts,
            Suggestion: observation.Suggestion);

    private static NetworkObservation ToObservation(DnsObservation observation) =>
        new(
            observation.Status,
            observation.Summary,
            observation.Facts,
            Suggestion: observation.Suggestion);

    private static NetworkTool ParseTool(DiagnosticRequest request) =>
        ParseTool(request.GetParameter(NetworkParameterNames.Tool));

    private static NetworkTool ParseTool(DiagnosticStepContext context) =>
        ParseTool(context.GetParameter(NetworkParameterNames.Tool));

    private static NetworkTool ParseTool(string? value) =>
        Enum.TryParse<NetworkTool>(value, ignoreCase: true, out var tool)
            ? tool
            : NetworkTool.Ping;

    private static TimeSpan ParseTimeout(DiagnosticStepContext context)
    {
        var raw = context.GetParameter(NetworkParameterNames.TimeoutSeconds);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
               && seconds is >= 1 and <= 60
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(5);
    }

    private static int ParsePort(DiagnosticStepContext context)
    {
        var raw = context.GetParameter(NetworkParameterNames.Port);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
               && port is >= 1 and <= 65535
            ? port
            : 80;
    }

    private static int ParseMaxHops(DiagnosticStepContext context)
    {
        var raw = context.GetParameter(NetworkParameterNames.MaxHops);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hops)
               && hops is >= 1 and <= 30
            ? hops
            : 16;
    }

    private static string ToolDisplayName(NetworkTool tool) => tool switch
    {
        NetworkTool.Ping => "Ping / ICMP",
        NetworkTool.Tcp => "TCP 端口连接",
        NetworkTool.Dns => "DNS 查询",
        NetworkTool.Local => "本机网络与路由",
        NetworkTool.Traceroute => "Traceroute 路径",
        _ => "网络诊断"
    };

    private static string DescribeRunning(NetworkTool tool, string target) => tool switch
    {
        NetworkTool.Ping => $"正在发送 ICMP 探测：{target}…",
        NetworkTool.Tcp => $"正在建立 TCP 连接：{target}…",
        NetworkTool.Dns => $"正在解析名称：{target}…",
        NetworkTool.Local => "正在读取本机网卡、网关与路由…",
        NetworkTool.Traceroute => $"正在探测路径：{target}…",
        _ => "正在执行网络诊断…"
    };
}
