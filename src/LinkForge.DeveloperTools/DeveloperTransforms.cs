using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LinkForge.DeveloperTools;

/// <summary>开发者工具类别。</summary>
public enum DeveloperToolKind
{
    JsonFormat,
    JsonMinify,
    UrlEncode,
    UrlDecode,
    Base64Encode,
    Base64Decode,
    TimestampToTime,
    TimeToTimestamp,
    UuidGenerate,
    JwtDecode
}

/// <summary>转换结果；失败时不抛异常，便于界面统一展示。</summary>
public sealed record DeveloperToolResult(bool Succeeded, string Output, string Message, bool IsWarning = false)
{
    public static DeveloperToolResult Ok(string output, string message, bool warning = false) =>
        new(true, output, message, warning);

    public static DeveloperToolResult Fail(string message) => new(false, string.Empty, message);
}

/// <summary>
/// 纯离线文本转换。
/// </summary>
/// <remarks>
/// 不访问网络、文件或系统剪贴板；输入上限按 UTF-8 字节计，
/// 防止误粘贴超大日志导致内存与渲染压力失控。
/// </remarks>
public static class DeveloperTransforms
{
    public const int MaxInputBytes = 1024 * 1024;
    private const int MaxUuidCount = 1000;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static DeveloperToolResult Execute(
        DeveloperToolKind kind,
        string? input,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        input ??= string.Empty;

        if (Encoding.UTF8.GetByteCount(input) > MaxInputBytes)
        {
            return DeveloperToolResult.Fail($"输入超过 {MaxInputBytes / 1024} KiB 上限，请截取后再处理。");
        }

        return kind switch
        {
            DeveloperToolKind.JsonFormat => Json(input, PrettyJson, "JSON 合法，已格式化。"),
            DeveloperToolKind.JsonMinify => Json(input, CompactJson, "JSON 合法，已压缩。"),
            DeveloperToolKind.UrlEncode => UrlEncode(input),
            DeveloperToolKind.UrlDecode => UrlDecode(input),
            DeveloperToolKind.Base64Encode => DeveloperToolResult.Ok(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(input)), "已按 UTF-8 编码为 Base64。"),
            DeveloperToolKind.Base64Decode => Base64Decode(input),
            DeveloperToolKind.TimestampToTime => TimestampToTime(input),
            DeveloperToolKind.TimeToTimestamp => TimeToTimestamp(input),
            DeveloperToolKind.UuidGenerate => UuidGenerate(input, cancellationToken),
            DeveloperToolKind.JwtDecode => JwtDecode(input),
            _ => DeveloperToolResult.Fail("不支持的工具。")
        };
    }

    private static DeveloperToolResult Json(string input, JsonSerializerOptions options, string message)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return DeveloperToolResult.Fail("请输入 JSON。");
        }

        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
            return DeveloperToolResult.Ok(JsonSerializer.Serialize(document.RootElement, options), message);
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber is { } l ? l + 1 : 0;
            var column = ex.BytePositionInLine is { } c ? c + 1 : 0;
            return DeveloperToolResult.Fail($"JSON 无效：第 {line} 行，第 {column} 字节附近。{ex.Message}");
        }
    }

    private static DeveloperToolResult UrlDecode(string input)
    {
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] != '%')
            {
                continue;
            }

            if (i + 2 >= input.Length
                || !IsHex(input[i + 1])
                || !IsHex(input[i + 2]))
            {
                return DeveloperToolResult.Fail($"URL 编码无效：位置 {i + 1} 的“%”后必须是两位十六进制字符。");
            }

            i += 2;
        }

        try
        {
            return DeveloperToolResult.Ok(Uri.UnescapeDataString(input.Replace('+', ' ')), "已解码；“+”按空格处理。");
        }
        catch (UriFormatException ex)
        {
            return DeveloperToolResult.Fail($"URL 编码无效：{ex.Message}");
        }
    }

    private static DeveloperToolResult UrlEncode(string input)
    {
        if (!HasValidSurrogatePairs(input))
        {
            return DeveloperToolResult.Fail("URL 编码失败：输入包含未配对的 UTF-16 代理字符。");
        }

        try
        {
            return DeveloperToolResult.Ok(Uri.EscapeDataString(input), "已按 RFC 3986 编码。");
        }
        catch (UriFormatException)
        {
            return DeveloperToolResult.Fail("URL 编码失败：输入包含未配对的 UTF-16 代理字符。");
        }
    }

    private static bool HasValidSurrogatePairs(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9'
        or >= 'a' and <= 'f'
        or >= 'A' and <= 'F';

    private static DeveloperToolResult Base64Decode(string input)
    {
        if (!TryDecodeBase64(input, urlSafe: false, out var bytes))
        {
            return DeveloperToolResult.Fail("Base64 无效：长度、字符或填充不正确。");
        }

        return DecodeUtf8(bytes, "Base64 已解码为 UTF-8 文本。");
    }

    private static DeveloperToolResult DecodeUtf8(byte[] bytes, string message)
    {
        try
        {
            return DeveloperToolResult.Ok(new UTF8Encoding(false, true).GetString(bytes), message);
        }
        catch (DecoderFallbackException)
        {
            return DeveloperToolResult.Ok(Convert.ToHexString(bytes), "内容不是有效 UTF-8，已显示为十六进制。", warning: true);
        }
    }

    private static bool TryDecodeBase64(string input, bool urlSafe, out byte[] bytes)
    {
        bytes = [];
        var text = string.Concat(input.Where(static c => !char.IsWhiteSpace(c)));

        if (urlSafe)
        {
            text = text.Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + ((4 - text.Length % 4) % 4), '=');
        }

        if (text.Length == 0 || text.Length % 4 != 0)
        {
            return false;
        }

        var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(text.Length)];
        var status = Base64.DecodeFromUtf8(Encoding.ASCII.GetBytes(text), buffer, out _, out var written);

        if (status != System.Buffers.OperationStatus.Done)
        {
            return false;
        }

        bytes = buffer[..written];
        return true;
    }

    private static DeveloperToolResult TimestampToTime(string input)
    {
        var text = input.Trim();

        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return DeveloperToolResult.Fail("请输入整数 Unix 时间戳（秒或毫秒）。");
        }

        var isMilliseconds = text.TrimStart('-').Length >= 13;

        try
        {
            var time = isMilliseconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            var local = time.ToLocalTime();
            var output = $"UTC：{time:yyyy-MM-dd HH:mm:ss.fff zzz}\n本地：{local:yyyy-MM-dd HH:mm:ss.fff zzz}";
            return DeveloperToolResult.Ok(output, isMilliseconds ? "已按毫秒解析。" : "已按秒解析。");
        }
        catch (ArgumentOutOfRangeException)
        {
            return DeveloperToolResult.Fail("时间戳超出 0001-01-01 至 9999-12-31 的可表示范围。");
        }
    }

    private static DeveloperToolResult TimeToTimestamp(string input)
    {
        if (!DateTimeOffset.TryParse(input.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
        {
            return DeveloperToolResult.Fail("时间格式无效。建议使用 ISO 8601，例如 2026-10-21T08:30:00+08:00。");
        }

        return DeveloperToolResult.Ok(
            $"秒：{time.ToUnixTimeSeconds()}\n毫秒：{time.ToUnixTimeMilliseconds()}",
            "已转换；未写时区时按本机时区解释。");
    }

    private static DeveloperToolResult UuidGenerate(string input, CancellationToken cancellationToken)
    {
        var text = input.Trim();
        var count = 1;

        if (text.Length > 0 && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count)
            || count is < 1 or > MaxUuidCount))
        {
            return DeveloperToolResult.Fail($"生成数量需为 1–{MaxUuidCount} 的整数；留空生成 1 个。");
        }

        var builder = new StringBuilder(count * 38);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AppendLine(Guid.NewGuid().ToString("D"));
        }

        return DeveloperToolResult.Ok(builder.ToString().TrimEnd(), $"已生成 {count} 个随机 UUID v4。");
    }

    private static DeveloperToolResult JwtDecode(string input)
    {
        var parts = input.Trim().Split('.');

        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return DeveloperToolResult.Fail("JWT 格式无效：应为 header.payload.signature 三段。");
        }

        if (!TryDecodeJwtJson(parts[0], out var header) || !TryDecodeJwtJson(parts[1], out var payload))
        {
            return DeveloperToolResult.Fail("JWT Header 或 Payload 不是有效的 Base64URL JSON。");
        }

        var output = $"Header\n{header}\n\nPayload\n{payload}\n\nSignature\n<未验证，长度 {parts[2].Length} 字符>";
        return DeveloperToolResult.Ok(output, "仅完成解码：未验证签名、签发者、受众或有效期，不能证明令牌可信。", warning: true);
    }

    private static bool TryDecodeJwtJson(string segment, out string json)
    {
        json = string.Empty;

        if (!TryDecodeBase64(segment, urlSafe: true, out var bytes))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            json = JsonSerializer.Serialize(document.RootElement, PrettyJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
