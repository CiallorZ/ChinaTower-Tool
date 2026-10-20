using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Adapters.Network.Tests;

/// <summary>
/// LF-006 网络适配器契约测试。
/// </summary>
/// <remarks>
/// 验收要求：成功、拒绝、超时、DNS 不存在、取消；
/// 以及“ICMP 无响应不得断言离线”和“Traceroute 平台限制说明”。
/// </remarks>
public class NetworkDiagnosticAdapterTests
{
    private static DiagnosticRequest Request(
        NetworkTool tool,
        string target = "example.test",
        int? port = null,
        int timeoutSeconds = 5,
        int maxHops = 16)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [NetworkParameterNames.Tool] = tool.ToString(),
            [NetworkParameterNames.Target] = target,
            [NetworkParameterNames.TimeoutSeconds] = timeoutSeconds.ToString(),
            [NetworkParameterNames.MaxHops] = maxHops.ToString()
        };

        if (port is { } value)
        {
            parameters[NetworkParameterNames.Port] = value.ToString();
        }

        return new DiagnosticRequest("run-network", target, parameters);
    }

    private static DiagnosticStepContext Context(DiagnosticRequest request) =>
        new(
            request.RunId,
            "step",
            "网络步骤",
            request.TargetLabel,
            request.ParameterMap,
            DateTimeOffset.Now);

    private static RecordingProgress Progress() => new();

    [Fact]
    public void Plan_should_only_declare_the_selected_tool()
    {
        var adapter = new NetworkDiagnosticAdapter(new FakeNetworkProbe());

        var plan = adapter.Plan(Request(NetworkTool.Tcp, port: 554));

        plan.Count.ShouldBe(1);
        plan[0].StepId.ShouldBe("tcp");
        plan[0].Name.ShouldContain("TCP");
    }

    [Fact]
    public async Task Successful_ping_should_map_to_passed_with_facts()
    {
        var fake = new FakeNetworkProbe
        {
            PingBehavior = (target, _, _) => Task.FromResult(new PingObservation(
                NetworkObservationStatus.Passed,
                "收到来自 192.0.2.1 的 ICMP 回应，往返 8 ms。",
                [new NetworkFact("目标", target), new NetworkFact("往返时间", "8 ms")]))
        };

        var adapter = new NetworkDiagnosticAdapter(fake);
        var result = await adapter.ExecuteStepAsync(
            Context(Request(NetworkTool.Ping)), Progress(), CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Passed);
        result.HasFacts.ShouldBeTrue();
        result.FactItems.ShouldContain(f => f.Label == "往返时间" && f.Value == "8 ms");
    }

    [Fact]
    public async Task Unresponsive_icmp_must_be_unknown_and_must_not_claim_the_host_is_offline()
    {
        var fake = new FakeNetworkProbe
        {
            PingBehavior = (target, _, _) => Task.FromResult(new PingObservation(
                NetworkObservationStatus.Unknown,
                "ICMP 无响应或超时。这不能单独证明主机不可达：目标可能禁 Ping，或中间设备丢弃了 ICMP。",
                [new NetworkFact("目标", target)],
                "请继续用 TCP 端口或应用层协议验证；只有 ICMP 结果时不要判定设备离线。"))
        };

        var adapter = new NetworkDiagnosticAdapter(fake);
        var result = await adapter.ExecuteStepAsync(
            Context(Request(NetworkTool.Ping)), Progress(), CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Unknown);
        result.Summary.ShouldContain("不能单独证明");
        result.Summary.ShouldNotContain("离线");
        result.NextStepSuggestion.ShouldNotBeNull().ShouldContain("TCP");
    }

    [Fact]
    public async Task Refused_tcp_connection_should_be_failed_not_unknown()
    {
        var fake = new FakeNetworkProbe
        {
            TcpBehavior = (target, port, _, _) => Task.FromResult(new TcpObservation(
                NetworkObservationStatus.Failed,
                "目标主机可达，但该端口拒绝连接；服务可能未启动或端口不正确。",
                [new NetworkFact("目标", $"{target}:{port}")],
                "核对目标地址与端口。"))
        };

        var adapter = new NetworkDiagnosticAdapter(fake);
        var result = await adapter.ExecuteStepAsync(
            Context(Request(NetworkTool.Tcp, port: 554)), Progress(), CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Failed);
        result.FactItems.ShouldContain(f => f.Value == "example.test:554");
    }

    [Fact]
    public async Task Timeout_should_use_the_timeout_conclusion()
    {
        var fake = new FakeNetworkProbe
        {
            TcpBehavior = (_, _, timeout, _) => Task.FromResult(new TcpObservation(
                NetworkObservationStatus.TimedOut,
                $"连接超过 {timeout.TotalSeconds:0.#} 秒。",
                [new NetworkFact("结果", "超时")],
                "检查防火墙。"))
        };

        var adapter = new NetworkDiagnosticAdapter(fake);
        var result = await adapter.ExecuteStepAsync(
            Context(Request(NetworkTool.Tcp, port: 443)), Progress(), CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.TimedOut);
        result.Summary.ShouldContain("超时");
    }

    [Fact]
    public async Task Missing_dns_name_should_be_failed_with_actionable_suggestion()
    {
        var fake = new FakeNetworkProbe
        {
            DnsBehavior = (target, _) => Task.FromResult(new DnsObservation(
                NetworkObservationStatus.Failed,
                "DNS 查询失败：HostNotFound。",
                [new NetworkFact("名称", target), new NetworkFact("错误", "HostNotFound")],
                "检查 DNS 服务器、网络连接与名称拼写。"))
        };

        var adapter = new NetworkDiagnosticAdapter(fake);
        var result = await adapter.ExecuteStepAsync(
            Context(Request(NetworkTool.Dns, "does-not-exist.invalid")), Progress(), CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Failed);
        result.FactItems.ShouldContain(f => f.Value == "HostNotFound");
        result.NextStepSuggestion.ShouldNotBeNull().ShouldContain("DNS");
    }

    [Fact]
    public async Task Cancellation_should_propagate_instead_of_becoming_failed()
    {
        var fake = new FakeNetworkProbe
        {
            PingBehavior = async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("不应到达这里。");
            }
        };

        var adapter = new NetworkDiagnosticAdapter(fake);
        using var cts = new CancellationTokenSource();

        var run = adapter.ExecuteStepAsync(Context(Request(NetworkTool.Ping)), Progress(), cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Local_network_snapshot_should_be_passed_with_raw_output()
    {
        var fake = new FakeNetworkProbe
        {
            LocalBehavior = _ => Task.FromResult(new LocalNetworkSnapshot(
                [new NetworkFact("网卡 Ethernet", "192.0.2.10")],
                "Ethernet 192.0.2.10"))
        };

        var adapter = new NetworkDiagnosticAdapter(fake);
        var result = await adapter.ExecuteStepAsync(
            Context(Request(NetworkTool.Local)), Progress(), CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Passed);
        result.HasRawOutput.ShouldBeTrue();
        result.RawOutput.ShouldNotBeNull().ShouldContain("Ethernet");
    }

    [Fact]
    public async Task Traceroute_should_report_unknown_when_no_final_response_is_observed()
    {
        var fake = new FakeNetworkProbe
        {
            TracerouteBehavior = (_, _, _, _) => Task.FromResult(new TracerouteObservation(
                NetworkObservationStatus.Unknown,
                "路径探测未得到明确的最终目标回应；中间跳点无响应不能证明目标离线。",
                [new NetworkFact("第 1 跳", "无响应/超时")],
                "raw",
                "可尝试 TCP 端口验证。"))
        };

        var adapter = new NetworkDiagnosticAdapter(fake);
        var result = await adapter.ExecuteStepAsync(
            Context(Request(NetworkTool.Traceroute)), Progress(), CancellationToken.None);

        result.Status.ShouldBe(DiagnosticStatus.Unknown);
        result.Summary.ShouldContain("不能证明目标离线");
        result.HasRawOutput.ShouldBeTrue();
    }

    [Fact]
    public void Invalid_port_and_timeout_should_fall_back_to_safe_defaults()
    {
        var request = new DiagnosticRequest(
            "run",
            "example.test",
            new Dictionary<string, string?>
            {
                [NetworkParameterNames.Tool] = "Tcp",
                [NetworkParameterNames.Target] = "example.test",
                [NetworkParameterNames.Port] = "70000",
                [NetworkParameterNames.TimeoutSeconds] = "0"
            });

        var plan = new NetworkDiagnosticAdapter(new FakeNetworkProbe()).Plan(request);

        plan.ShouldHaveSingleItem();
        plan[0].Timeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    private sealed class RecordingProgress : IProgress<DiagnosticProgress>
    {
        public List<DiagnosticProgress> Events { get; } = [];

        public void Report(DiagnosticProgress value) => Events.Add(value);
    }
}
