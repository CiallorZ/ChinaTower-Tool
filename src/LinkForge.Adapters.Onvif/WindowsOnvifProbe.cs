using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LinkForge.Adapters.Onvif;

/// <summary>
/// Windows ONVIF 标准探测实现：UDP WS-Discovery + SOAP over HTTP(S)。
/// </summary>
/// <remarks>
/// 本实现不关闭 TLS 校验，不保存凭据，不自动扩大发现范围。
/// 发现使用固定 WS-Discovery 组播地址；未发现设备时返回“未知”，
/// 因为组播被过滤并不等于网络中没有 ONVIF 设备。
/// </remarks>
public sealed class WindowsOnvifProbe : IOnvifProbe
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly IPEndPoint DiscoveryEndpoint = new(IPAddress.Parse("239.255.255.250"), 3702);

    /// <inheritdoc />
    public async Task<OnvifDiscoveryObservation> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var devices = new Dictionary<string, OnvifDiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        var raw = new StringBuilder();

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        var payload = Encoding.UTF8.GetBytes(OnvifXml.BuildProbeEnvelope());

        try
        {
            await udp.SendAsync(payload, DiscoveryEndpoint, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            while (!linked.IsCancellationRequested)
            {
                try
                {
                    var response = await udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
                    var text = Encoding.UTF8.GetString(response.Buffer);

                    try
                    {
                        foreach (var device in ParseDiscoveryResponse(text))
                        {
                            devices.TryAdd(device.Endpoint, device);
                        }

                        raw.AppendLine(text);
                    }
                    catch (XmlException)
                    {
                        // 单个伪造或损坏的组播包不能中断整个发现窗口。
                        // 原始包可能包含任意内容，因此坏包不进入 UI 原始输出。
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or FormatException or UriFormatException)
                    {
                        // 异常文档结构或非法 URI 同样只影响当前包。
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                         && !cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new OnvifDiscoveryObservation(
                OnvifObservationStatus.Failed,
                $"WS-Discovery 发送失败：{ex.SocketErrorCode}。",
                [],
                Suggestion: "检查本机网络接口、组播策略和防火墙后重试。");
        }

        var ordered = devices.Values
            .OrderBy(static device => device.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ordered.Length == 0)
        {
            return new OnvifDiscoveryObservation(
                OnvifObservationStatus.Unknown,
                "WS-Discovery 在超时内未收到设备响应。",
                [],
                raw.ToString(),
                "组播可能被网络策略过滤；可改用设备服务 URL 直接查询。不要把“未发现”解释为网络中没有 ONVIF 设备。");
        }

        return new OnvifDiscoveryObservation(
            OnvifObservationStatus.Passed,
            $"发现 {ordered.Length} 个 ONVIF 视频发送端。",
            ordered,
            raw.ToString());
    }

    /// <inheritdoc />
    public Task<OnvifOperationObservation> GetDeviceInformationAsync(
        OnvifEndpoint endpoint,
        OnvifCredentials? credentials,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            endpoint,
            credentials,
            timeout,
            "http://www.onvif.org/ver10/device/wsdl/GetDeviceInformation",
            OnvifXml.BuildGetDeviceInformationBody(),
            ParseDeviceInformation,
            cancellationToken);

    /// <inheritdoc />
    public Task<OnvifOperationObservation> GetCapabilitiesAsync(
        OnvifEndpoint endpoint,
        OnvifCredentials? credentials,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            endpoint,
            credentials,
            timeout,
            "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
            OnvifXml.BuildGetCapabilitiesBody(),
            ParseCapabilities,
            cancellationToken);

    /// <inheritdoc />
    public async Task<OnvifOperationObservation> GetMediaProfilesAsync(
        OnvifEndpoint endpoint,
        OnvifCredentials? credentials,
        string? profileToken,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var profiles = await ExecuteAsync(
                endpoint,
                credentials,
                timeout,
                "http://www.onvif.org/ver10/media/wsdl/GetProfiles",
                OnvifXml.BuildGetProfilesBody(),
                ParseProfiles,
                cancellationToken)
            .ConfigureAwait(false);

        if (profiles.Status is OnvifObservationStatus.Failed or OnvifObservationStatus.TimedOut)
        {
            return profiles;
        }

        var tokens = profiles.Facts
            .Where(static fact => fact.Label.StartsWith("Profile Token ", StringComparison.Ordinal))
            .Select(static fact => fact.Value)
            .ToArray();

        var selected = string.IsNullOrWhiteSpace(profileToken)
            ? tokens.FirstOrDefault()
            : profileToken.Trim();

        if (string.IsNullOrWhiteSpace(selected))
        {
            return profiles with
            {
                Status = OnvifObservationStatus.Warning,
                Summary = "设备没有返回可用的媒体 Profile。",
                Suggestion = "确认设备已启用媒体服务并配置视频通道。"
            };
        }

        if (!tokens.Contains(selected, StringComparer.Ordinal))
        {
            return profiles with
            {
                Status = OnvifObservationStatus.Warning,
                Summary = $"指定的 Profile Token“{selected}”不在设备返回的 Profile 列表中。",
                Suggestion = "留空 Profile Token 以使用设备返回的第一个 Profile。"
            };
        }

        var stream = await ExecuteAsync(
                endpoint,
                credentials,
                timeout,
                "http://www.onvif.org/ver10/media/wsdl/GetStreamUri",
                OnvifXml.BuildGetStreamUriBody(selected),
                xml => ParseStreamUri(xml, selected),
                cancellationToken)
            .ConfigureAwait(false);

        var facts = profiles.Facts.Concat(stream.Facts).ToArray();

        return stream.Status == OnvifObservationStatus.Passed
            ? new OnvifOperationObservation(
                OnvifObservationStatus.Passed,
                $"已读取 {tokens.Length} 个媒体 Profile，并获得 Profile“{selected}”的 RTSP Stream URI。",
                facts,
                stream.RawOutput,
                stream.Suggestion)
            : stream with { Facts = facts };
    }

    private static async Task<OnvifOperationObservation> ExecuteAsync(
        OnvifEndpoint endpoint,
        OnvifCredentials? credentials,
        TimeSpan timeout,
        string action,
        string body,
        Func<string, OnvifOperationObservation> parser,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = timeout
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.ServiceUri);
        var xml = OnvifXml.BuildEnvelope(body, credentials);
        var content = new StringContent(xml, Encoding.UTF8, "application/soap+xml");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml")
        {
            CharSet = "utf-8"
        };
        content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action", $"\"{action}\""));
        request.Content = content;
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{action}\"");

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new OnvifOperationObservation(
                    OnvifObservationStatus.Failed,
                    "设备返回 HTTP 401。",
                    [new OnvifFact("HTTP", "401 Unauthorized")],
                    Suggestion: "核对凭据；当前版本仅发送 ONVIF WS-Security UsernameToken，不提供 HTTP Basic/Digest 或关闭 TLS 校验的选项。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new OnvifOperationObservation(
                    OnvifObservationStatus.Failed,
                    $"设备返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。",
                    [new OnvifFact("HTTP", $"{(int)response.StatusCode} {response.ReasonPhrase}")],
                    Suggestion: "确认设备服务 URL、端口和 ONVIF 服务是否启用。");
            }

            var responseText = await ReadLimitedAsync(response, linked.Token).ConfigureAwait(false);

            try
            {
                return parser(responseText);
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException or FormatException)
            {
                return new OnvifOperationObservation(
                    OnvifObservationStatus.Failed,
                    $"设备响应不是可解析的 ONVIF SOAP：{ex.Message}",
                    [new OnvifFact("HTTP", $"{(int)response.StatusCode}")],
                    Suggestion: "确认 URL 指向 ONVIF device service，并检查设备固件兼容性。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new OnvifOperationObservation(
                OnvifObservationStatus.TimedOut,
                $"ONVIF 请求超过 {timeout.TotalSeconds:0.#} 秒。",
                [],
                Suggestion: "检查设备可达性，或适当提高超时后重试。");
        }
        catch (HttpRequestException ex)
        {
            return new OnvifOperationObservation(
                OnvifObservationStatus.Failed,
                $"ONVIF HTTP 请求失败：{ex.Message}",
                [],
                Suggestion: "检查 DNS、TCP 端口、TLS 证书和设备服务地址。");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return new OnvifOperationObservation(
                OnvifObservationStatus.Failed,
                $"ONVIF 响应处理失败：{ex.Message}",
                [],
                Suggestion: "确认设备返回标准 SOAP 响应，并检查响应大小是否异常。");
        }
    }

    private static async Task<string> ReadLimitedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidOperationException(
                    $"响应超过 {MaxResponseBytes / 1024 / 1024} MiB 上限。");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static IReadOnlyList<OnvifDiscoveredDevice> ParseDiscoveryResponse(string xml)
    {
        var document = OnvifXml.Parse(xml);
        var results = new List<OnvifDiscoveredDevice>();

        foreach (var match in document
                     .Descendants()
                     .Where(static element => element.Name.LocalName == "ProbeMatch"))
        {
            var addresses = OnvifXml.ChildValue(match, "XAddrs")?
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(static value => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                       || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? [];

            if (addresses.Length == 0)
            {
                continue;
            }

            var scopesText = OnvifXml.ChildValue(match, "Scopes") ?? string.Empty;
            var scopes = scopesText
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var name = scopes
                .FirstOrDefault(static scope =>
                    scope.StartsWith("onvif://www.onvif.org/name/", StringComparison.OrdinalIgnoreCase))
                ?.Split('/')
                .LastOrDefault();
            var types = OnvifXml.ChildValue(match, "Types")?
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                ?? [];

            foreach (var address in addresses)
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme is not ("http" or "https")
                    || !string.IsNullOrEmpty(endpoint.UserInfo))
                {
                    continue;
                }

                results.Add(new OnvifDiscoveredDevice(
                    endpoint.AbsoluteUri,
                    string.IsNullOrWhiteSpace(name) ? null : Uri.UnescapeDataString(name),
                    scopes,
                    types));
            }
        }

        return results;
    }

    private static OnvifOperationObservation ParseDeviceInformation(string xml)
    {
        var document = OnvifXml.Parse(xml);
        var response = document
            .Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "GetDeviceInformationResponse");

        if (response is null)
        {
            return SoapFault(document, "设备未返回 GetDeviceInformationResponse。");
        }

        var facts = new[]
        {
            new OnvifFact("厂商", OnvifXml.ChildValue(response, "Manufacturer") ?? "未提供"),
            new OnvifFact("型号", OnvifXml.ChildValue(response, "Model") ?? "未提供"),
            new OnvifFact("固件", OnvifXml.ChildValue(response, "FirmwareVersion") ?? "未提供"),
            new OnvifFact("序列号", OnvifXml.ChildValue(response, "SerialNumber") ?? "未提供"),
            new OnvifFact("硬件 ID", OnvifXml.ChildValue(response, "HardwareId") ?? "未提供")
        };

        return new OnvifOperationObservation(
            OnvifObservationStatus.Passed,
            $"已读取设备信息：{facts[0].Value} / {facts[1].Value}。",
            facts,
            xml);
    }

    private static OnvifOperationObservation ParseCapabilities(string xml)
    {
        var document = OnvifXml.Parse(xml);
        var response = document
            .Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "GetCapabilitiesResponse");

        if (response is null)
        {
            return SoapFault(document, "设备未返回 GetCapabilitiesResponse。");
        }

        var capabilities = response
            .Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "Capabilities");

        if (capabilities is null)
        {
            return new OnvifOperationObservation(
                OnvifObservationStatus.Unknown,
                "设备响应中没有 Capabilities 节点。",
                [],
                xml,
                "确认设备 ONVIF 固件与接口版本。");
        }

        var facts = capabilities
            .Elements()
            .Select(static element => new OnvifFact(
                element.Name.LocalName,
                string.IsNullOrWhiteSpace(element.Attribute("XAddr")?.Value)
                    ? "已声明"
                    : element.Attribute("XAddr")!.Value))
            .ToArray();

        return new OnvifOperationObservation(
            facts.Length == 0 ? OnvifObservationStatus.Unknown : OnvifObservationStatus.Passed,
            facts.Length == 0
                ? "设备未声明可用服务能力。"
                : $"设备声明 {facts.Length} 类 ONVIF 服务能力。",
            facts,
            xml);
    }

    private static OnvifOperationObservation ParseProfiles(string xml)
    {
        var document = OnvifXml.Parse(xml);
        var response = document
            .Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "GetProfilesResponse");

        if (response is null)
        {
            return SoapFault(document, "设备未返回 GetProfilesResponse。");
        }

        var facts = new List<OnvifFact>();
        var profiles = response
            .Descendants()
            .Where(static element => element.Name.LocalName == "Profiles")
            .ToArray();

        for (var index = 0; index < profiles.Length; index++)
        {
            var profile = profiles[index];
            var token = profile.Attribute("token")?.Value?.Trim();
            var name = OnvifXml.ChildValue(profile, "Name") ?? $"Profile {index + 1}";
            var encoding = profile
                .Descendants()
                .FirstOrDefault(static element => element.Name.LocalName == "Encoding")
                ?.Value;
            var resolution = profile
                .Descendants()
                .FirstOrDefault(static element => element.Name.LocalName == "Resolution");
            var width = resolution?
                .Descendants()
                .FirstOrDefault(static element => element.Name.LocalName == "Width")
                ?.Value;
            var height = resolution?
                .Descendants()
                .FirstOrDefault(static element => element.Name.LocalName == "Height")
                ?.Value;
            var fps = profile
                .Descendants()
                .FirstOrDefault(static element => element.Name.LocalName == "FrameRateLimit")
                ?.Value;

            if (!string.IsNullOrWhiteSpace(token))
            {
                facts.Add(new OnvifFact($"Profile Token {index + 1}", token));
            }

            facts.Add(new OnvifFact($"Profile {index + 1}", name));

            if (!string.IsNullOrWhiteSpace(encoding))
            {
                facts.Add(new OnvifFact($"Profile {index + 1} 编码", encoding));
            }

            if (!string.IsNullOrWhiteSpace(width) && !string.IsNullOrWhiteSpace(height))
            {
                facts.Add(new OnvifFact($"Profile {index + 1} 分辨率", $"{width} × {height}"));
            }

            if (!string.IsNullOrWhiteSpace(fps))
            {
                facts.Add(new OnvifFact($"Profile {index + 1} 帧率", fps));
            }
        }

        return new OnvifOperationObservation(
            profiles.Length == 0 ? OnvifObservationStatus.Warning : OnvifObservationStatus.Passed,
            profiles.Length == 0
                ? "设备未返回媒体 Profile。"
                : $"设备返回 {profiles.Length} 个媒体 Profile。",
            facts,
            xml,
            profiles.Length == 0 ? "确认设备已配置视频通道和媒体服务。" : null);
    }

    private static OnvifOperationObservation ParseStreamUri(string xml, string profileToken)
    {
        var document = OnvifXml.Parse(xml);
        var response = document
            .Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "GetStreamUriResponse");

        if (response is null)
        {
            return SoapFault(document, "设备未返回 GetStreamUriResponse。");
        }

        var uri = OnvifXml.ChildValue(response, "Uri");

        if (string.IsNullOrWhiteSpace(uri))
        {
            return new OnvifOperationObservation(
                OnvifObservationStatus.Warning,
                "设备返回的 Stream URI 为空。",
                [new OnvifFact("Profile Token", profileToken)],
                xml,
                "在设备 Web 界面确认媒体 Profile 已启用。");
        }

        return new OnvifOperationObservation(
            OnvifObservationStatus.Passed,
            "已获得 RTSP Stream URI；该结果不代表视频已经播放。",
            [
                new OnvifFact("Profile Token", profileToken),
                new OnvifFact("RTSP Stream URI", uri)
            ],
            xml);
    }

    private static OnvifOperationObservation SoapFault(XDocument document, string fallback)
    {
        var fault = document
            .Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "Fault");
        var reason = fault is null
            ? fallback
            : OnvifXml.ChildValue(fault, "Text")
              ?? OnvifXml.ChildValue(fault, "Reason")
              ?? fallback;

        return new OnvifOperationObservation(
            OnvifObservationStatus.Failed,
            $"ONVIF SOAP 失败：{reason}",
            [],
            document.ToString(),
            "核对凭据、服务 URL 与设备 ONVIF 实现。");
    }
}
