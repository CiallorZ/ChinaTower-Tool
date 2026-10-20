using System.Net;

namespace LinkForge.Adapters.Network;

/// <summary>网络探测的统一观察结果。</summary>
/// <param name="Status">观察结论。</param>
/// <param name="Summary">面向用户的摘要。</param>
/// <param name="Facts">结构化事实。</param>
/// <param name="RawOutput">原始输出；调用方在进入历史前仍需脱敏。</param>
/// <param name="Suggestion">下一步建议。</param>
public sealed record NetworkObservation(
    NetworkObservationStatus Status,
    string Summary,
    IReadOnlyList<NetworkFact> Facts,
    string? RawOutput = null,
    string? Suggestion = null);

/// <summary>网络观察结论（与领域状态分离，便于测试适配器内部行为）。</summary>
public enum NetworkObservationStatus
{
    /// <summary>成功。</summary>
    Passed,

    /// <summary>失败。</summary>
    Failed,

    /// <summary>无法判定。</summary>
    Unknown,

    /// <summary>超时。</summary>
    TimedOut
}

/// <summary>网络事实。</summary>
public readonly record struct NetworkFact(string Label, string Value);

/// <summary>本机网络快照。</summary>
public sealed record LocalNetworkSnapshot(
    IReadOnlyList<NetworkFact> Facts,
    string RawOutput);

/// <summary>DNS 解析结果。</summary>
public sealed record DnsObservation(
    NetworkObservationStatus Status,
    string Summary,
    IReadOnlyList<NetworkFact> Facts,
    string? Suggestion = null);

/// <summary>Ping 结果。</summary>
public sealed record PingObservation(
    NetworkObservationStatus Status,
    string Summary,
    IReadOnlyList<NetworkFact> Facts,
    string? Suggestion = null);

/// <summary>TCP 连接结果。</summary>
public sealed record TcpObservation(
    NetworkObservationStatus Status,
    string Summary,
    IReadOnlyList<NetworkFact> Facts,
    string? Suggestion = null);

/// <summary>Traceroute 结果。</summary>
public sealed record TracerouteObservation(
    NetworkObservationStatus Status,
    string Summary,
    IReadOnlyList<NetworkFact> Facts,
    string? RawOutput = null,
    string? Suggestion = null);

/// <summary>网络探测接口。</summary>
/// <remarks>
/// 把系统 API 放在接口后面，测试才能在不依赖公网、真实网卡或 tracert 的情况下
/// 覆盖成功、拒绝、超时、DNS 不存在和取消。生产组合根只注入真实实现。
/// </remarks>
public interface INetworkProbe
{
    /// <summary>执行 Ping。</summary>
    Task<PingObservation> PingAsync(string target, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>执行 TCP 连接。</summary>
    Task<TcpObservation> TcpAsync(string target, int port, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>执行 DNS 解析。</summary>
    Task<DnsObservation> DnsAsync(string target, CancellationToken cancellationToken);

    /// <summary>读取本机网络配置。</summary>
    Task<LocalNetworkSnapshot> LocalAsync(CancellationToken cancellationToken);

    /// <summary>执行 Traceroute。</summary>
    Task<TracerouteObservation> TracerouteAsync(
        string target,
        int maxHops,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
