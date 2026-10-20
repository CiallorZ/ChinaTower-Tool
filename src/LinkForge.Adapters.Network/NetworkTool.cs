namespace LinkForge.Adapters.Network;

/// <summary>
/// 网络基础诊断工具。
/// </summary>
/// <remarks>
/// 工具选择由页面传入请求参数，适配器据此只声明本次真正要执行的步骤。
/// 这样页面不会因为“把所有步骤都列出来”而让用户误以为一次运行会执行全套扫描。
/// </remarks>
public enum NetworkTool
{
    /// <summary>ICMP 连通性。</summary>
    Ping,

    /// <summary>TCP 端口连通性。</summary>
    Tcp,

    /// <summary>DNS 解析。</summary>
    Dns,

    /// <summary>本机网卡、网关、DNS 与 IPv4 路由。</summary>
    Local,

    /// <summary>基于 Windows tracert 的路径探测。</summary>
    Traceroute
}

/// <summary>网络工具参数的稳定键名。</summary>
public static class NetworkParameterNames
{
    /// <summary>工具标识。</summary>
    public const string Tool = "tool";

    /// <summary>目标主机或 IP。</summary>
    public const string Target = "target";

    /// <summary>TCP 端口。</summary>
    public const string Port = "port";

    /// <summary>单次探测超时（秒）。</summary>
    public const string TimeoutSeconds = "timeoutSeconds";

    /// <summary>Traceroute 最大跳数。</summary>
    public const string MaxHops = "maxHops";
}
