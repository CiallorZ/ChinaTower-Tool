namespace LinkForge.Adapters.Http;

/// <summary>
/// 一次 HTTP 响应。
/// </summary>
/// <remarks>
/// HTTP 4xx/5xx 仍属于“请求执行完成并收到响应”，因此本对象会被正常返回；
/// 只有 DNS、连接、TLS、超时和取消等传输层问题才通过异常表达。
/// </remarks>
public sealed record HttpApiResponse
{
    /// <summary>实际使用的 HTTP 方法。</summary>
    public required string Method { get; init; }

    /// <summary>最终请求 URI。</summary>
    public required Uri FinalUri { get; init; }

    /// <summary>HTTP 状态码。</summary>
    public required int StatusCode { get; init; }

    /// <summary>HTTP 原因短语。</summary>
    public required string ReasonPhrase { get; init; }

    /// <summary>响应头；同名头会合并为多个值。</summary>
    public required IReadOnlyDictionary<string, string[]> ResponseHeaders { get; init; }

    /// <summary>已解码并截断后的响应体。</summary>
    public required string Body { get; init; }

    /// <summary>从发送到读完响应体的耗时。</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>响应体是否因达到上限而截断。</summary>
    public required bool IsTruncated { get; init; }

    /// <summary>实际保留的响应体字节数。</summary>
    public required long ResponseBytes { get; init; }

    /// <summary>是否为 2xx。</summary>
    public bool IsSuccess => StatusCode is >= 200 and <= 299;

    /// <summary>状态行文本，例如 <c>200 OK</c>。</summary>
    public string StatusText => string.IsNullOrWhiteSpace(ReasonPhrase)
        ? StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : $"{StatusCode} {ReasonPhrase}";
}
