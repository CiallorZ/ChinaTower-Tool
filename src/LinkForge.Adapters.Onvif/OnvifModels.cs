namespace LinkForge.Adapters.Onvif;

/// <summary>ONVIF 工具类型。</summary>
public enum OnvifTool
{
    /// <summary>通过 WS-Discovery 查找视频发送端。</summary>
    Discover,

    /// <summary>查询指定设备服务地址的详细信息、能力与媒体 Profile。</summary>
    Inspect
}

/// <summary>ONVIF 参数的稳定键名。</summary>
public static class OnvifParameterNames
{
    /// <summary>工具标识。</summary>
    public const string Tool = "tool";

    /// <summary>设备服务 URL。</summary>
    public const string ServiceUrl = "serviceUrl";

    /// <summary>用户名；不进入日志、事实或导出。</summary>
    public const string Username = "username";

    /// <summary>口令；只用于构造 WS-Security 请求头。</summary>
    public const string Password = "password";

    /// <summary>单步超时（秒）。</summary>
    public const string TimeoutSeconds = "timeoutSeconds";

    /// <summary>用户指定的媒体 Profile Token；为空时使用第一个 Profile。</summary>
    public const string ProfileToken = "profileToken";
}

/// <summary>ONVIF 观察结论。</summary>
public enum OnvifObservationStatus
{
    /// <summary>操作成功。</summary>
    Passed,

    /// <summary>操作完成，但结果不完整或需要关注。</summary>
    Warning,

    /// <summary>操作失败。</summary>
    Failed,

    /// <summary>无法判定。</summary>
    Unknown,

    /// <summary>超时。</summary>
    TimedOut
}

/// <summary>ONVIF 事实。</summary>
public readonly record struct OnvifFact(string Label, string Value);

/// <summary>WS-Discovery 发现的设备。</summary>
/// <param name="Endpoint">首个可用的设备服务地址。</param>
/// <param name="Name">从 scopes 提取的名称。</param>
/// <param name="Scopes">设备 scope。</param>
/// <param name="Types">设备类型。</param>
public sealed record OnvifDiscoveredDevice(
    string Endpoint,
    string? Name,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Types);

/// <summary>设备发现结果。</summary>
public sealed record OnvifDiscoveryObservation(
    OnvifObservationStatus Status,
    string Summary,
    IReadOnlyList<OnvifDiscoveredDevice> Devices,
    string? RawOutput = null,
    string? Suggestion = null);

/// <summary>单个 ONVIF SOAP 操作的观察结果。</summary>
public sealed record OnvifOperationObservation(
    OnvifObservationStatus Status,
    string Summary,
    IReadOnlyList<OnvifFact> Facts,
    string? RawOutput = null,
    string? Suggestion = null);

/// <summary>只在调用期间存在的凭据，不得进入 UI 文本或结果对象。</summary>
public sealed record OnvifCredentials(string Username, string Password)
{
    /// <summary>是否提供了凭据。</summary>
    public bool HasValue => !string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password);
}

/// <summary>经过校验的设备服务地址。</summary>
public sealed record OnvifEndpoint(Uri ServiceUri)
{
    /// <summary>用于展示的地址；不接受 userinfo，因此与真实地址一致。</summary>
    public string DisplayUrl => ServiceUri.AbsoluteUri;

    /// <summary>主机。</summary>
    public string Host => ServiceUri.Host;

    /// <summary>解析并校验设备服务 URL。</summary>
    public static bool TryParse(string? value, out OnvifEndpoint endpoint, out string? error)
    {
        endpoint = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请输入 ONVIF 设备服务 URL，例如 http://10.0.0.8/onvif/device_service。";
            return false;
        }

        if (value.Length > 2048)
        {
            error = "设备服务 URL 不能超过 2048 个字符。";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            error = "设备服务 URL 不是有效的绝对地址。";
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            error = "仅支持 http:// 或 https:// 的 ONVIF 设备服务地址。";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "设备服务 URL 不能包含 userinfo；请在独立凭据字段中填写用户名和口令。";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            error = "设备服务 URL 不能包含片段（#）。";
            return false;
        }

        endpoint = new OnvifEndpoint(uri);
        return true;
    }
}

/// <summary>ONVIF 系统边界；测试用它替换真实 UDP/HTTP。</summary>
public interface IOnvifProbe
{
    /// <summary>通过 WS-Discovery 查找视频发送端。</summary>
    Task<OnvifDiscoveryObservation> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>查询设备厂商、型号、固件与序列号。</summary>
    Task<OnvifOperationObservation> GetDeviceInformationAsync(
        OnvifEndpoint endpoint,
        OnvifCredentials? credentials,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>查询设备声明的服务能力。</summary>
    Task<OnvifOperationObservation> GetCapabilitiesAsync(
        OnvifEndpoint endpoint,
        OnvifCredentials? credentials,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>读取媒体 Profile，并查询首个/指定 Profile 的 RTSP Stream URI。</summary>
    Task<OnvifOperationObservation> GetMediaProfilesAsync(
        OnvifEndpoint endpoint,
        OnvifCredentials? credentials,
        string? profileToken,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
