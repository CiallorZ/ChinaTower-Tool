namespace LinkForge.Adapters.Network.Tests;

/// <summary>
/// 可编排的假网络探测：只模拟系统边界，不模拟适配器逻辑。
/// </summary>
/// <remarks>
/// 适配器对“通过/失败/未知/超时”的映射仍走真实实现，
/// 这样测试验证的是生产代码，而不是另一套假规则。
/// </remarks>
internal sealed class FakeNetworkProbe : INetworkProbe
{
    public Func<string, TimeSpan, CancellationToken, Task<PingObservation>>? PingBehavior { get; set; }

    public Func<string, int, TimeSpan, CancellationToken, Task<TcpObservation>>? TcpBehavior { get; set; }

    public Func<string, CancellationToken, Task<DnsObservation>>? DnsBehavior { get; set; }

    public Func<CancellationToken, Task<LocalNetworkSnapshot>>? LocalBehavior { get; set; }

    public Func<string, int, TimeSpan, CancellationToken, Task<TracerouteObservation>>? TracerouteBehavior { get; set; }

    public Task<PingObservation> PingAsync(string target, TimeSpan timeout, CancellationToken cancellationToken) =>
        PingBehavior?.Invoke(target, timeout, cancellationToken)
        ?? Task.FromResult(new PingObservation(
            NetworkObservationStatus.Passed,
            "收到回应",
            [new NetworkFact("目标", target)]));

    public Task<TcpObservation> TcpAsync(
        string target,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        TcpBehavior?.Invoke(target, port, timeout, cancellationToken)
        ?? Task.FromResult(new TcpObservation(
            NetworkObservationStatus.Passed,
            "连接成功",
            [new NetworkFact("目标", $"{target}:{port}")]));

    public Task<DnsObservation> DnsAsync(string target, CancellationToken cancellationToken) =>
        DnsBehavior?.Invoke(target, cancellationToken)
        ?? Task.FromResult(new DnsObservation(
            NetworkObservationStatus.Passed,
            "解析成功",
            [new NetworkFact("名称", target)]));

    public Task<LocalNetworkSnapshot> LocalAsync(CancellationToken cancellationToken) =>
        LocalBehavior?.Invoke(cancellationToken)
        ?? Task.FromResult(new LocalNetworkSnapshot(
            [new NetworkFact("网卡 Ethernet", "192.0.2.10")],
            "Ethernet 192.0.2.10"));

    public Task<TracerouteObservation> TracerouteAsync(
        string target,
        int maxHops,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        TracerouteBehavior?.Invoke(target, maxHops, timeout, cancellationToken)
        ?? Task.FromResult(new TracerouteObservation(
            NetworkObservationStatus.Passed,
            "路径探测完成",
            [new NetworkFact("第 1 跳", "192.0.2.1")]));
}
