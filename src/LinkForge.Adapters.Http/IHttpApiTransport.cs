namespace LinkForge.Adapters.Http;

/// <summary>
/// HTTP 传输边界。
/// </summary>
/// <remarks>
/// UI 不直接依赖 <see cref="System.Net.Http.HttpClient"/>；
/// 测试使用本地 mock server，不依赖公网。
/// </remarks>
public interface IHttpApiTransport
{
    /// <summary>发送一次请求。</summary>
    Task<HttpApiResponse> SendAsync(
        HttpApiRequest request,
        CancellationToken cancellationToken = default);
}
