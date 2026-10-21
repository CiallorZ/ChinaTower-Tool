using LinkForge.Domain.Diagnostics;

namespace LinkForge.Reporting.Reports;

/// <summary>
/// 报告的来源元数据（产品、项目与目标）。
/// </summary>
/// <remarks>
/// 这些字段来自真实输入，不提供"默认示例值"：
/// 报告必须能回答"这份报告是谁在什么环境下、对哪个目标跑出来的"，
/// 用占位文案填满会让报告看起来完整却无法追溯。
/// </remarks>
public sealed record ReportMetadata
{
    /// <summary>产品名称。</summary>
    public string ProductName { get; init; } = "LinkForge 视联网调试工具箱";

    /// <summary>产品版本（取自真实程序集版本）。</summary>
    public required string ProductVersion { get; init; }

    /// <summary>项目名称；未选择项目时为 null。</summary>
    public string? ProjectName { get; init; }

    /// <summary>报告生成时间。</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>补充环境信息（原样展示，例如运行时与操作系统）。</summary>
    public IReadOnlyList<DiagnosticFact> EnvironmentFacts { get; init; } = [];
}

/// <summary>
/// 一份可渲染的报告内容。
/// </summary>
/// <remarks>
/// <see cref="Run"/> 是唯一的数据来源：报告不重新计算结论，
/// 也不"美化"状态。所有统计值都从 <see cref="DiagnosticRunResult"/> 派生，
/// 因此报告与页面、历史三处永远一致。
/// </remarks>
public sealed record ReportDocument
{
    /// <summary>来源元数据。</summary>
    public required ReportMetadata Metadata { get; init; }

    /// <summary>诊断运行结果。</summary>
    public required DiagnosticRunResult Run { get; init; }

    /// <summary>报告标题。</summary>
    public string Title => $"{Run.DisplayName} 诊断报告";

    /// <summary>任务层状态的显示文案。</summary>
    public string StateText => Run.State switch
    {
        TaskRunState.Completed => "已完成",
        TaskRunState.PartiallySucceeded => "部分成功",
        TaskRunState.Failed => "失败",
        TaskRunState.Cancelled => "已取消",
        TaskRunState.Inconclusive => "无有效结论",
        TaskRunState.Running => "执行中",
        TaskRunState.Preparing => "准备中",
        _ => "待运行"
    };

    /// <summary>是否为成功类结论（用于样式，不代表可以省略风险提示）。</summary>
    public bool IsSuccessState => Run.State == TaskRunState.Completed;

    /// <summary>是否需要提示"结论不完整"。</summary>
    public bool IsIncompleteState => Run.State is
        TaskRunState.PartiallySucceeded or TaskRunState.Cancelled or TaskRunState.Inconclusive;

    /// <summary>
    /// 报告中需要脱敏与扫描的全部文本。
    /// </summary>
    /// <remarks>
    /// 集中在一处枚举，避免新增字段时忘记纳入扫描 ——
    /// 忘记一处就等于给报告开了一个静默的泄漏口。
    /// </remarks>
    public IEnumerable<(string Location, string Text)> EnumerateTexts()
    {
        yield return ("产品名称", Metadata.ProductName);
        yield return ("产品版本", Metadata.ProductVersion);
        yield return ("项目名称", Metadata.ProjectName ?? string.Empty);
        yield return ("目标", Run.TargetLabel);
        yield return ("适配器名称", Run.DisplayName);
        yield return ("适配器标识", Run.AdapterId);
        yield return ("运行标识", Run.RunId);
        yield return ("证据目录", Run.EvidenceDirectory ?? string.Empty);

        foreach (var fact in Metadata.EnvironmentFacts)
        {
            yield return ($"环境信息/{fact.Label}", fact.Value);
        }

        foreach (var step in Run.Steps)
        {
            yield return ($"步骤/{step.StepId}/名称", step.Name);
            yield return ($"步骤/{step.StepId}/摘要", step.Summary);
            yield return ($"步骤/{step.StepId}/建议", step.NextStepSuggestion ?? string.Empty);
            yield return ($"步骤/{step.StepId}/跳过原因", step.SkipReason ?? string.Empty);
            yield return ($"步骤/{step.StepId}/原始输出", step.RawOutput ?? string.Empty);

            foreach (var fact in step.FactItems)
            {
                yield return ($"步骤/{step.StepId}/事实/{fact.Label}", fact.Value);
            }

            foreach (var evidence in step.EvidenceArtifacts)
            {
                yield return ($"步骤/{step.StepId}/证据/{evidence.FileName}", evidence.RelativePath);
                yield return ($"步骤/{step.StepId}/证据说明/{evidence.FileName}", evidence.Description ?? string.Empty);
            }
        }
    }
}

