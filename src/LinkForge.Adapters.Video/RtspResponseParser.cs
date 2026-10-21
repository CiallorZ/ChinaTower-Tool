using System.Globalization;
using System.Text;

namespace LinkForge.Adapters.Video;

/// <summary>
/// RTSP 响应与 SDP 解析的纯函数部分。
/// </summary>
/// <remarks>
/// 拆分出来是为了让超时/认证/非法 SDP 的判定可单测，而不必每次都启动 socket。
/// 解析器不执行任何 I/O，也不把完整 URL 或 Authorization 放进结果。
/// </remarks>
public static class RtspResponseParser
{
    /// <summary>解析 RTSP 状态行与头部。</summary>
    public static bool TryParseResponse(
        string raw,
        out int statusCode,
        out string reason,
        out IReadOnlyDictionary<string, string> headers,
        out string body,
        out string? error)
    {
        statusCode = 0;
        reason = string.Empty;
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        body = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "RTSP 响应为空。";
            return false;
        }

        var separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = separator >= 0 ? raw[..separator] : raw;
        body = separator >= 0 ? raw[(separator + 4)..] : string.Empty;
        var lines = head.Split("\r\n", StringSplitOptions.None);

        if (lines.Length == 0)
        {
            error = "RTSP 响应缺少状态行。";
            return false;
        }

        var statusParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (statusParts.Length < 2
            || !statusParts[0].StartsWith("RTSP/", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(statusParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out statusCode))
        {
            error = "RTSP 状态行格式无效。";
            return false;
        }

        reason = statusParts.Length >= 3 ? statusParts[2] : string.Empty;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');

            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Length > 0)
            {
                map[name] = value;
            }
        }

        headers = map;
        return true;
    }

    /// <summary>读取响应头；不存在时返回 null。</summary>
    public static string? Header(
        IReadOnlyDictionary<string, string> headers,
        string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>解析 SDP 的媒体级信息。</summary>
    public static SdpSummary ParseSdp(string body)
    {
        var lines = body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var sessionName = string.Empty;
        var media = new List<string>();
        var codecs = new List<string>();
        var controls = new List<string>();
        var hasVersion = false;
        var hasTiming = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("v=", StringComparison.Ordinal))
            {
                hasVersion = true;
            }
            else if (line.StartsWith("s=", StringComparison.Ordinal))
            {
                sessionName = line[2..].Trim();
            }
            else if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                media.Add(line[2..].Trim());
            }
            else if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                var value = line["a=rtpmap:".Length..].Trim();
                var space = value.IndexOf(' ');
                codecs.Add(space > 0 ? value[(space + 1)..].Trim() : value);
            }
            else if (line.StartsWith("a=control:", StringComparison.Ordinal))
            {
                controls.Add(line["a=control:".Length..].Trim());
            }
            else if (line.StartsWith("t=", StringComparison.Ordinal))
            {
                hasTiming = true;
            }
        }

        return new SdpSummary(
            hasVersion,
            hasTiming,
            sessionName,
            media.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            codecs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            controls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>从 WWW-Authenticate 提取认证方案，不返回 nonce 等敏感值。</summary>
    public static string DescribeAuthenticationSchemes(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return "未提供";
        }

        var schemes = new List<string>();

        // 认证头可能形如 Basic realm="x", Digest realm="x", nonce="..."；
        // 这里只保留“方案名”，不能把后续参数误当成另一种认证方案。
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     header,
                     @"(?:^|,)\s*(?<scheme>[A-Za-z][A-Za-z0-9_-]*)\s"))
        {
            var scheme = match.Groups["scheme"].Value;

            if (scheme.Length > 0
                && !scheme.Equals("realm", StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals("nonce", StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals("qop", StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals("stale", StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals("algorithm", StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals("opaque", StringComparison.OrdinalIgnoreCase))
            {
                schemes.Add(scheme);
            }
        }

        var distinct = schemes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return distinct.Length == 0 ? "未提供" : string.Join(" / ", distinct);
    }

    /// <summary>把响应头转换为可展示事实，过滤敏感头。</summary>
    public static IReadOnlyList<RtspFact> DescribeHeaders(
        IReadOnlyDictionary<string, string> headers)
    {
        var facts = new List<RtspFact>();

        foreach (var name in new[] { "Server", "CSeq", "Content-Type", "Content-Length", "Public" })
        {
            var value = Header(headers, name);

            if (!string.IsNullOrWhiteSpace(value))
            {
                facts.Add(new RtspFact(name, value));
            }
        }

        var auth = Header(headers, "WWW-Authenticate");

        if (!string.IsNullOrWhiteSpace(auth))
        {
            facts.Add(new RtspFact("认证方案", DescribeAuthenticationSchemes(auth)));
        }

        return facts;
    }

    /// <summary>把响应脱敏为可用于日志/导出的文本。</summary>
    public static string RedactRawResponse(string response)
    {
        var builder = new StringBuilder();
        var sanitized = response
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        var skippingBody = false;

        foreach (var line in sanitized)
        {
            if (skippingBody)
            {
                continue;
            }

            if (line.StartsWith("WWW-Authenticate:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(line[..line.IndexOf(':')] + ": ***");
                continue;
            }

            if (line.Trim().Length == 0)
            {
                skippingBody = true;
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>SDP 摘要。</summary>
public sealed record SdpSummary(
    bool HasVersion,
    bool HasTiming,
    string SessionName,
    IReadOnlyList<string> Media,
    IReadOnlyList<string> Codecs,
    IReadOnlyList<string> Controls)
{
    /// <summary>是否看起来是可用 SDP。</summary>
    public bool IsValid => HasVersion && HasTiming && Media.Count > 0;
}
