namespace LinkForge.Adapters.Video;

/// <summary>RTSP 诊断参数名。</summary>
public static class RtspParameterNames
{
    /// <summary>完整 RTSP URL（可能包含 userinfo；不得写入日志）。</summary>
    public const string Url = "url";

    /// <summary>不跟随重定向时使用的单步超时秒数。</summary>
    public const string TimeoutSeconds = "timeoutSeconds";
}

/// <summary>RTSP URL 的安全分解结果。</summary>
/// <param name="Original">原始 URL；仅用于实际请求，不用于展示。</param>
/// <param name="Redacted">已脱敏的展示 URL。</param>
/// <param name="Scheme">协议（rtsp 或 rtsps）。</param>
/// <param name="Host">主机名或 IP。</param>
/// <param name="Port">端口。</param>
/// <param name="UserInfo">userinfo；存在时只用于 Basic 认证，不展示。</param>
/// <param name="PathAndQuery">路径与查询。</param>
public sealed record RtspEndpoint(
    string Original,
    string Redacted,
    string Scheme,
    string Host,
    int Port,
    string? UserInfo,
    string PathAndQuery);

/// <summary>RTSP 探测接口；测试只替换 socket/HTTP 边界。</summary>
public interface IRtspProbe
{
    /// <summary>建立 TCP 连接并发起 OPTIONS + DESCRIBE。</summary>
    Task<RtspObservation> ProbeAsync(
        RtspEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>RTSP 观察状态。</summary>
public enum RtspObservationStatus
{
    /// <summary>信令与 SDP 均成功。</summary>
    Passed,

    /// <summary>明确失败（连接拒绝、401、协议错误、无效 SDP 等）。</summary>
    Failed,

    /// <summary>无法判定。</summary>
    Unknown,

    /// <summary>超时。</summary>
    TimedOut,

    /// <summary>未执行的后续阶段（例如媒体接收/解码）。</summary>
    Skipped
}

/// <summary>一个 RTSP 阶段观察结果。</summary>
/// <param name="StageId">阶段标识（network/handshake/authentication/sdp）。</param>
/// <param name="StageName">阶段名称。</param>
/// <param name="Status">阶段状态。</param>
/// <param name="Summary">面向用户的摘要。</param>
/// <param name="Facts">结构化事实。</param>
/// <param name="RawOutput">脱敏后的原始信令输出。</param>
/// <param name="Suggestion">下一步建议。</param>
public sealed record RtspStageObservation(
    string StageId,
    string StageName,
    RtspObservationStatus Status,
    string Summary,
    IReadOnlyList<RtspFact> Facts,
    string? RawOutput = null,
    string? Suggestion = null);

/// <summary>RTSP 事实项。</summary>
public readonly record struct RtspFact(string Label, string Value);

/// <summary>完整 RTSP 探测结果。</summary>
public sealed record RtspObservation(
    RtspObservationStatus Status,
    string Summary,
    IReadOnlyList<RtspStageObservation> Stages,
    IReadOnlyList<RtspFact> Facts,
    string? Suggestion = null,
    string? RawOutput = null);
