using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LinkForge.Adapters.Network;

/// <summary>
/// 基于 Windows 与 .NET 原生 API 的真实网络探测实现。
/// </summary>
/// <remarks>
/// <para>
/// Ping/TCP/DNS/网卡使用 .NET API；Traceroute 包装系统 <c>tracert.exe</c>，
/// 全部通过 <see cref="ProcessStartInfo.ArgumentList"/> 传参，禁止 shell 字符串拼接
/// （ADR-009）。
/// </para>
/// <para>
/// ICMP 无响应只说明“没有收到 Echo Reply”，不能据此断言目标离线：
/// 防火墙、路由策略或目标主机禁 Ping 都会产生同样结果。因此这种情况返回
/// <see cref="NetworkObservationStatus.Unknown"/> 并明确写出限制。
/// </para>
/// </remarks>
public sealed partial class WindowsNetworkProbe : INetworkProbe
{
    /// <summary>读取本机网卡与路由快照。</summary>
    public Task<LocalNetworkSnapshot> LocalAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadLocalSnapshot(cancellationToken), cancellationToken);

    private static LocalNetworkSnapshot ReadLocalSnapshot(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var facts = new List<NetworkFact>();
        var raw = new StringBuilder();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(static a => a.OperationalStatus == OperationalStatus.Up))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var properties = adapter.GetIPProperties();
            var addresses = properties.UnicastAddresses
                .Where(static a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(static a => a.Address.ToString())
                .ToArray();

            if (addresses.Length == 0)
            {
                continue;
            }

            facts.Add(new NetworkFact($"网卡 {adapter.Name}", string.Join(", ", addresses)));
            raw.AppendLine($"{adapter.Name} [{adapter.NetworkInterfaceType}] {string.Join(", ", addresses)}");

            var gateway = properties.GatewayAddresses
                .Select(static g => g.Address)
                .Where(static a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(static a => a.ToString())
                .ToArray();

            if (gateway.Length > 0)
            {
                facts.Add(new NetworkFact($"网关 {adapter.Name}", string.Join(", ", gateway)));
            }

            var dns = properties.DnsAddresses
                .Where(static a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(static a => a.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (dns.Length > 0)
            {
                facts.Add(new NetworkFact($"DNS {adapter.Name}", string.Join(", ", dns)));
            }

            raw.AppendLine($"  gateway: {string.Join(", ", gateway)}");
            raw.AppendLine($"  dns: {string.Join(", ", dns)}");
        }

        foreach (var route in ReadIPv4Routes())
        {
            facts.Add(new NetworkFact($"路由 {route.Destination}", $"via {route.Gateway} / {route.Interface}"));
            raw.AppendLine($"route {route.Destination} via {route.Gateway} if {route.Interface}");
        }

        return new LocalNetworkSnapshot(facts, raw.ToString().TrimEnd());
    }

    /// <summary>执行 Ping。</summary>
    public async Task<PingObservation> PingAsync(
        string target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await SendPingAsync(
                    ping,
                    target,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            var facts = new[]
            {
                new NetworkFact("目标", target),
                new NetworkFact("状态", reply.Status.ToString()),
                new NetworkFact("往返时间", $"{reply.RoundtripTime} ms")
            };

            if (reply.Status == IPStatus.Success)
            {
                return new PingObservation(
                    NetworkObservationStatus.Passed,
                    $"收到来自 {reply.Address} 的 ICMP 回应，往返 {reply.RoundtripTime} ms。",
                    facts);
            }

            if (reply.Status is IPStatus.TimedOut or IPStatus.TimeExceeded)
            {
                return new PingObservation(
                    NetworkObservationStatus.Unknown,
                    "ICMP 无响应或超时。这不能单独证明主机不可达：目标可能禁 Ping，或中间设备丢弃了 ICMP。",
                    facts,
                    "请继续用 TCP 端口或应用层协议验证；只有 ICMP 结果时不要判定设备离线。");
            }

            return new PingObservation(
                NetworkObservationStatus.Unknown,
                $"ICMP 未得到成功回应（{reply.Status}）。不能仅据此判断主机离线。",
                facts,
                "请继续用 TCP 端口或应用层协议验证。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PingObservation(
                NetworkObservationStatus.TimedOut,
                "ICMP 探测超过设定超时。",
                [new NetworkFact("目标", target)],
                "可提高超时后重试，或改用 TCP 端口验证。");
        }
        catch (PingException ex)
        {
            return new PingObservation(
                NetworkObservationStatus.Failed,
                $"ICMP 探测无法执行：{ex.Message}",
                [new NetworkFact("目标", target)],
                "检查目标名称、网络权限与防火墙策略。");
        }
    }

    /// <summary>执行 TCP 连接。</summary>
    public async Task<TcpObservation> TcpAsync(
        string target,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var facts = new[]
        {
            new NetworkFact("目标", $"{target}:{port}"),
            new NetworkFact("协议", "TCP")
        };

        using var client = new TcpClient();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await client.ConnectAsync(target, port, linked.Token).ConfigureAwait(false);

            return new TcpObservation(
                NetworkObservationStatus.Passed,
                $"TCP {target}:{port} 连接成功。",
                [.. facts, new NetworkFact("结果", "已建立连接")]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new TcpObservation(
                NetworkObservationStatus.TimedOut,
                $"TCP {target}:{port} 连接超过 {timeout.TotalSeconds:0.#} 秒。",
                [.. facts, new NetworkFact("结果", "超时")],
                "确认目标可达、端口正确，并检查防火墙是否丢弃了连接。");
        }
        catch (SocketException ex)
        {
            var hint = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "目标主机可达，但该端口拒绝连接；服务可能未启动或端口不正确。",
                SocketError.HostNotFound => "目标名称无法解析。",
                SocketError.NetworkUnreachable => "当前主机没有到目标网络的路由。",
                SocketError.HostUnreachable => "网络路径报告目标不可达。",
                _ => "TCP 连接未建立。"
            };

            return new TcpObservation(
                NetworkObservationStatus.Failed,
                $"{hint}（{ex.SocketErrorCode}）",
                [.. facts, new NetworkFact("错误", ex.SocketErrorCode.ToString())],
                "核对目标地址与端口，再从目标侧检查服务监听和防火墙。");
        }
    }

    /// <summary>执行 DNS 解析。</summary>
    public async Task<DnsObservation> DnsAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false);

            if (addresses.Length == 0)
            {
                return new DnsObservation(
                    NetworkObservationStatus.Unknown,
                    $"DNS 查询 {target} 没有返回地址记录。",
                    [new NetworkFact("名称", target)],
                    "确认名称拼写、DNS 后缀和记录类型。");
            }

            var distinct = addresses
                .Select(static a => a.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return new DnsObservation(
                NetworkObservationStatus.Passed,
                $"DNS 解析成功，共 {distinct.Length} 个地址。",
                [
                    new NetworkFact("名称", target),
                    new NetworkFact("地址", string.Join(", ", distinct))
                ]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SocketException ex)
        {
            return new DnsObservation(
                NetworkObservationStatus.Failed,
                $"DNS 查询失败：{ex.SocketErrorCode}。",
                [
                    new NetworkFact("名称", target),
                    new NetworkFact("错误", ex.SocketErrorCode.ToString())
                ],
                "检查 DNS 服务器、网络连接与名称拼写。");
        }
    }

    /// <summary>执行 Traceroute（Windows tracert）。</summary>
    public async Task<TracerouteObservation> TracerouteAsync(
        string target,
        int maxHops,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TracerouteObservation(
                NetworkObservationStatus.Unknown,
                "当前平台不提供 Windows tracert 包装。",
                [new NetworkFact("目标", target)],
                Suggestion: "该步骤仅在 Windows 上可用。");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "tracert.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("-d");
        process.StartInfo.ArgumentList.Add("-h");
        process.StartInfo.ArgumentList.Add(maxHops.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-w");
        process.StartInfo.ArgumentList.Add(((int)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add(target);

        using var timeoutCts = new CancellationTokenSource(timeout * (maxHops + 1));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var raw = string.Join(Environment.NewLine, new[] { stdout.Trim(), stderr.Trim() }
                .Where(static s => !string.IsNullOrWhiteSpace(s)));

            var hops = ParseTracert(stdout);
            var reached = hops.Any(static h => h.Value != "无响应/超时");

            return new TracerouteObservation(
                reached ? NetworkObservationStatus.Passed : NetworkObservationStatus.Unknown,
                reached
                    ? $"路径探测完成，共识别 {hops.Count} 跳。"
                    : "路径探测未得到明确的最终目标回应；中间跳点无响应不能证明目标离线。",
                hops,
                raw,
                reached ? null : "可尝试 TCP 端口或应用层协议验证；中间设备可能过滤 ICMP。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new TracerouteObservation(
                NetworkObservationStatus.TimedOut,
                "Traceroute 超过整体超时，已中止。",
                [new NetworkFact("目标", target)],
                Suggestion: "缩小最大跳数或提高超时后重试。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException
                                      or IOException)
        {
            return new TracerouteObservation(
                NetworkObservationStatus.Failed,
                $"无法启动系统 tracert：{ex.Message}",
                [new NetworkFact("目标", target)],
                Suggestion: "确认系统路径中存在 tracert.exe，并检查进程启动权限。");
        }
    }

    private static IReadOnlyList<NetworkFact> ParseTracert(string output)
    {
        var facts = new List<NetworkFact>();

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = TracertHopPattern().Match(line.Trim());

            if (!match.Success)
            {
                continue;
            }

            var hop = match.Groups["hop"].Value;
            var host = match.Groups["host"].Value.Trim();
            var isResponse = match.Groups["host"].Success && host != "*";

            facts.Add(new NetworkFact($"第 {hop} 跳", isResponse ? host : "无响应/超时"));
        }

        return facts;
    }

    /// <summary>
    /// 发送 Ping；IPv4/IPv6 字面量走带超时的重载，名称先解析再探测。
    /// </summary>
    /// <remarks>
    /// .NET 的 <c>SendPingAsync(string, int, CancellationToken)</c> 重载并不存在，
    /// 名称重载也不接受取消令牌。先解析成地址既能统一使用可取消的重载，
    /// 也能把 DNS 失败与 ICMP 无响应区分开。
    /// </remarks>
    private static async Task<PingReply> SendPingAsync(
        Ping ping,
        string target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(target, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
        {
            throw new PingException("目标名称未解析到任何地址。");
        }

        var address = addresses.FirstOrDefault(static a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses[0];

        return await ping
            .SendPingAsync(address, timeout, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<(string Destination, string Gateway, string Interface)> ReadIPv4Routes()
    {
        var routes = new List<(string, string, string)>();

        if (!OperatingSystem.IsWindows())
        {
            return routes;
        }

        try
        {
            var size = 0;
            _ = GetIpForwardTable(IntPtr.Zero, ref size, order: false);

            if (size <= 0)
            {
                return routes;
            }

            var buffer = Marshal.AllocHGlobal(size);

            try
            {
                if (GetIpForwardTable(buffer, ref size, order: false) != 0)
                {
                    return routes;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibIpForwardRow>();
                var rowPointer = IntPtr.Add(buffer, sizeof(int));

                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibIpForwardRow>(
                        IntPtr.Add(rowPointer, i * rowSize));

                    routes.Add((
                        UInt32ToIPv4(row.ForwardDest),
                        UInt32ToIPv4(row.ForwardNextHop),
                        row.ForwardIfIndex.ToString(CultureInfo.InvariantCulture)));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return routes;
    }

    private static string UInt32ToIPv4(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, value);
        return new IPAddress(bytes).ToString();
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow
    {
        public uint ForwardDest;
        public uint ForwardMask;
        public uint ForwardPolicy;
        public uint ForwardNextHop;
        public uint ForwardIfIndex;
        public uint ForwardType;
        public uint ForwardProto;
        public uint ForwardAge;
        public uint ForwardNextHopAs;
        public uint ForwardMetric1;
        public uint ForwardMetric2;
        public uint ForwardMetric3;
        public uint ForwardMetric4;
        public uint ForwardMetric5;
    }

    [GeneratedRegex(@"^\s*(?<hop>\d+)\s+(?:(?:[\d<]+\s*ms|\*)\s+){1,3}(?<host>\S+)")]
    private static partial Regex TracertHopPattern();
}
