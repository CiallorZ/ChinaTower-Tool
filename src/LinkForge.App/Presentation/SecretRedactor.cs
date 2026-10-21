using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace LinkForge.App.Presentation;

/// <summary>
/// 敏感信息脱敏（UI 规范 §9、ADR-004）。
/// </summary>
/// <remarks>
/// 应用范围：日志、历史记录、剪贴板、报告与导出文件。
/// 这是**唯一**的脱敏实现点，避免各模块各写一套正则导致漏网。
///
/// 设计取舍：
/// <list type="bullet">
///   <item>宁可多脱敏，不可漏脱敏——无法判定时按敏感处理。</item>
///   <item>保留键名与结构（如 <c>password=***</c>），便于排障时理解字段含义。</item>
///   <item>URL 中的 userinfo（<c>rtsp://user:pass@host</c>）必须整体替换密码段。</item>
/// </list>
/// </remarks>
public static partial class SecretRedactor
{
    /// <summary>统一替换文本。</summary>
    public const string Mask = "***";

    /// <summary>
    /// 判断 HTTP Header 或查询参数名称是否应按“值整体敏感”处理。
    /// </summary>
    /// <param name="name">Header/参数名称。</param>
    /// <returns>敏感名称返回 true。</returns>
    /// <remarks>
    /// 仅对值调用 <see cref="Redact(string?)"/> 不足以识别
    /// <c>Set-Cookie: session=...</c> 这类“值本身不含敏感键名”的形态。
    /// 因此名称命中敏感语义时，调用方应直接把整个值替换为掩码。
    /// </remarks>
    public static bool IsSensitiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return SensitiveNamePattern().IsMatch(name.Trim());
    }

    /// <summary>
    /// 按名称与值共同脱敏。名称敏感时整体遮蔽，否则只处理值中的可识别秘密。
    /// </summary>
    /// <param name="name">Header/参数名称。</param>
    /// <param name="value">原始值。</param>
    /// <returns>可安全显示、记录或导出的值。</returns>
    public static string RedactNamedValue(string? name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        return IsSensitiveName(name) ? Mask : Redact(value) ?? string.Empty;
    }

    /// <summary>
    /// 对任意文本执行脱敏。
    /// </summary>
    /// <param name="input">原始文本；为 null/空时原样返回。</param>
    /// <returns>脱敏后的文本。</returns>
    [return: NotNullIfNotNull(nameof(input))]
    public static string? Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var result = input;

        // 顺序至关重要，且已由测试锁定（曾因顺序错误导致真实泄漏）：
        //
        // 1. Authorization 头必须最先处理。"authorization" 本身也是敏感键名，
        //    若先跑键值规则，会把方案名（Bearer/Basic）替换成 ***，
        //    真正的令牌反而留在明文里 —— 这是最危险的"假脱敏"。
        // 2. URL userinfo 其次：保留用户名与主机，只替换密码段。
        // 3. 键值对：处理 JSON / 查询串 / 配置行。
        // 4. 无键名令牌形态：JWT、PAT 等。
        result = RedactAuthorizationHeaders(result);
        result = RedactUrlUserInfo(result);
        result = RedactBareUserInfo(result);
        result = RedactKeyValuePairs(result);
        result = RedactKnownTokenFormats(result);

        return result;
    }

    /// <summary>
    /// URL 中的认证信息：<c>scheme://user:password@host</c> -> <c>scheme://user:***@host</c>。
    /// </summary>
    /// <remarks>
    /// 覆盖 rtsp / http(s) / mqtt / ftp 等所有带 userinfo 的协议。
    /// 仅脱敏密码段，保留用户名与主机，便于定位问题。
    /// </remarks>
    private static string RedactUrlUserInfo(string input) =>
        UrlUserInfoPattern().Replace(input, static match =>
            $"{match.Groups["scheme"].Value}://{match.Groups["user"].Value}:{Mask}@");

    /// <summary>
    /// 不带协议的 userinfo：<c>admin:password@10.0.0.8</c> -> <c>admin:***@10.0.0.8</c>。
    /// </summary>
    /// <remarks>
    /// 带 scheme 的规则只覆盖 <c>rtsp://user:pass@host</c>，但目标标签、插件返回的
    /// 连接串和厂商 SDK 日志常以<b>裸</b> <c>user:pass@host</c> 形态出现
    /// （例如 "10.0.0.8 的凭据 admin:Passw0rd@10.0.0.8"）。
    /// 没有这条规则时，密码会原样进入日志、历史、剪贴板与报告。
    ///
    /// 边界刻意收紧：用户名与密码都不允许包含空白、斜杠、冒号或 <c>@</c>，
    /// 因此普通邮箱（<c>a@b.com</c>，冒号不存在）与 <c>10.0.0.1:554</c>（无 <c>@</c>）
    /// 都不会被误伤；替换后仍是 <c>user:***@host</c>，幂等。
    /// </remarks>
    private static string RedactBareUserInfo(string input) =>
        BareUserInfoPattern().Replace(input, static match =>
            $"{match.Groups["user"].Value}:{Mask}@");

    /// <summary>
    /// 键值对形式：<c>password=xxx</c>、<c>"token": "xxx"</c>、<c>api_key: xxx</c> 等。
    /// </summary>
    private static string RedactKeyValuePairs(string input) =>
        KeyValuePattern().Replace(input, static match =>
            $"{match.Groups["prefix"].Value}{match.Groups["quote"].Value}{Mask}{match.Groups["quote"].Value}");

    /// <summary>
    /// Authorization 头：<c>Authorization: Bearer xxx</c>、<c>Basic xxx</c>、<c>Authorization: xxx</c>。
    /// </summary>
    /// <remarks>
    /// 必须能处理"无认证方案"的形态（<c>Authorization: &lt;裸令牌&gt;</c>），
    /// 否则该值会落入通用键值规则，被替换成"方案名"而漏掉真正的令牌。
    /// </remarks>
    private static string RedactAuthorizationHeaders(string input) =>
        AuthorizationPattern().Replace(input, static match =>
            match.Groups["scheme"].Success
                ? $"{match.Groups["prefix"].Value}{match.Groups["scheme"].Value} {Mask}"
                : $"{match.Groups["prefix"].Value}{Mask}");

    /// <summary>
    /// 常见令牌形态（无键名上下文时也能识别）。
    /// </summary>
    private static string RedactKnownTokenFormats(string input) =>
        KnownTokenPattern().Replace(input, Mask);

    // 键名清单：命中即脱敏。宁可包含更多近义词，避免因命名差异漏网。
    //
    // 注意：这里**不能**包含 authorization —— 它由 AuthorizationPattern 专用规则处理，
    // 否则通用规则会把方案名（Bearer/Basic）当值脱敏，反而留下真正的令牌。
    // 同理保留的 auth 是独立键名，因负向后顾不会误命中 authorization 内部。
    private const string SensitiveKeyNames =
        "password|passwd|pwd|secret|token|access[_-]?token|refresh[_-]?token|" +
        "api[_-]?key|apikey|app[_-]?key|app[_-]?secret|client[_-]?secret|" +
        "auth|credential|private[_-]?key|session[_-]?id|" +
        "cookie|set-cookie|signature|sign|accesskey|access[_-]?key[_-]?secret";

    // Header 名称没有统一的命名空间。这里覆盖标准认证/会话头，
    // 以及常见网关与厂商使用的令牌头；宁可整体遮蔽，也不把值交给启发式规则猜测。
    private const string SensitiveHeaderNames =
        "authorization|proxy-authorization|cookie|set-cookie|" +
        "x-api-key|api-key|x-auth-token|x-access-token|x-session-token|x-token|" +
        "x-csrf-token|x-xsrf-token|www-authenticate|proxy-authenticate";

    [GeneratedRegex(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*)://(?<user>[^:/@\s]+):(?<password>[^@/\s]+)@",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoPattern();

    // 用户名前不能紧跟 / : @ ，避免把 scheme://user 再匹配一次。
    [GeneratedRegex(
        @"(?<![:/@])(?<user>[^:/@\s]+):(?<password>[^:/@\s]+)@",
        RegexOptions.CultureInvariant)]
    private static partial Regex BareUserInfoPattern();

    [GeneratedRegex(
        $@"(?<prefix>(?<![A-Za-z0-9_\-])[""']?(?:{SensitiveKeyNames})[""']?\s*[:=]\s*)(?<quote>[""']?)(?<value>[^""'\s,;&}}]+)\k<quote>",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(
        @"(?<prefix>(?<![A-Za-z0-9_\-])(?:Proxy-)?Authorization[""']?\s*[:=]\s*[""']?)" +
        @"(?:(?<scheme>Bearer|Basic|Digest|Token|Negotiate|ApiKey)\s+)?" +
        @"(?<value>[^""'\s,;&}]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationPattern();

    // 无键名上下文：JWT、GitHub PAT、常见厂商密钥前缀。
    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b" +
        @"|\bgh[pousr]_[A-Za-z0-9]{20,}\b" +
        @"|\bsk-[A-Za-z0-9]{20,}\b" +
        @"|\bAKIA[0-9A-Z]{16}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenPattern();

    [GeneratedRegex(
        $@"^(?:{SensitiveHeaderNames}|{SensitiveKeyNames})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveNamePattern();
}
