using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace LinkForge.Adapters.Http;

/// <summary>
/// 基于 <see cref="SocketsHttpHandler"/> 的真实 HTTP 传输。
/// </summary>
/// <remarks>
/// 安全边界：
/// <list type="bullet">
///   <item>不提供关闭 TLS 证书校验的选项，也不设置任何危险证书回调。</item>
///   <item>不启用自动 Cookie，避免把浏览器或其他工具的会话带入请求。</item>
///   <item>每次请求使用独立 handler/client，超时、取消和重定向互不影响。</item>
///   <item>响应按字节流读取，先到上限即停止，不把超大响应整体读入内存。</item>
/// </list>
/// </remarks>
public sealed class HttpApiTransport : IHttpApiTransport
{
    private const int MinimumResponseLimit = 16 * 1024;
    private const int MaximumResponseLimit = 50 * 1024 * 1024;

    /// <inheritdoc />
    public async Task<HttpApiResponse> SendAsync(
        HttpApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Validate(request);

        var uri = BuildUri(request);
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = request.FollowRedirects,
            MaxAutomaticRedirections = request.FollowRedirects
                ? Math.Max(1, request.MaxRedirects)
                : 1,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            ConnectTimeout = request.Timeout
        };

        using var client = new HttpClient(handler, disposeHandler: false)
        {
            // 超时由本次请求的 linked CTS 控制，以便区分“用户取消”和“超时”。
            Timeout = Timeout.InfiniteTimeSpan
        };

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        ApplyBody(message, request);
        ApplyHeaders(message, request);
        ApplyAuthentication(message, request);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var (body, bytes, truncated) = await ReadBodyAsync(
                    response,
                    request.MaxResponseBytes,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            stopwatch.Stop();

            return new HttpApiResponse
            {
                Method = request.Method.ToUpperInvariant(),
                FinalUri = response.RequestMessage?.RequestUri ?? uri,
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase ?? string.Empty,
                ResponseHeaders = CopyHeaders(response),
                Body = body,
                Duration = stopwatch.Elapsed,
                IsTruncated = truncated,
                ResponseBytes = bytes
            };
        }
        catch (OperationCanceledException ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (timeoutCts.IsCancellationRequested)
            {
                throw new HttpApiTransportException(
                    HttpApiFailureKind.Timeout,
                    $"请求超过 {FormatTimeout(request.Timeout)}，已停止。",
                    ex);
            }

            throw;
        }
        catch (HttpRequestException ex)
        {
            throw Classify(ex);
        }
        catch (AuthenticationException ex)
        {
            throw new HttpApiTransportException(
                HttpApiFailureKind.Tls,
                "TLS 握手或服务器证书验证失败。LinkForge 不会关闭证书验证来绕过错误。",
                ex);
        }
        catch (IOException ex)
        {
            throw new HttpApiTransportException(
                HttpApiFailureKind.Connection,
                "连接在读取响应时中断。",
                ex);
        }
    }

    private static void Validate(HttpApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Method)
            || !request.Method.All(static c => char.IsLetter(c)))
        {
            throw new HttpApiTransportException(
                HttpApiFailureKind.InvalidRequest,
                "HTTP 方法不能为空，且只能包含字母。");
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new HttpApiTransportException(
                HttpApiFailureKind.InvalidRequest,
                "URL 必须是有效的 http:// 或 https:// 绝对地址。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new HttpApiTransportException(
                HttpApiFailureKind.InvalidRequest,
                "URL 中不能包含用户名或密码；请使用认证字段。");
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new HttpApiTransportException(
                HttpApiFailureKind.InvalidRequest,
                "超时时间必须大于零。");
        }

        if (request.MaxResponseBytes is < MinimumResponseLimit or > MaximumResponseLimit)
        {
            throw new HttpApiTransportException(
                HttpApiFailureKind.InvalidRequest,
                $"响应上限必须在 {MinimumResponseLimit / 1024}–{MaximumResponseLimit / 1024} KB 之间。");
        }

        if (request.FollowRedirects && request.MaxRedirects is < 0 or > 10)
        {
            throw new HttpApiTransportException(
                HttpApiFailureKind.InvalidRequest,
                "最大重定向次数必须在 0–10 之间。");
        }
    }

    private static Uri BuildUri(HttpApiRequest request)
    {
        var builder = new UriBuilder(request.Url);
        var query = new List<string>();

        if (!string.IsNullOrWhiteSpace(builder.Query))
        {
            query.Add(builder.Query.TrimStart('?'));
        }

        foreach (var pair in request.Query)
        {
            query.Add(
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        }

        builder.Query = string.Join('&', query.Where(static value => value.Length > 0));
        return builder.Uri;
    }

    private static void ApplyBody(HttpRequestMessage message, HttpApiRequest request)
    {
        if (request.BodyKind == HttpApiBodyKind.None || request.Body is null)
        {
            return;
        }

        var mediaType = request.BodyKind switch
        {
            HttpApiBodyKind.Json => "application/json",
            HttpApiBodyKind.FormUrlEncoded => "application/x-www-form-urlencoded",
            _ => "text/plain"
        };

        message.Content = new StringContent(request.Body, Encoding.UTF8, mediaType);
    }

    private static void ApplyHeaders(HttpRequestMessage message, HttpApiRequest request)
    {
        foreach (var (name, value) in request.Headers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (message.Headers.TryAddWithoutValidation(name, value))
            {
                continue;
            }

            if (message.Content is not null
                && message.Content.Headers.TryAddWithoutValidation(name, value))
            {
                continue;
            }

            throw new HttpApiTransportException(
                HttpApiFailureKind.InvalidRequest,
                $"请求头“{name}”不是有效的 HTTP 头。");
        }
    }

    private static void ApplyAuthentication(HttpRequestMessage message, HttpApiRequest request)
    {
        switch (request.AuthMode)
        {
            case HttpApiAuthMode.None:
                return;

            case HttpApiAuthMode.Basic:
                message.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(
                        Encoding.UTF8.GetBytes(
                            $"{request.BasicUsername ?? string.Empty}:{request.BasicPassword ?? string.Empty}")));
                return;

            case HttpApiAuthMode.Bearer:
                message.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    request.BearerToken ?? string.Empty);
                return;

            case HttpApiAuthMode.ApiKey:
                if (string.IsNullOrWhiteSpace(request.ApiKeyHeaderName)
                    || request.ApiKeyHeaderName.Any(static c => char.IsWhiteSpace(c) || c == ':'))
                {
                    throw new HttpApiTransportException(
                        HttpApiFailureKind.InvalidRequest,
                        "API Key Header 名称无效。");
                }

                message.Headers.TryAddWithoutValidation(
                    request.ApiKeyHeaderName,
                    request.ApiKeyValue ?? string.Empty);
                return;

            default:
                throw new HttpApiTransportException(
                    HttpApiFailureKind.InvalidRequest,
                    "不支持的认证方式。");
        }
    }

    private static async Task<(string Body, long Bytes, bool Truncated)> ReadBodyAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var truncated = false;

        while (true)
        {
            var remaining = maxBytes - (int)buffer.Length;
            var readSize = Math.Min(chunk.Length, remaining + 1);
            var read = await stream
                .ReadAsync(chunk.AsMemory(0, readSize), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            var accepted = Math.Min(read, remaining);
            if (accepted > 0)
            {
                buffer.Write(chunk, 0, accepted);
            }

            if (read > accepted)
            {
                truncated = true;
                break;
            }

            if (buffer.Length >= maxBytes)
            {
                // 再读一字节才能确认是否还有内容；下一次循环会读到 0 或触发截断。
                var extra = await stream
                    .ReadAsync(chunk.AsMemory(0, 1), cancellationToken)
                    .ConfigureAwait(false);

                truncated = extra > 0;
                break;
            }
        }

        var encoding = ResolveEncoding(response);
        var text = encoding.GetString(buffer.ToArray());

        return (text, buffer.Length, truncated);
    }

    private static Encoding ResolveEncoding(HttpResponseMessage response)
    {
        var charset = response.Content.Headers.ContentType?.CharSet;
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static IReadOnlyDictionary<string, string[]> CopyHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            headers[header.Key] = header.Value.ToArray();
        }

        return headers;
    }

    private static HttpApiTransportException Classify(HttpRequestException exception)
    {
        if (exception.HttpRequestError == HttpRequestError.NameResolutionError
            || HasSocketError(exception, SocketError.HostNotFound, SocketError.NoData))
        {
            return new HttpApiTransportException(
                HttpApiFailureKind.Dns,
                "DNS 解析失败，未找到目标主机。",
                exception);
        }

        if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return new HttpApiTransportException(
                HttpApiFailureKind.Tls,
                "TLS 握手或服务器证书验证失败。LinkForge 不会关闭证书验证来绕过错误。",
                exception);
        }

        if (exception.HttpRequestError == HttpRequestError.ConnectionError
            || HasSocketError(
                exception,
                SocketError.ConnectionRefused,
                SocketError.HostUnreachable,
                SocketError.NetworkUnreachable,
                SocketError.ConnectionReset))
        {
            return new HttpApiTransportException(
                HttpApiFailureKind.Connection,
                "无法建立 TCP 连接；目标可能未监听、被防火墙拒绝或网络不可达。",
                exception);
        }

        return new HttpApiTransportException(
            HttpApiFailureKind.Network,
            "HTTP 传输失败；请检查网络、代理、TLS 和目标服务状态。",
            exception);
    }

    private static bool HasSocketError(HttpRequestException exception, params SocketError[] errors)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket
                && errors.Contains(socket.SocketErrorCode))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>测试专用：验证异常分类，不绕过真实传输路径。</summary>
    internal static HttpApiTransportException ClassifyForTests(HttpRequestException exception) =>
        Classify(exception);

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalSeconds >= 1
            ? $"{timeout.TotalSeconds:0.#} 秒"
            : $"{timeout.TotalMilliseconds:0} 毫秒";
}
