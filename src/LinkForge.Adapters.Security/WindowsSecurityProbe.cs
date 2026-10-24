using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LinkForge.Adapters.Security;

/// <summary>基于 .NET 原生 TCP、SslStream 与 X.509 校验的安全基线实现。</summary>
/// <remarks>
/// <para>
/// 只检查用户明确列出的端口。TLS 握手始终执行默认证书校验：
/// 自定义回调仅记录校验错误用于展示，遇到任何错误仍返回 <c>false</c>，
/// 不会把无效证书当作成功连接。
/// </para>
/// <para>
/// 端口开放、关闭或过滤是网络事实，不等于漏洞结论；本实现不会执行漏洞利用、
/// 口令爆破、规避检测或范围外扫描。
/// </para>
/// </remarks>
public sealed class WindowsSecurityProbe : ISecurityProbe
{
    private const int MaxConcurrency = 4;

    /// <inheritdoc />
    public async Task<SecurityBaselineObservation> RunAsync(
        SecurityBaselineRequest request,
        IProgress<SecurityProbeProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Target);

        if (request.Ports.Count == 0)
        {
            return Failed("未提供任何 TCP 端口。", "请显式输入最多 32 个检查端口。");
        }

        var addresses = await ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);

        if (addresses.Count == 0)
        {
            return Failed("目标名称未解析到任何地址。", "检查 DNS、名称拼写与当前网络连接。");
        }

        var address = addresses.FirstOrDefault(static item => item.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses[0];
        var tlsPorts = request.TlsPorts.ToHashSet();
        var concurrency = Math.Clamp(request.Concurrency, 1, MaxConcurrency);
        var delay = request.DelayBetweenChecks < TimeSpan.Zero ? TimeSpan.Zero : request.DelayBetweenChecks;
        using var gate = new SemaphoreSlim(concurrency, concurrency);

        var completed = 0;
        var tasks = request.Ports.Select((port, index) => CheckPortThrottledAsync(
            request,
            address,
            port,
            tlsPorts.Contains(port),
            index,
            delay,
            gate,
            cancellationToken,
            () =>
            {
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new SecurityProbeProgress(
                    current,
                    request.Ports.Count,
                    $"已完成 {current}/{request.Ports.Count} 个端口检查。"));
            })).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        var ports = tasks.Select(static task => task.Result).ToArray();
        return BuildObservation(request, address, ports);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string target,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out var literal))
        {
            return [literal];
        }

        try
        {
            return await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private static async Task<SecurityPortResult> CheckPortThrottledAsync(
        SecurityBaselineRequest request,
        IPAddress address,
        int port,
        bool checkTls,
        int index,
        TimeSpan delay,
        SemaphoreSlim gate,
        CancellationToken userToken,
        Action onCompleted)
    {
        try
        {
            if (delay > TimeSpan.Zero && index > 0)
            {
                await Task.Delay(delay * index, userToken).ConfigureAwait(false);
            }

            await gate.WaitAsync(userToken).ConfigureAwait(false);

            try
            {
                return await CheckPortAsync(
                        request.Target,
                        address,
                        port,
                        checkTls,
                        request.Timeout,
                        userToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SecurityPortResult(
                port,
                SecurityPortState.Filtered,
                $"TCP {port} 在超时内未建立连接，结果按“过滤/无响应”记录。",
                request.Timeout);
        }
        finally
        {
            onCompleted();
        }
    }

    private static async Task<SecurityPortResult> CheckPortAsync(
        string target,
        IPAddress address,
        int port,
        bool checkTls,
        TimeSpan timeout,
        CancellationToken userToken)
    {
        var watch = Stopwatch.StartNew();

        using var client = new TcpClient(address.AddressFamily);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, timeoutCts.Token);

        try
        {
            await client.ConnectAsync(address, port, linked.Token).ConfigureAwait(false);
            watch.Stop();

            var tls = checkTls
                ? await CheckTlsAsync(target, client, port, timeout, userToken).ConfigureAwait(false)
                : null;

            return new SecurityPortResult(
                port,
                SecurityPortState.Open,
                tls is null
                    ? $"TCP {port} 开放。"
                    : $"TCP {port} 开放；{tls.Summary}",
                watch.Elapsed,
                tls);
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            watch.Stop();

            return new SecurityPortResult(
                port,
                SecurityPortState.Filtered,
                $"TCP {port} 在 {timeout.TotalSeconds:0.#} 秒内未建立连接，可能被防火墙过滤。",
                watch.Elapsed);
        }
        catch (SocketException ex)
        {
            watch.Stop();
            var state = ex.SocketErrorCode == SocketError.ConnectionRefused
                ? SecurityPortState.Closed
                : SecurityPortState.Unknown;
            var text = state == SecurityPortState.Closed
                ? $"TCP {port} 明确拒绝连接，端口按关闭记录。"
                : $"TCP {port} 未能判定（{ex.SocketErrorCode}）。";

            return new SecurityPortResult(port, state, text, watch.Elapsed);
        }
    }

    private static async Task<SecurityTlsResult> CheckTlsAsync(
        string target,
        TcpClient client,
        int port,
        TimeSpan timeout,
        CancellationToken userToken)
    {
        SslPolicyErrors capturedErrors = SslPolicyErrors.None;
        X509ChainStatusFlags capturedChainErrors = X509ChainStatusFlags.NoError;
        X509Certificate2? capturedCertificate = null;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, timeoutCts.Token);

        try
        {
            using var ssl = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, chain, errors) =>
                {
                    capturedErrors = errors;
                    capturedChainErrors = chain?.ChainStatus
                        .Aggregate(
                            X509ChainStatusFlags.NoError,
                            static (current, status) => current | status.Status)
                        ?? X509ChainStatusFlags.NoError;
                    capturedCertificate = CloneCertificate(certificate);

                    // 只记录校验错误，绝不把错误证书当作可信连接。
                    return errors == SslPolicyErrors.None;
                });

            await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = target,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.Online
                    },
                    linked.Token)
                .ConfigureAwait(false);

            var remoteCertificate = CloneCertificate(ssl.RemoteCertificate);

            try
            {
                return BuildTlsSuccess(port, remoteCertificate ?? capturedCertificate, ssl);
            }
            finally
            {
                remoteCertificate?.Dispose();
            }
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new SecurityTlsResult(
                port,
                SecurityTlsState.TimedOut,
                $"TLS {port} 握手超时。",
                [new SecurityFact("TLS 状态", "握手超时", SecurityFactStatus.Unknown)]);
        }
        catch (AuthenticationException) when (capturedErrors != SslPolicyErrors.None)
        {
            return BuildTlsFailure(port, capturedErrors, capturedChainErrors, capturedCertificate);
        }
        catch (IOException)
        {
            if (capturedErrors != SslPolicyErrors.None)
            {
                return BuildTlsFailure(port, capturedErrors, capturedChainErrors, capturedCertificate);
            }

            return new SecurityTlsResult(
                port,
                SecurityTlsState.HandshakeFailed,
                $"TLS {port} 握手未完成。",
                [new SecurityFact("TLS 状态", "握手失败", SecurityFactStatus.Warning)]);
        }
        catch (AuthenticationException)
        {
            return new SecurityTlsResult(
                port,
                SecurityTlsState.HandshakeFailed,
                $"TLS {port} 握手未完成。",
                [new SecurityFact("TLS 状态", "握手失败", SecurityFactStatus.Warning)]);
        }
        finally
        {
            capturedCertificate?.Dispose();
        }
    }

    private static SecurityTlsResult BuildTlsSuccess(
        int port,
        X509Certificate2? certificate,
        SslStream ssl)
    {
        var facts = new List<SecurityFact>
        {
            new("TLS 状态", "握手成功，默认证书校验通过", SecurityFactStatus.Passed),
            new("协议", ssl.SslProtocol.ToString(), SecurityFactStatus.Passed),
            new("加密套件", ssl.NegotiatedCipherSuite.ToString(), SecurityFactStatus.Passed)
        };

        AddCertificateFacts(facts, certificate);

        return new SecurityTlsResult(
            port,
            SecurityTlsState.Valid,
            $"TLS {port} 握手成功，证书通过默认校验。",
            facts);
    }

    private static SecurityTlsResult BuildTlsFailure(
        int port,
        SslPolicyErrors errors,
        X509ChainStatusFlags chainErrors,
        X509Certificate2? certificate)
    {
        var facts = new List<SecurityFact>
        {
            new(
                "证书校验",
                errors == SslPolicyErrors.None ? "握手失败" : errors.ToString(),
                SecurityFactStatus.Warning)
        };

        if (chainErrors != X509ChainStatusFlags.NoError)
        {
            facts.Add(new SecurityFact(
                "证书链",
                chainErrors.ToString(),
                SecurityFactStatus.Warning));
        }

        AddCertificateFacts(facts, certificate);

        return new SecurityTlsResult(
            port,
            SecurityTlsState.CertificateRejected,
            $"TLS {port} 证书未通过默认校验，握手已拒绝。",
            facts);
    }

    private static void AddCertificateFacts(
        ICollection<SecurityFact> facts,
        X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            facts.Add(new SecurityFact("证书", "未返回证书", SecurityFactStatus.Unknown));
            return;
        }

        facts.Add(new SecurityFact("证书主体", certificate.Subject));
        facts.Add(new SecurityFact("证书颁发者", certificate.Issuer));
        facts.Add(new SecurityFact(
            "有效期",
            $"{certificate.NotBefore:yyyy-MM-dd} 至 {certificate.NotAfter:yyyy-MM-dd}"));
        facts.Add(new SecurityFact(
            "签名算法",
            certificate.SignatureAlgorithm.FriendlyName
            ?? certificate.SignatureAlgorithm.Value
            ?? "未知"));
        facts.Add(new SecurityFact("证书指纹", certificate.Thumbprint));
    }

    private static X509Certificate2? CloneCertificate(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return null;
        }

        return X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
    }

    private static SecurityBaselineObservation BuildObservation(
        SecurityBaselineRequest request,
        IPAddress resolvedAddress,
        IReadOnlyList<SecurityPortResult> ports)
    {
        var open = ports.Count(static port => port.State == SecurityPortState.Open);
        var closed = ports.Count(static port => port.State == SecurityPortState.Closed);
        var filtered = ports.Count(static port => port.State == SecurityPortState.Filtered);
        var unknown = ports.Count(static port => port.State == SecurityPortState.Unknown);
        var tlsResults = ports
            .Where(static port => port.Tls is not null)
            .Select(static port => port.Tls!)
            .ToArray();
        var tlsValid = tlsResults.Count(static tls => tls.State == SecurityTlsState.Valid);
        var tlsIssues = tlsResults.Count(static tls =>
            tls.State is SecurityTlsState.CertificateRejected or SecurityTlsState.HandshakeFailed);

        var facts = new List<SecurityFact>
        {
            new("目标", request.Target),
            new("实际解析地址", resolvedAddress.ToString()),
            new("TCP 端口", SecurityPortParser.Format(request.Ports)),
            new("TLS 端口", request.TlsPorts.Count == 0
                ? "未请求"
                : SecurityPortParser.Format(request.TlsPorts)),
            new("范围说明", "仅限用户显式列出的端口，未扫描其他端口或网段")
        };

        var raw = new StringBuilder();

        foreach (var port in ports)
        {
            var stateText = port.State switch
            {
                SecurityPortState.Open => "开放",
                SecurityPortState.Closed => "关闭",
                SecurityPortState.Filtered => "过滤/无响应",
                _ => "未知"
            };
            var portFactStatus = port.State switch
            {
                SecurityPortState.Filtered or SecurityPortState.Unknown => SecurityFactStatus.Unknown,
                _ => SecurityFactStatus.Information
            };

            facts.Add(new SecurityFact($"TCP {port.Port}", stateText, portFactStatus));
            raw.AppendLine($"TCP {port.Port}: {port.Summary}");

            if (port.Tls is not { } tls)
            {
                continue;
            }

            var tlsStatus = tls.State switch
            {
                SecurityTlsState.Valid => SecurityFactStatus.Passed,
                SecurityTlsState.TimedOut => SecurityFactStatus.Unknown,
                _ => SecurityFactStatus.Warning
            };

            facts.Add(new SecurityFact($"TLS {port.Port}", tls.Summary, tlsStatus));
            facts.AddRange(tls.Facts);

            foreach (var fact in tls.Facts)
            {
                raw.AppendLine($"TLS {port.Port} {fact.Label}: {fact.Value}");
            }
        }

        var status = tlsIssues > 0
            ? SecurityObservationStatus.Warning
            : filtered + unknown + tlsResults.Count(static tls => tls.State == SecurityTlsState.TimedOut) > 0
                ? SecurityObservationStatus.Unknown
                : SecurityObservationStatus.Passed;

        var summary = $"TCP 暴露面检查完成：{open} 开放、{closed} 关闭、{filtered} 过滤/无响应、{unknown} 未知；"
                      + (tlsResults.Length == 0
                          ? "未执行 TLS 握手。"
                          : $"TLS 证书通过 {tlsValid} 个，需关注 {tlsIssues} 个。")
                      + "该结论只覆盖指定端口，不代表漏洞扫描或渗透测试。";

        return new SecurityBaselineObservation(
            status,
            summary,
            facts,
            ports,
            raw.ToString().TrimEnd(),
            tlsIssues > 0
                ? "核对证书信任链、主机名、有效期与 TLS 协议；证书告警不等于已确认可利用漏洞。"
                : filtered + unknown > 0
                    ? "过滤或无响应可能来自防火墙、路由或服务策略；无法据此判定主机离线。"
                    : null);
    }

    private static SecurityBaselineObservation Failed(string summary, string suggestion) =>
        new(
            SecurityObservationStatus.Failed,
            summary,
            [],
            [],
            Suggestion: suggestion);
}
