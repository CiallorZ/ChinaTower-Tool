namespace LinkForge.Adapters.Security;

/// <summary>安全基线观察结论。</summary>
public enum SecurityObservationStatus
{
    /// <summary>检查完成，未发现需要提示的证书问题。</summary>
    Passed,

    /// <summary>检查完成，但发现证书或 TLS 基线问题。</summary>
    Warning,

    /// <summary>检查无法执行。</summary>
    Failed,

    /// <summary>部分结果无法判定。</summary>
    Unknown,

    /// <summary>超时。</summary>
    TimedOut
}

/// <summary>TCP 端口观察状态。</summary>
public enum SecurityPortState
{
    /// <summary>连接成功。</summary>
    Open,

    /// <summary>目标明确拒绝连接。</summary>
    Closed,

    /// <summary>超时或网络策略导致无响应。</summary>
    Filtered,

    /// <summary>证据不足，无法区分关闭与过滤。</summary>
    Unknown
}

/// <summary>TLS 握手与证书观察状态。</summary>
public enum SecurityTlsState
{
    /// <summary>未请求 TLS。</summary>
    NotRequested,

    /// <summary>握手成功且默认证书校验通过。</summary>
    Valid,

    /// <summary>证书链或主机名校验失败。</summary>
    CertificateRejected,

    /// <summary>证书有效但协议或握手失败。</summary>
    HandshakeFailed,

    /// <summary>TLS 超时。</summary>
    TimedOut
}

/// <summary>安全事实的状态。</summary>
public enum SecurityFactStatus
{
    /// <summary>纯事实。</summary>
    Information,

    /// <summary>符合基线。</summary>
    Passed,

    /// <summary>需要关注。</summary>
    Warning,

    /// <summary>失败。</summary>
    Failed,

    /// <summary>无法判定。</summary>
    Unknown
}

/// <summary>安全基线事实。</summary>
public readonly record struct SecurityFact(
    string Label,
    string Value,
    SecurityFactStatus Status = SecurityFactStatus.Information);

/// <summary>TLS 检查结果。</summary>
public sealed record SecurityTlsResult(
    int Port,
    SecurityTlsState State,
    string Summary,
    IReadOnlyList<SecurityFact> Facts);

/// <summary>单个端口检查结果。</summary>
public sealed record SecurityPortResult(
    int Port,
    SecurityPortState State,
    string Summary,
    TimeSpan Duration,
    SecurityTlsResult? Tls = null);

/// <summary>一次授权范围内安全基线的完整观察。</summary>
public sealed record SecurityBaselineObservation(
    SecurityObservationStatus Status,
    string Summary,
    IReadOnlyList<SecurityFact> Facts,
    IReadOnlyList<SecurityPortResult> Ports,
    string? RawOutput = null,
    string? Suggestion = null);

/// <summary>安全基线请求。</summary>
/// <param name="Target">单一目标主机或 IP。</param>
/// <param name="Ports">用户明确选择的 TCP 端口。</param>
/// <param name="TlsPorts">需要执行 TLS 握手的端口；必须属于 <paramref name="Ports"/>。</param>
/// <param name="Timeout">单个连接或握手超时。</param>
/// <param name="Concurrency">并发检查上限。</param>
/// <param name="DelayBetweenChecks">相邻检查启动之间的最小延迟。</param>
public sealed record SecurityBaselineRequest(
    string Target,
    IReadOnlyList<int> Ports,
    IReadOnlyList<int> TlsPorts,
    TimeSpan Timeout,
    int Concurrency,
    TimeSpan DelayBetweenChecks);

/// <summary>探测进度。</summary>
public readonly record struct SecurityProbeProgress(
    int Completed,
    int Total,
    string Message);

/// <summary>安全探测边界。</summary>
public interface ISecurityProbe
{
    /// <summary>执行一次授权范围内的 TCP/TLS 基线检查。</summary>
    Task<SecurityBaselineObservation> RunAsync(
        SecurityBaselineRequest request,
        IProgress<SecurityProbeProgress>? progress,
        CancellationToken cancellationToken);
}
