using LinkForge.DeveloperTools;

namespace LinkForge.App.Presentation;

/// <summary>
/// 开发者工具在 UI 中展示的稳定描述。
/// </summary>
/// <param name="Kind">实际执行的离线转换类型。</param>
/// <param name="Name">工具名称。</param>
/// <param name="Description">工具用途与边界说明。</param>
/// <param name="InputLabel">输入字段标签。</param>
/// <param name="Placeholder">示例占位；不替代字段标签。</param>
/// <param name="Hint">格式或安全提示。</param>
/// <param name="ActionLabel">执行按钮文案。</param>
/// <param name="RequiresInput">是否要求输入内容；UUID 数量可为空。</param>
public sealed record DeveloperToolDescriptor(
    DeveloperToolKind Kind,
    string Name,
    string Description,
    string InputLabel,
    string Placeholder,
    string Hint,
    string ActionLabel,
    bool RequiresInput)
{
    /// <summary>页面展示的工具清单，顺序与 PRD LF-011 验收项一致。</summary>
    public static IReadOnlyList<DeveloperToolDescriptor> All { get; } =
    [
        new(
            DeveloperToolKind.JsonFormat,
            "JSON 格式化",
            "校验 JSON 并输出保留中文的缩进结果；注释与尾逗号按无效 JSON 处理。",
            "JSON 输入",
            "{\"设备\":\"摄像机\",\"通道\":2}",
            "输入上限为 1 MiB UTF-8 字节。",
            "格式化",
            true),
        new(
            DeveloperToolKind.JsonMinify,
            "JSON 压缩",
            "校验 JSON 并移除格式空白。",
            "JSON 输入",
            "{ \"a\": 1 }",
            "非法 JSON 不会返回伪装结果。",
            "压缩",
            true),
        new(
            DeveloperToolKind.UrlEncode,
            "URL 编码",
            "按 RFC 3986 对完整输入值进行百分号编码。",
            "待编码文本",
            "设备名称",
            "中文和空格可编码；未配对 UTF-16 代理字符会明确失败。",
            "编码",
            true),
        new(
            DeveloperToolKind.UrlDecode,
            "URL 解码",
            "解码百分号转义序列。",
            "URL 编码文本",
            "device=%E8%AE%BE%E5%A4%87",
            "“+”按空格处理；非法“%”序列不会静默保留。",
            "解码",
            true),
        new(
            DeveloperToolKind.Base64Encode,
            "Base64 编码",
            "把文本按 UTF-8 编码为标准 Base64。",
            "待编码文本",
            "LinkForge",
            "输入可能包含凭据，结果只保留在当前会话。",
            "编码",
            true),
        new(
            DeveloperToolKind.Base64Decode,
            "Base64 解码",
            "解码标准 Base64；允许输入中的空白字符。",
            "Base64 文本",
            "SGVsbG8=",
            "非 UTF-8 字节会显示为十六进制并给出警告。",
            "解码",
            true),
        new(
            DeveloperToolKind.TimestampToTime,
            "时间戳转时间",
            "把 Unix 秒或毫秒时间戳转换为 UTC 与本地时间。",
            "Unix 时间戳",
            "1761000000",
            "13 位按毫秒解析，其余按秒解析。",
            "转换",
            true),
        new(
            DeveloperToolKind.TimeToTimestamp,
            "时间转时间戳",
            "把日期时间转换为 Unix 秒和毫秒。",
            "ISO 8601 时间",
            "2026-10-21T08:30:00+08:00",
            "未写时区时按本机时区解释。",
            "转换",
            true),
        new(
            DeveloperToolKind.UuidGenerate,
            "UUID 生成",
            "批量生成随机 UUID v4。",
            "数量（1–1000，留空为 1）",
            "8",
            "生成过程可取消，不使用示例值伪装结果。",
            "生成",
            false),
        new(
            DeveloperToolKind.JwtDecode,
            "JWT 解码",
            "仅解码 Header 与 Payload；不验证签名、签发者、受众或有效期。",
            "JWT",
            "header.payload.signature",
            "凭据只在当前页面使用，不写入日志、历史或报告。",
            "解码",
            true)
    ];
}
