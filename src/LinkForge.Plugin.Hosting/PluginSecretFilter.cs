namespace LinkForge.Plugin.Hosting;

/// <summary>
/// 宿主对插件返回文本执行的秘密过滤。
/// </summary>
/// <remarks>
/// <para>
/// 参数白名单只保证"宿主不把秘密发给插件"，不保证"插件不返回秘密"。
/// 恶意或有缺陷的插件完全可以把厂商 SDK 打印的账号密码塞进摘要或原始输出。
/// 因此在插件文本进入日志、历史、复制或报告之前，宿主必须自己再过滤一遍 ——
/// 这是最后一道边界，不能委托给插件作者。
/// </para>
/// <para>
/// 规则刻意保持保守：宁可多遮蔽，也不放过常见凭据形态。
/// 遮蔽是幂等的，重复执行不会改变已遮蔽的文本。
/// </para>
/// </remarks>
public static class PluginSecretFilter
{
    private static readonly string[] SecretKeyNames =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "accesstoken",
        "refreshtoken",
        "apikey",
        "api_key",
        "authorization",
        "authtoken",
        "privatekey",
        "clientsecret",
        "cookie"
    ];

    /// <summary>遮蔽文本中的凭据赋值，例如 <c>password=abc123</c>。</summary>
    /// <param name="text">待处理文本；null 原样返回。</param>
    /// <returns>遮蔽后的文本。</returns>
    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;

        foreach (var key in SecretKeyNames)
        {
            // 形如 key<分隔><值>：值到行尾、分号、逗号或空白为止。
            result = ReplaceAssignments(result, key);
        }

        // 常见凭据前缀（OpenAI sk-、AWS AKIA、Bearer 头）没有键名可依，按形态遮蔽。
        result = ReplaceTokenShapes(result);

        return result;
    }

    private static string ReplaceAssignments(string text, string key)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var index = 0;

        while (index < text.Length)
        {
            var found = FindKey(text, index, key);

            if (found < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var afterKey = found + key.Length;
            var separator = SkipSeparator(text, afterKey);

            // 键名之后必须紧跟赋值分隔符，否则只是文本里出现了同一个词。
            if (separator == afterKey)
            {
                builder.Append(text, index, separator - index);
                index = separator;
                continue;
            }

            var valueStart = SkipWhitespace(text, separator);

            // 带引号的值（JSON / 配置）：保留引号只替换内容，
            // 否则会产出 "password":*** 这种非法 JSON，把整份导出文件写坏。
            if (valueStart < text.Length && (text[valueStart] == '"' || text[valueStart] == '\''))
            {
                var quote = text[valueStart];
                var closing = FindClosingQuote(text, valueStart + 1, quote);

                if (closing > valueStart)
                {
                    builder.Append(text, index, valueStart - index);
                    builder.Append(quote).Append("***").Append(quote);
                    index = closing + 1;
                    continue;
                }
            }

            var valueEnd = FindValueEnd(text, valueStart);

            // 认证方案名不是秘密：若把 "Bearer" 当值遮蔽，真正的令牌会原样留下，
            // 这是最危险的"假脱敏"（看起来打了码，实际没打）。跳过方案名再遮蔽。
            if (valueEnd > valueStart && IsAuthScheme(text[valueStart..valueEnd]))
            {
                var tokenStart = SkipWhitespace(text, valueEnd);
                var tokenEnd = FindValueEnd(text, tokenStart);

                if (tokenEnd > tokenStart)
                {
                    builder.Append(text, index, tokenStart - index);
                    builder.Append("***");
                    index = tokenEnd;
                    continue;
                }
            }

            if (valueEnd > valueStart)
            {
                builder.Append(text, index, valueStart - index);
                builder.Append("***");
                index = valueEnd;
                continue;
            }

            builder.Append(text, index, separator - index);
            index = separator;
        }

        return builder.ToString();
    }

    /// <summary>找到闭合引号的位置；未闭合时返回 -1。</summary>
    private static int FindClosingQuote(string text, int from, char quote)
    {
        var cursor = from;

        while (cursor < text.Length)
        {
            if (text[cursor] == '\\')
            {
                cursor += 2;
                continue;
            }

            if (text[cursor] == quote)
            {
                return cursor;
            }

            cursor++;
        }

        return -1;
    }

    private static bool IsAuthScheme(string value) =>
        value.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Basic", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Digest", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Token", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Negotiate", StringComparison.OrdinalIgnoreCase)
        || value.Equals("ApiKey", StringComparison.OrdinalIgnoreCase);

    private static int FindKey(string text, int startIndex, string key)
    {
        var found = text.IndexOf(key, startIndex, StringComparison.OrdinalIgnoreCase);

        while (found >= 0)
        {
            var beforeIsBoundary = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            var afterIndex = found + key.Length;
            var afterIsBoundary = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);

            // "secretRef" 必须整体排除：它是引用键，本身就是应当保留的标识符。
            var isReferenceKey = afterIndex < text.Length
                && char.IsLetter(text[afterIndex])
                && text.AsSpan(afterIndex).StartsWith("Ref", StringComparison.OrdinalIgnoreCase);

            if (beforeIsBoundary && afterIsBoundary && !isReferenceKey)
            {
                return found;
            }

            var next = found + 1;

            if (next >= text.Length)
            {
                return -1;
            }

            found = text.IndexOf(key, next, StringComparison.OrdinalIgnoreCase);
        }

        return found;
    }

    private static int SkipSeparator(string text, int index)
    {
        // 依次吃掉：空白 → 键名右侧的引号（JSON/配置写法）→ 空白 → 冒号或等号 → 空白。
        // 少吃任何一段都会让 "password":"x" 整条规则失配，秘密原样漏出去。
        var cursor = SkipWhitespace(text, index);

        if (cursor < text.Length && (text[cursor] == '"' || text[cursor] == '\''))
        {
            cursor = SkipWhitespace(text, cursor + 1);
        }

        if (cursor < text.Length && (text[cursor] == ':' || text[cursor] == '='))
        {
            return SkipWhitespace(text, cursor + 1);
        }

        return index;
    }

    private static int SkipWhitespace(string text, int index)
    {
        var cursor = index;

        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        return cursor;
    }

    private static int FindValueEnd(string text, int valueStart)
    {
        if (valueStart >= text.Length)
        {
            return valueStart;
        }

        // 带引号的值以引号为界。
        if (text[valueStart] == '"' || text[valueStart] == '\'')
        {
            var quote = text[valueStart];
            var cursor = valueStart + 1;

            while (cursor < text.Length)
            {
                if (text[cursor] == '\\')
                {
                    cursor += 2;
                    continue;
                }

                if (text[cursor] == quote)
                {
                    return cursor + 1;
                }

                cursor++;
            }

            return text.Length;
        }

        var end = valueStart;

        while (end < text.Length)
        {
            var ch = text[end];

            if (ch is '\n' or '\r' or ',' or ';' or '&' or '"' or '\'')
            {
                break;
            }

            end++;
        }

        return TrimTrailingSpace(text, valueStart, end);
    }

    private static int TrimTrailingSpace(string text, int valueStart, int end)
    {
        var cursor = end;

        while (cursor > valueStart && char.IsWhiteSpace(text[cursor - 1]))
        {
            cursor--;
        }

        return cursor;
    }

    private static string ReplaceTokenShapes(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var cursor = 0;

        while (cursor < text.Length)
        {
            var shape = FindTokenShape(text, cursor);

            if (shape is null)
            {
                builder.Append(text, cursor, text.Length - cursor);
                break;
            }

            var (start, length) = shape.Value;
            builder.Append(text, cursor, start - cursor);
            builder.Append("***");
            cursor = start + length;
        }

        return builder.ToString();
    }

    private static (int Start, int Length)? FindTokenShape(string text, int from)
    {
        foreach (var prefix in new[] { "sk-", "AKIA", "Bearer ", "bearer " })
        {
            var found = text.IndexOf(prefix, from, StringComparison.Ordinal);

            if (found < 0)
            {
                continue;
            }

            var valueStart = found + prefix.Length;
            var valueEnd = valueStart;

            while (valueEnd < text.Length && !char.IsWhiteSpace(text[valueEnd]) && text[valueEnd] != '"')
            {
                valueEnd++;
            }

            // 只有真的带出一段凭据才遮蔽，避免把正文里的 "sk-" 词根误伤。
            if (valueEnd - valueStart >= 8)
            {
                return (found, valueEnd - found);
            }
        }

        return null;
    }
}
