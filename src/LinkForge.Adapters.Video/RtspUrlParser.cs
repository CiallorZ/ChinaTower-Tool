using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace LinkForge.Adapters.Video;

/// <summary>
/// RTSP URL 解析与脱敏。
/// </summary>
/// <remarks>
/// 只接受 rtsp/rtsps，不把任意 URI 送进 socket；userinfo 保留在请求对象中，
/// 但展示、日志、事实和导出只使用 <see cref="RtspEndpoint.Redacted"/>。
/// </remarks>
public static class RtspUrlParser
{
    /// <summary>尝试解析 RTSP URL。</summary>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out RtspEndpoint? endpoint,
        [NotNullWhen(false)] out string? error)
    {
        endpoint = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "RTSP URL 不能为空。";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            error = "RTSP URL 格式无效，请使用 rtsp://host:554/path。";
            return false;
        }

        if (!uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("rtsps", StringComparison.OrdinalIgnoreCase))
        {
            error = "只支持 rtsp:// 或 rtsps:// URL。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "RTSP URL 缺少主机名或 IP。";
            return false;
        }

        var port = uri.IsDefaultPort ? 554 : uri.Port;

        if (port is < 1 or > 65535)
        {
            error = "RTSP 端口必须在 1-65535 之间。";
            return false;
        }

        var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        var redactedUser = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : "***:***@";
        // Uri.Host 对 IPv6 有时已带方括号；先去掉再统一格式化，避免出现 "[[::1]]"。
        var host = uri.Host.Trim('[', ']');
        var redacted = $"{uri.Scheme.ToLowerInvariant()}://{redactedUser}{FormatHost(host)}:{port}{path}";

        endpoint = new RtspEndpoint(
            uri.AbsoluteUri,
            redacted,
            uri.Scheme.ToLowerInvariant(),
            host,
            port,
            string.IsNullOrEmpty(uri.UserInfo) ? null : uri.UserInfo,
            path);

        return true;
    }

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
}
