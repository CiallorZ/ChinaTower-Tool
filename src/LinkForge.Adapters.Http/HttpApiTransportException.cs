namespace LinkForge.Adapters.Http;

/// <summary>HTTP 传输失败的分类。</summary>
public enum HttpApiFailureKind
{
    /// <summary>请求参数不合法。</summary>
    InvalidRequest = 0,

    /// <summary>本次请求超时。</summary>
    Timeout = 1,

    /// <summary>DNS 解析失败。</summary>
    Dns = 2,

    /// <summary>TCP 连接失败或被拒绝。</summary>
    Connection = 3,

    /// <summary>TLS 握手或证书验证失败。</summary>
    Tls = 4,

    /// <summary>其他网络错误。</summary>
    Network = 5,

    /// <summary>未分类异常。</summary>
    Unexpected = 6
}

/// <summary>
/// 传输层异常，携带稳定分类供 UI 给出可执行建议。
/// </summary>
public sealed class HttpApiTransportException : Exception
{
    /// <summary>创建传输异常。</summary>
    public HttpApiTransportException(
        HttpApiFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>失败分类。</summary>
    public HttpApiFailureKind Kind { get; }
}
