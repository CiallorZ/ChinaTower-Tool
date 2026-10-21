namespace LinkForge.Adapters.Http;

/// <summary>请求体类型。</summary>
public enum HttpApiBodyKind
{
    /// <summary>无请求体。</summary>
    None = 0,

    /// <summary>JSON 文本。</summary>
    Json = 1,

    /// <summary>普通文本。</summary>
    Text = 2,

    /// <summary>application/x-www-form-urlencoded 表单。</summary>
    FormUrlEncoded = 3
}

/// <summary>认证方式。</summary>
public enum HttpApiAuthMode
{
    /// <summary>不附加认证信息。</summary>
    None = 0,

    /// <summary>HTTP Basic。</summary>
    Basic = 1,

    /// <summary>Bearer Token。</summary>
    Bearer = 2,

    /// <summary>自定义 Header 中的 API Key。</summary>
    ApiKey = 3
}

/// <summary>
/// 一次 HTTP/API 请求。
/// </summary>
/// <remarks>
/// 该类型只承载执行所需的请求事实，不负责脱敏或持久化。
/// 凭据只在本对象与传输层之间短暂存在；历史、日志和导出不得保存这些字段。
/// </remarks>
public sealed record HttpApiRequest
{
    /// <summary>HTTP 方法。</summary>
    public required string Method { get; init; }

    /// <summary>绝对 URL；只允许 http/https。</summary>
    public required string Url { get; init; }

    /// <summary>查询参数；键和值会由传输层编码。</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>请求头。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>请求体文本；Form 模式下应为原始 key=value 文本。</summary>
    public string? Body { get; init; }

    /// <summary>请求体类型。</summary>
    public HttpApiBodyKind BodyKind { get; init; }

    /// <summary>认证方式。</summary>
    public HttpApiAuthMode AuthMode { get; init; }

    /// <summary>Basic 用户名。</summary>
    public string? BasicUsername { get; init; }

    /// <summary>Basic 密码。</summary>
    public string? BasicPassword { get; init; }

    /// <summary>Bearer Token。</summary>
    public string? BearerToken { get; init; }

    /// <summary>API Key Header 名称。</summary>
    public string ApiKeyHeaderName { get; init; } = "X-API-Key";

    /// <summary>API Key 值。</summary>
    public string? ApiKeyValue { get; init; }

    /// <summary>是否自动跟随重定向。</summary>
    public bool FollowRedirects { get; init; } = true;

    /// <summary>自动跟随的最大重定向次数。</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>本次请求超时。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>响应体最多读取的字节数；超过后截断。</summary>
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
}