/// <summary>
/// 一条敏感信息扫描发现。
/// </summary>
/// <param name="Location">发现位置（字段路径，不含具体值）。</param>
/// <param name="Kind">疑似敏感信息类别。</param>
/// <remarks>
/// 刻意<b>不</b>保存命中的原文：扫描结果会被写进错误信息与日志，
/// 把原文带进去等于用"检测泄漏"的动作制造一次新的泄漏。
/// </remarks>
public sealed record SensitiveFinding(string Location, string Kind);

/// <summary>
/// 敏感信息扫描结果。
/// </summary>
/// <param name="Findings">命中项。</param>
public sealed record SensitiveScanResult(IReadOnlyList<SensitiveFinding> Findings)
{
    /// <summary>是否通过（无命中）。</summary>
    public bool IsClean => Findings.Count == 0;

    /// <summary>命中项的可读摘要，例如 "3 处：Authorization、URL 凭据"。</summary>
    public string Describe()
    {
        if (IsClean)
        {
            return "未发现疑似敏感信息。";
        }

        var kinds = Findings
            .Select(static f => f.Kind)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static k => k, StringComparer.Ordinal);

        return $"{Findings.Count} 处疑似敏感信息：{string.Join("、", kinds)}";
    }
}

/// <summary>
/// 生成前的敏感信息扫描。
/// </summary>
/// <remarks>
/// 这是<b>第二道</b>检查：写入领域模型的文本理应已经过 <c>ITextRedactor</c>，
/// 本扫描器独立验证这一点。两套规则的重复是刻意的 ——
/// 如果"脱敏"与"校验"共用同一份正则，同一个正则缺陷会让两者同时失效，
/// 扫描就永远显示"通过"。
/// </remarks>
public static class SensitiveDataScanner
{
    /// <summary>
    /// 扫描一组带位置的文本。
    /// </summary>
    /// <param name="texts">(位置, 文本) 序列。</param>
    /// <returns>扫描结果。</returns>
    public static SensitiveScanResult Scan(IEnumerable<(string Location, string Text)> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var findings = new List<SensitiveFinding>();

        foreach (var (location, text) in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            foreach (var kind in Detect(text))
            {
                findings.Add(new SensitiveFinding(location, kind));
            }
        }

        return new SensitiveScanResult(findings);
    }

    private static IEnumerable<string> Detect(string text)
    {
        if (ContainsUrlCredential(text))
        {
            yield return "URL 凭据";
        }

        if (ContainsAuthorization(text))
        {
            yield return "Authorization";
        }

        if (ContainsKeyValueSecret(text))
        {
            yield return "键值型口令";
        }

        if (ContainsToken(text))
        {
            yield return "令牌";
        }

        if (ContainsPrivateKey(text))
        {
            yield return "私钥";
        }
    }

