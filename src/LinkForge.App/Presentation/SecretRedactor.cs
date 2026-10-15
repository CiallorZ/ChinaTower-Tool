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

    [GeneratedRegex(
        @"(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*)://(?<user>[^:/@\s]+):(?<password>[^@/\s]+)@",
        RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfoPattern();

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
}
