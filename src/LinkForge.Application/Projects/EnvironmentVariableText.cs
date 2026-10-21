namespace LinkForge.Application.Projects;

/// <summary>
/// 环境变量文本的一处问题（LF-004）。
/// </summary>
/// <param name="LineNumber">物理行号（1 起）。</param>
/// <param name="Message">面向用户的说明。</param>
public readonly record struct EnvironmentVariableIssue(int LineNumber, string Message)
{
    /// <summary>显示为“第 N 行：说明”。</summary>
    public override string ToString() => $"第 {LineNumber} 行：{Message}";
}

/// <summary>
/// 环境变量文本的解析结果。
/// </summary>
public sealed record EnvironmentVariableParseResult
{
    /// <summary>解析成功的变量（键大小写不敏感）。</summary>
    public required IReadOnlyDictionary<string, string> Variables { get; init; }

    /// <summary>被拒绝的行。</summary>
    public required IReadOnlyList<EnvironmentVariableIssue> Errors { get; init; }

    /// <summary>是否存在被拒绝的行。</summary>
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// 环境变量在界面与数据库之间的文本形态（LF-004）。
/// </summary>
/// <remarks>
/// <para>
/// 形态是每行一条 <c>KEY=VALUE</c>，以 <c>#</c> 开头的行是注释。
/// 值允许包含 <c>=</c>（只按第一个等号切分），因为代理地址之类的值里常有等号。
/// </para>
/// <para>
/// <b>凭据边界。</b>环境只保存非秘密配置。若变量名看起来像密码或令牌，
/// 本解析器会拒绝该行并说明“请改用秘密引用”——
/// 这不是过度谨慎：环境变量会随项目导出、随报告展示，
/// 一旦明文密码进入这里，它就会出现在用户根本没预期的地方。
/// </para>
/// <para>
/// 解析规则集中在此处，界面表单与后续的导入导出共用同一份实现。
/// </para>
/// </remarks>
public static class EnvironmentVariableText
{
    /// <summary>变量名长度上限。</summary>
    public const int KeyLength = 128;

    /// <summary>变量值长度上限。</summary>
    public const int ValueLength = 1024;

    /// <summary>变量条数上限。</summary>
    public const int Count = 64;

    /// <summary>
    /// 承载秘密语义的变量名词段。
    /// </summary>
    /// <remarks>
    /// 这里只放<b>单个词段</b>，不再放 <c>api_key</c> 这类已经带下划线的组合：
    /// 匹配发生在切分之后，<c>API_KEY</c> 会切成 <c>api</c> + <c>key</c>，
    /// 写成 <c>api_key</c> 反而永远匹配不上。
    /// </remarks>
    private static readonly string[] ForbiddenNameFragments =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "apikey",
        "appkey",
        "credential",
        "privatekey",
        "accesskey",
        "sessionid",
        "cookie",
        "signature",
        "key"
    ];

    /// <summary>
    /// 变量名的词段分隔符：下划线、连字符、点与驼峰边界。
    /// </summary>
    /// <remarks>
    /// 驼峰在 <see cref="LooksLikeSecret"/> 里单独处理，不放进这里，
    /// 因为把大写字母当分隔符会破坏“原样分段”的可读性。
    /// </remarks>
    private static readonly char[] SegmentSeparators = ['_', '-', '.'];

