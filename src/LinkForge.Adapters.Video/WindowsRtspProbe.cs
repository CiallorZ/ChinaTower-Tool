using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LinkForge.Adapters.Video;

/// <summary>
/// 基于 System.Net.Sockets 的 RTSP 信令探测实现。
/// </summary>
/// <remarks>
/// <para>
/// 这里只做 OPTIONS 与 DESCRIBE；不进行 SETUP/PLAY、不接收 RTP、不启动解码。
/// 因此页面不会出现“黑屏”或“首帧”之类无法由本层证明的结论。
/// </para>
/// <para>
/// 401 是 RTSP 的正常鉴权握手：先发无凭据请求，再按响应中的 Basic 方案重试一次。
/// 本项目不实现 Digest，遇到 Digest-only 服务明确返回失败并提示原因，不伪造成功。
/// </para>
/// </remarks>
public sealed class WindowsRtspProbe : IRtspProbe
{
    private const int MaxResponseBytes = 64 * 1024;

    /// <inheritdoc />
    public async Task<RtspObservation> ProbeAsync(
        RtspEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        var stages = new List<RtspStageObservation>();
        var startedAt = DateTimeOffset.Now;
        var authorization = BuildAuthorization(endpoint.UserInfo);
        var raw = new StringBuilder();

        try
        {
            stages.Add(await ResolveStageAsync(endpoint, linked.Token).ConfigureAwait(false));

            if (stages[0].Status != RtspObservationStatus.Passed)
            {
                return BuildResult(stages, raw, startedAt);
            }

            using var client = new TcpClient();
            await client.ConnectAsync(endpoint.Host, endpoint.Port, linked.Token).ConfigureAwait(false);
            client.NoDelay = true;

            stages.Add(new RtspStageObservation(
                "network",
                "TCP 连接",
                RtspObservationStatus.Passed,
                $"已建立到 {endpoint.Host}:{endpoint.Port} 的 TCP 连接。",
                [new RtspFact("目标", $"{endpoint.Host}:{endpoint.Port}"), new RtspFact("协议", endpoint.Scheme.ToUpperInvariant())]));

            using var stream = client.GetStream();
            var sequence = 1;
            var optionsResponse = await SendRequestAsync(
                    stream,
                    BuildRequest("OPTIONS", BuildRequestTarget(endpoint), sequence++, null),
                    linked.Token)
                .ConfigureAwait(false);
            raw.AppendLine(optionsResponse);

            if (!RtspResponseParser.TryParseResponse(
                    optionsResponse,
                    out var optionsStatus,
                    out var optionsReason,
                    out var optionsHeaders,
                    out _,
                    out var optionsError))
            {
                stages.Add(new RtspStageObservation(
                    "handshake",
                    "RTSP 握手",
                    RtspObservationStatus.Failed,
                    optionsError ?? "RTSP OPTIONS 响应无效。",
                    [],
                    RtspResponseParser.RedactRawResponse(optionsResponse),
                    "确认目标端口确实是 RTSP 服务，而不是 HTTP 或其他协议。"));
                return BuildResult(stages, raw, startedAt);
            }

            if (optionsStatus == 401)
            {
                var authHeader = RtspResponseParser.Header(optionsHeaders, "WWW-Authenticate");
                var schemes = RtspResponseParser.DescribeAuthenticationSchemes(authHeader);

                stages.Add(new RtspStageObservation(
                    "handshake",
                    "RTSP 握手",
                    RtspObservationStatus.Passed,
                    $"RTSP 服务可达，返回 401；服务要求认证（{schemes}）。",
                    RtspResponseParser.DescribeHeaders(optionsHeaders),
                    RtspResponseParser.RedactRawResponse(optionsResponse)) { });

                if (string.IsNullOrEmpty(authorization))
                {
                    stages.Add(new RtspStageObservation(
                        "authentication",
                        "认证",
                        RtspObservationStatus.Failed,
                        "服务要求认证，但 URL 未提供凭据。",
                        [new RtspFact("认证方案", schemes)],
                        null,
                        "把用户名和口令放入 RTSP URL，或改用有凭据的来源后重试。"));
                    return BuildResult(stages, raw, startedAt);
                }

                if (authHeader is null
                    || !authHeader.StartsWith("Basic", StringComparison.OrdinalIgnoreCase))
                {
                    stages.Add(new RtspStageObservation(
                        "authentication",
                        "认证",
                        RtspObservationStatus.Failed,
                        $"服务仅提供 {schemes} 认证；当前只实现 Basic 重试。",
                        [new RtspFact("认证方案", schemes)],
                        null,
                        "请使用支持 Basic 认证的测试地址，或后续版本补充 Digest 支持。"));
                    return BuildResult(stages, raw, startedAt);
                }

                stages.Add(new RtspStageObservation(
                    "authentication",
                    "认证",
                    RtspObservationStatus.Passed,
                    "已识别 Basic 认证方案，将使用 URL 中的凭据重试。",
                    [new RtspFact("认证方案", "Basic")]));

                var describeResponse = await SendRequestAsync(
                        stream,
                        BuildRequest("DESCRIBE", BuildRequestTarget(endpoint), sequence++, authorization),
                        linked.Token)
                    .ConfigureAwait(false);
                raw.AppendLine(describeResponse);
                return ParseDescribe(endpoint, stages, raw, startedAt, describeResponse);
            }

            if (optionsStatus is < 200 or >= 300)
            {
                stages.Add(new RtspStageObservation(
                    "handshake",
                    "RTSP 握手",
                    RtspObservationStatus.Failed,
                    $"RTSP OPTIONS 返回 {optionsStatus} {optionsReason}。",
                    RtspResponseParser.DescribeHeaders(optionsHeaders),
                    RtspResponseParser.RedactRawResponse(optionsResponse),
                    "检查 RTSP 服务状态、URL 路径与服务器配置。"));
                return BuildResult(stages, raw, startedAt);
            }

            stages.Add(new RtspStageObservation(
                "handshake",
                "RTSP 握手",
                RtspObservationStatus.Passed,
                $"RTSP OPTIONS 返回 {optionsStatus} {optionsReason}。",
                RtspResponseParser.DescribeHeaders(optionsHeaders),
                RtspResponseParser.RedactRawResponse(optionsResponse)));

            var describe = await SendRequestAsync(
                    stream,
                    BuildRequest("DESCRIBE", BuildRequestTarget(endpoint), sequence++, authorization),
                    linked.Token)
                .ConfigureAwait(false);
            raw.AppendLine(describe);
            return ParseDescribe(endpoint, stages, raw, startedAt, describe);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stages.Add(new RtspStageObservation(
                "handshake",
                "RTSP 握手",
                RtspObservationStatus.TimedOut,
                $"RTSP 信令超过 {timeout.TotalSeconds:0.#} 秒。",
                [],
                null,
                "可提高超时或确认目标服务是否响应 RTSP。"));
            return BuildResult(stages, raw, startedAt);
        }
        catch (SocketException ex)
        {
            stages.Add(new RtspStageObservation(
                "network",
                "TCP 连接",
                RtspObservationStatus.Failed,
                $"无法建立 TCP 连接：{ex.SocketErrorCode}。",
                [],
                null,
                "检查主机、端口、防火墙与 RTSP 服务是否监听。"));
            return BuildResult(stages, raw, startedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stages.Add(new RtspStageObservation(
                "handshake",
                "RTSP 握手",
                RtspObservationStatus.Failed,
                $"RTSP 信令失败：{ex.GetType().Name} — {ex.Message}",
                [],
                null,
                "确认目标地址与协议实现；必要时先用 TCP 端口检测。"));
            return BuildResult(stages, raw, startedAt);
        }
    }

    private static async Task<RtspStageObservation> ResolveStageAsync(
        RtspEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(endpoint.Host, out var address))
        {
            return new RtspStageObservation(
                "network",
                "DNS 解析",
                RtspObservationStatus.Passed,
                "目标是 IP 地址，无需 DNS 解析。",
                [new RtspFact("地址", address.ToString())]);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken)
                .ConfigureAwait(false);
            var text = string.Join(", ", addresses.Select(static a => a.ToString()).Distinct());

            return new RtspStageObservation(
                "network",
                "DNS 解析",
                RtspObservationStatus.Passed,
                $"已解析 {endpoint.Host}。",
                [new RtspFact("名称", endpoint.Host), new RtspFact("地址", text)]);
        }
        catch (SocketException ex)
        {
            return new RtspStageObservation(
                "network",
                "DNS 解析",
                RtspObservationStatus.Failed,
                $"DNS 解析失败：{ex.SocketErrorCode}。",
                [new RtspFact("名称", endpoint.Host)],
                null,
                "检查 DNS、网络连接与主机名拼写。");
        }
    }

    private static RtspObservation ParseDescribe(
        RtspEndpoint endpoint,
        List<RtspStageObservation> stages,
        StringBuilder raw,
        DateTimeOffset startedAt,
        string response)
    {
        if (!RtspResponseParser.TryParseResponse(
                response,
                out var status,
                out var reason,
                out var headers,
                out var body,
                out var error))
        {
            stages.Add(new RtspStageObservation(
                "sdp",
                "SDP 解析",
                RtspObservationStatus.Failed,
                error ?? "DESCRIBE 响应无效。",
                [],
                RtspResponseParser.RedactRawResponse(response),
                "确认服务器是否返回标准 RTSP/SDP。"));
            return BuildResult(stages, raw, startedAt);
        }

        if (status == 401)
        {
            var schemes = RtspResponseParser.DescribeAuthenticationSchemes(
                RtspResponseParser.Header(headers, "WWW-Authenticate"));
            stages.Add(new RtspStageObservation(
                "authentication",
                "认证",
                RtspObservationStatus.Failed,
                $"DESCRIBE 返回 401，认证未通过（{schemes}）。",
                [new RtspFact("认证方案", schemes)],
                RtspResponseParser.RedactRawResponse(response),
                "核对用户名与口令；Digest-only 服务暂不支持。"));
            return BuildResult(stages, raw, startedAt);
        }

        if (status is < 200 or >= 300)
        {
            stages.Add(new RtspStageObservation(
                "sdp",
                "SDP 解析",
                RtspObservationStatus.Failed,
                $"DESCRIBE 返回 {status} {reason}。",
                RtspResponseParser.DescribeHeaders(headers),
                RtspResponseParser.RedactRawResponse(response),
                "检查 URL 路径、设备通道号与服务器配置。"));
            return BuildResult(stages, raw, startedAt);
        }

        stages.Add(new RtspStageObservation(
            "handshake",
            "RTSP DESCRIBE",
            RtspObservationStatus.Passed,
            $"DESCRIBE 返回 {status} {reason}。",
            RtspResponseParser.DescribeHeaders(headers),
            RtspResponseParser.RedactRawResponse(response)));

        var sdp = RtspResponseParser.ParseSdp(body);

        if (!sdp.IsValid)
        {
            stages.Add(new RtspStageObservation(
                "sdp",
                "SDP 解析",
                RtspObservationStatus.Failed,
                "响应体不是有效 SDP：缺少 v=/t= 或媒体行 m=。",
                [new RtspFact("会话名", string.IsNullOrWhiteSpace(sdp.SessionName) ? "未提供" : sdp.SessionName)],
                RtspResponseParser.RedactRawResponse(response),
                "确认服务器返回标准 SDP；部分设备需要先 SETUP 才会返回媒体信息。"));
            return BuildResult(stages, raw, startedAt);
        }

        var facts = new List<RtspFact>
        {
            new("会话名", string.IsNullOrWhiteSpace(sdp.SessionName) ? "未提供" : sdp.SessionName),
            new("媒体流", sdp.Media.Count.ToString()),
            new("编码", sdp.Codecs.Count == 0 ? "未标注" : string.Join(", ", sdp.Codecs))
        };

        if (sdp.Controls.Count > 0)
        {
            facts.Add(new RtspFact("Control", string.Join(", ", sdp.Controls)));
        }

        stages.Add(new RtspStageObservation(
            "sdp",
            "SDP 解析",
            RtspObservationStatus.Passed,
            $"已解析 {sdp.Media.Count} 条媒体流和 {sdp.Codecs.Count} 个编码信息。",
            facts,
            null));

        stages.Add(new RtspStageObservation(
            "media",
            "RTP / 解码 / 首帧",
            RtspObservationStatus.Skipped,
            "本阶段只验证 RTSP 信令与 SDP，未执行 SETUP/PLAY、RTP 接收或媒体解码。",
            [],
            null,
            "媒体接收与首帧需要独立播放组件；当前不以静态图片或假帧冒充预览。"));

        return BuildResult(stages, raw, startedAt);
    }

    private static RtspObservation BuildResult(
        IReadOnlyList<RtspStageObservation> stages,
        StringBuilder raw,
        DateTimeOffset startedAt)
    {
        var failed = stages.FirstOrDefault(static s =>
            s.Status is RtspObservationStatus.Failed or RtspObservationStatus.TimedOut);
        var unknown = stages.FirstOrDefault(static s => s.Status == RtspObservationStatus.Unknown);
        var status = failed?.Status
            ?? (unknown is not null ? RtspObservationStatus.Unknown : RtspObservationStatus.Passed);
        var summary = failed?.Summary
            ?? unknown?.Summary
            ?? "RTSP 信令诊断完成。";
        var facts = stages
            .SelectMany(static s => s.Facts)
            .Distinct()
            .ToArray();

        return new RtspObservation(
            status,
            summary,
            stages,
            facts,
            failed?.Suggestion ?? unknown?.Suggestion,
            RtspResponseParser.RedactRawResponse(raw.ToString()));
    }

    private static string BuildRequest(
        string method,
        string url,
        int sequence,
        string? authorization)
    {
        var builder = new StringBuilder();
        builder.Append(method).Append(' ').Append(url).Append(" RTSP/1.0\r\n");
        builder.Append("CSeq: ").Append(sequence).Append("\r\n");
        builder.Append("User-Agent: LinkForge/0.1\r\n");

        if (method == "DESCRIBE")
        {
            builder.Append("Accept: application/sdp\r\n");
        }

        if (!string.IsNullOrEmpty(authorization))
        {
            builder.Append("Authorization: ").Append(authorization).Append("\r\n");
        }

        builder.Append("\r\n");
        return builder.ToString();
    }

    private static string BuildRequestTarget(RtspEndpoint endpoint)
    {
        var host = IPAddress.TryParse(endpoint.Host, out var address)
                   && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{endpoint.Host}]"
            : endpoint.Host;

        return $"{endpoint.Scheme}://{host}:{endpoint.Port}{endpoint.PathAndQuery}";
    }

    private static string? BuildAuthorization(string? userInfo)
    {
        if (string.IsNullOrEmpty(userInfo))
        {
            return null;
        }

        var decoded = Uri.UnescapeDataString(userInfo);
        var colon = decoded.IndexOf(':');
        var user = colon >= 0 ? decoded[..colon] : decoded;
        var password = colon >= 0 ? decoded[(colon + 1)..] : string.Empty;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

        return $"Basic {token}";
    }

    private static async Task<string> SendRequestAsync(
        NetworkStream stream,
        string request,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[MaxResponseBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;

            if (ContainsCompleteResponse(buffer, total))
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static bool ContainsCompleteResponse(byte[] buffer, int length)
    {
        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

        if (headerEnd < 0)
        {
            return false;
        }

        var header = text[..headerEnd];
        var contentLengthLine = header
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(static line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));

        if (contentLengthLine is null)
        {
            return true;
        }

        var value = contentLengthLine[(contentLengthLine.IndexOf(':') + 1)..].Trim();

        return int.TryParse(value, out var contentLength)
               && length >= headerEnd + 4 + contentLength;
    }
}