    /// <summary>
    /// URL userinfo 中带真实密码：<c>scheme://user:pass@host</c>。
    /// </summary>
    /// <remarks>
    /// 只把"非掩码"的密码视为命中，因此已脱敏的 <c>user:***@</c> 不会被误报。
    /// </remarks>
    private static bool ContainsUrlCredential(string text)
    {
        foreach (var segment in text.Split(' ', '\t', '\r', '\n', '"', '\''))
        {
            var schemeIndex = segment.IndexOf("://", StringComparison.Ordinal);

            if (schemeIndex <= 0)
            {
                continue;
            }

            var atIndex = segment.IndexOf('@', schemeIndex);

            if (atIndex < 0)
            {
                continue;
            }

            var authority = segment[(schemeIndex + 3)..atIndex];
            var colonIndex = authority.LastIndexOf(':');

            if (colonIndex < 0 || colonIndex == authority.Length - 1)
            {
                continue;
            }

            var password = authority[(colonIndex + 1)..];

            if (password is not ("***" or "*" or ""))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAuthorization(string text)
    {
        var index = text.IndexOf("authorization", StringComparison.OrdinalIgnoreCase);

        while (index >= 0)
        {
            var rest = text[(index + "authorization".Length)..].TrimStart();

            if (rest.StartsWith(':') || rest.StartsWith('='))
            {
                rest = rest[1..].TrimStart();

                // 去掉认证方案前缀：`Bearer ***` 与 `***` 同样表示已脱敏。
                // 不剥离方案会让"已脱敏"的报告永远无法通过扫描，用户只能选择关闭检查。
                foreach (var scheme in AuthSchemes)
                {
                    if (rest.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                    {
                        rest = rest[scheme.Length..].TrimStart();
                        break;
                    }
                }

                // 掩码值（*** 或 <REDACTED>）表示已脱敏，不算命中。
                if (!rest.StartsWith("***", StringComparison.Ordinal)
                    && !rest.StartsWith("<REDACTED>", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            index = text.IndexOf("authorization", index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool ContainsKeyValueSecret(string text)
    {
        foreach (var key in SecretKeyNames)
        {
            var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);

            while (index >= 0)
            {
                var rest = text[(index + key.Length)..].TrimStart();

                if (rest.Length > 0 && (rest[0] == ':' || rest[0] == '='))
                {
                    var value = rest[1..].TrimStart().Trim('"', '\'', ' ', ',', ';', '}', '&');

                    if (value.Length > 0
                        && !value.StartsWith("***", StringComparison.Ordinal)
                        && !value.StartsWith("<REDACTED>", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                index = text.IndexOf(key, index + key.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static bool ContainsToken(string text) =>
        HasJwtShape(text)
        || HasPrefixedToken(text, "ghp_", 20)
        || HasPrefixedToken(text, "gho_", 20)
        || HasPrefixedToken(text, "sk-", 20)
        || HasAwsKey(text);

    private static bool HasJwtShape(string text)
    {
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3)
        {
            return false;
        }

        foreach (var part in parts)
        {
            var trimmed = part.Trim();

            if (trimmed.StartsWith("eyJ", StringComparison.Ordinal)
                && trimmed.Length is > 12 and < 4096
                && trimmed.All(static c => char.IsLetterOrDigit(c) || c is '_' or '-'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPrefixedToken(string text, string prefix, int minBodyLength)
    {
        var index = text.IndexOf(prefix, StringComparison.Ordinal);

        while (index >= 0)
        {
            var body = text[(index + prefix.Length)..]
                .TakeWhile(static c => char.IsLetterOrDigit(c) || c is '_' or '-')
                .ToArray();

            if (body.Length >= minBodyLength)
            {
                return true;
            }

            index = text.IndexOf(prefix, index + prefix.Length, StringComparison.Ordinal);
        }

        return false;
    }

    private static bool HasAwsKey(string text)
    {
        var index = text.IndexOf("AKIA", StringComparison.Ordinal);

        if (index < 0 || index + 20 > text.Length)
        {
            return false;
        }

        return text
            .AsSpan(index + 4, 16)
            .ToString()
            .All(static c => char.IsUpper(c) || char.IsDigit(c));
    }

    private static bool ContainsPrivateKey(string text) =>
        text.Contains("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal)
        || text.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)
        || text.Contains("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal)
        || text.Contains("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal);

    private static readonly string[] SecretKeyNames =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "api_key",
        "apikey",
        "access_token",
        "refresh_token",
        "client_secret",
        "session_id"
    ];

    /// <summary>常见认证方案前缀（用于识别"方案 + 掩码"形态）。</summary>
    private static readonly string[] AuthSchemes =
    [
        "Bearer ",
        "Basic ",
        "Digest ",
        "Token ",
        "Negotiate ",
        "ApiKey "
    ];
}