    /// <summary>
    /// 解析环境变量文本。
    /// </summary>
    /// <param name="raw">原始文本；可为空。</param>
    /// <returns>解析结果；不抛异常，问题以 <see cref="EnvironmentVariableParseResult.Errors"/> 表达。</returns>
    public static EnvironmentVariableParseResult Parse(string? raw)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<EnvironmentVariableIssue>();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new EnvironmentVariableParseResult { Variables = variables, Errors = errors };
        }

        // 必须按 CRLF 整体切，不能把 \r 和 \n 当作两个独立分隔符：
        // Windows 上 Format 写出的就是 CRLF，逐字符切会把每行变成“内容 + 空行”，
        // 于是行号整体错位（用户看到的“第 3 行”其实指向另一行），
        // 往返也不再稳定。
        var lines = raw.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');

            if (separator <= 0)
            {
                errors.Add(new EnvironmentVariableIssue(
                    lineNumber,
                    "格式应为 KEY=VALUE，等号不能缺失。"));
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (key.Length == 0)
            {
                errors.Add(new EnvironmentVariableIssue(lineNumber, "变量名不能为空。"));
                continue;
            }

            if (key.Length > KeyLength)
            {
                errors.Add(new EnvironmentVariableIssue(
                    lineNumber,
                    $"变量名超过 {KeyLength} 字符上限。"));
                continue;
            }

            if (value.Length > ValueLength)
            {
                errors.Add(new EnvironmentVariableIssue(
                    lineNumber,
                    $"变量值超过 {ValueLength} 字符上限。"));
                continue;
            }

            if (LooksLikeSecret(key))
            {
                errors.Add(new EnvironmentVariableIssue(
                    lineNumber,
                    $"变量名 “{key}” 看起来承载密码或令牌，环境只保存非秘密配置。"
                    + "请改用秘密引用键，或换用不含凭据含义的变量名。"));
                continue;
            }

            if (variables.ContainsKey(key))
            {
                errors.Add(new EnvironmentVariableIssue(
                    lineNumber,
                    $"变量 “{key}” 重复（变量名大小写不敏感）。"));
                continue;
            }

            if (variables.Count >= Count)
            {
                errors.Add(new EnvironmentVariableIssue(
                    lineNumber,
                    $"环境变量超过 {Count} 条上限。"));
                continue;
            }

            variables[key] = value;
        }

        return new EnvironmentVariableParseResult
        {
            Variables = variables,
            Errors = errors
        };
    }

    /// <summary>
    /// 把变量渲染成可编辑文本（按键名排序，保证同一份配置导出后稳定可比对）。
    /// </summary>
    /// <param name="variables">变量。</param>
    /// <returns>每行一条 <c>KEY=VALUE</c> 的文本。</returns>
    public static string Format(IReadOnlyDictionary<string, string>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return string.Empty;
        }

        // 刻意用 System.Environment.NewLine：同名的 LinkForge.Application.Environment
        // 命名空间会在本文件里遮蔽掉 Environment 关键字。
        return string.Join(
            System.Environment.NewLine,
            variables
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    /// <summary>
    /// 判断变量名是否承载秘密语义。
    /// </summary>
    /// <param name="key">变量名。</param>
    /// <returns>是否应拒绝。</returns>
    private static bool LooksLikeSecret(string key)
    {
        // 按分隔符切成词段再逐段比对，而不是整串 Contains。
        // 差别很实际：
        //   MY_PWD   -> ["my", "pwd"]            -> 命中 "pwd"，拒绝
        //   MY-PWD   -> ["my", "pwd"]            -> 命中 "pwd"，拒绝
        //   TOKENIZER-> ["tokenizer"]            -> 不等于 "token"，放行
        //   AUTHOR   -> ["author"]               -> 不承载凭据，放行
        //   COOKIEJAR-> ["cookiejar"]            -> 不等于 "cookie"，放行
        // 整串子串匹配会把 TOKENIZER、COOKIEJAR、KEYSTORE 一并误杀，
        // 那类误拦会逼用户把变量改名，是比漏拦更难排查的故障。
        var segments = SplitSegments(key);

        foreach (var segment in segments)
        {
            foreach (var fragment in ForbiddenNameFragments)
            {
                if (segment.Equals(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // 复合词也要能识别：AWS_SECRET_ACCESS_KEY 切出的是 aws/secret/access/key，
        // 单段比对会漏掉，这里补一次“相邻段拼接”的检查。
        for (var index = 0; index + 1 < segments.Count; index++)
        {
            foreach (var fragment in ForbiddenNameFragments)
            {
                if ((segments[index] + segments[index + 1])
                    .Equals(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 把变量名切成词段：先按分隔符切，再按驼峰边界切。
    /// </summary>
    /// <param name="key">变量名。</param>
    /// <returns>小写的词段序列。</returns>
    /// <remarks>
    /// 驼峰边界必须处理：<c>dbPassword</c> 与 <c>DB_PASSWORD</c> 语义相同，
    /// 只按分隔符切的话前者会漏网。
    /// </remarks>
    private static List<string> SplitSegments(string key)
    {
        var segments = new List<string>();

        foreach (var part in key.Split(
                     SegmentSeparators,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var start = 0;

            for (var index = 1; index < part.Length; index++)
            {
                if (!char.IsUpper(part[index]))
                {
                    continue;
                }

                // 大写字母开头一个新词；若前一个也是大写（AWSKey），则并入当前词。
                if (char.IsUpper(part[index - 1]))
                {
                    continue;
                }

                segments.Add(part[start..index]);
                start = index;
            }

            segments.Add(part[start..]);
        }

        for (var index = 0; index < segments.Count; index++)
        {
            segments[index] = segments[index].ToLowerInvariant();
        }

        return segments;
    }
}
