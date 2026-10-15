using System.Windows.Media;

namespace LinkForge.App.Presentation;

/// <summary>
/// 结构化日志级别（架构文档 §5：日志字段至少包含 Timestamp、Level、Module、RunId、TargetId、EventId）。
/// </summary>
/// <remarks>
/// 刻意不引入日志框架的 Level 类型：本枚举是 UI 展示层契约，
/// 让 Presentation 依赖具体日志框架会破坏分层（架构 §4 Application 层不得引用 UI）。
/// </remarks>
public enum LfLogLevel
{
    /// <summary>跟踪：最细粒度的内部步骤。</summary>
    Trace,

    /// <summary>调试：开发排障信息。</summary>
    Debug,

    /// <summary>信息：常规运行信息。</summary>
    Information,

    /// <summary>警告：异常但已继续。</summary>
    Warning,

    /// <summary>错误：操作失败但任务可继续。</summary>
    Error,

    /// <summary>致命：任务无法继续。</summary>
    Critical
}

/// <summary>
/// 一条结构化日志（视图模型）。
/// </summary>
/// <param name="Timestamp">本地时间戳。</param>
/// <param name="Level">级别。</param>
/// <param name="Module">模块名，例如 "Mqtt"、"Network"。</param>
/// <param name="RunId">所属任务运行 ID，用于与证据关联。</param>
/// <param name="TargetId">目标 ID（可空）。</param>
/// <param name="OriginalMessage">原始消息（未脱敏）。</param>
/// <remarks>
/// <para>
/// 关键设计：<b>脱敏在构造时完成，且不可逆</b>。
/// </para>
/// <para>
/// 为什么不在控件里按需脱敏：那样原始明文会一直留在内存与可视化树中，
/// 任何一次绑定路径写错、模板改动或 UI 自动化树读取都会拿到明文。
/// 架构文档 §3.4 要求"日志脱敏不能仅靠 UI 层，必须在日志写入前处理"，
/// 本类型承担的就是"进入 UI 之前的最后一道脱敏"。
/// </para>
/// </remarks>
public sealed class LogEntry
{
    /// <summary>
    /// 构造一条已脱敏的日志。
    /// </summary>
    /// <param name="timestamp">时间戳。</param>
    /// <param name="level">级别。</param>
    /// <param name="module">模块名。</param>
    /// <param name="runId">任务运行 ID。</param>
    /// <param name="targetId">目标 ID，可空。</param>
    /// <param name="originalMessage">原始消息；此处立即脱敏，之后不再保留明文。</param>
    public LogEntry(
        DateTimeOffset timestamp,
        LfLogLevel level,
        string module,
        string runId,
        string? targetId,
        string originalMessage)
    {
        Timestamp = timestamp;
        Level = level;
        Module = module;
        RunId = runId;
        TargetId = targetId;
        Message = SecretRedactor.Redact(originalMessage) ?? string.Empty;
    }

    /// <summary>本地时间戳。</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>级别。</summary>
    public LfLogLevel Level { get; }

    /// <summary>模块名。</summary>
    public string Module { get; }

    /// <summary>所属任务运行 ID。</summary>
    public string RunId { get; }

    /// <summary>目标 ID（可空）。</summary>
    public string? TargetId { get; }

    /// <summary>
    /// 已脱敏的消息。
    /// </summary>
    /// <remarks>
    /// 这是本对象对外暴露的**唯一**消息出口，没有明文属性可供误用。
    /// </remarks>
    public string Message { get; }

    /// <summary>格式化时间戳，例如 "14:03:21.482"。</summary>
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>级别的中文短标签。</summary>
    public string LevelText => Level switch
    {
        LfLogLevel.Trace => "跟踪",
        LfLogLevel.Debug => "调试",
        LfLogLevel.Information => "信息",
        LfLogLevel.Warning => "警告",
        LfLogLevel.Error => "错误",
        LfLogLevel.Critical => "致命",
        _ => "未知"
    };

    /// <summary>目标文本；为空时显示为 "-"，避免空列让表格看起来缺数据。</summary>
    public string TargetText => string.IsNullOrWhiteSpace(TargetId) ? "-" : TargetId;
}

/// <summary>
/// 日志级别到视觉语义的映射。
/// </summary>
/// <remarks>
/// 复用 <see cref="StatusVisualMap"/> 的令牌体系，避免日志自造一套颜色，
/// 导致"日志里的警告色"与"状态标签的警告色"不一致。
/// </remarks>
public static class LogLevelPresentation
{
    /// <summary>级别对应的视觉语义。</summary>
    public static LfStatusVisual Visual(LfLogLevel level) => level switch
    {
        LfLogLevel.Trace => LfStatusVisual.Neutral,
        LfLogLevel.Debug => LfStatusVisual.Neutral,
        LfLogLevel.Information => LfStatusVisual.Running,
        LfLogLevel.Warning => LfStatusVisual.Warning,
        LfLogLevel.Error => LfStatusVisual.Danger,
        LfLogLevel.Critical => LfStatusVisual.Danger,
        _ => LfStatusVisual.Unknown
    };

    /// <summary>级别对应的图标字形。</summary>
    public static string Glyph(LfLogLevel level) => level switch
    {
        LfLogLevel.Trace => "\uE7C3",       // ProgressRing 太重，用 Ruler
        LfLogLevel.Debug => "\uEB41",       // Code
        LfLogLevel.Information => "\uE946", // Info
        LfLogLevel.Warning => "\uE7BA",     // Warning
        LfLogLevel.Error => "\uEA39",       // ErrorBadge
        LfLogLevel.Critical => "\uE7BA",    // 复用警告形状，靠颜色与文字区分级别
        _ => "\uE9CE"
    };

    /// <summary>级别行文本的颜色令牌后缀。</summary>
    public static string TokenSuffix(LfLogLevel level) =>
        StatusPresentation.TokenSuffix(Visual(level));

    /// <summary>
    /// 是否值得加粗强调。
    /// </summary>
    /// <remarks>
    /// 只有 Error 与 Critical 加粗：若 Warning 也加粗，
    /// 用户在满屏日志里就分不出轻重，等于没有强调。
    /// </remarks>
    public static bool IsEmphasized(LfLogLevel level) =>
        level is LfLogLevel.Error or LfLogLevel.Critical;
}
