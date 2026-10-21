using LinkForge.Application.Diagnostics;

namespace LinkForge.Application.Logging;

/// <summary>
/// 结构化日志级别。
/// </summary>
/// <remarks>
/// 该枚举位于 Application 层，供磁盘日志与上层适配共同使用；
/// UI 层可以保留自己的展示枚举，但持久化格式不依赖 WPF。
/// </remarks>
public enum StructuredLogLevel
{
    /// <summary>最细粒度步骤。</summary>
    Trace,

    /// <summary>开发排障信息。</summary>
    Debug,

    /// <summary>常规运行信息。</summary>
    Information,

    /// <summary>异常但可继续。</summary>
    Warning,

    /// <summary>操作失败。</summary>
    Error,

    /// <summary>无法继续的致命错误。</summary>
    Critical
}

/// <summary>
/// 一条已脱敏的结构化日志记录。
/// </summary>
/// <param name="Timestamp">发生时间。</param>
/// <param name="Level">级别。</param>
/// <param name="Module">模块标识。</param>
/// <param name="RunId">运行标识，用于关联诊断证据。</param>
/// <param name="TargetId">目标标识；无可为空。</param>
/// <param name="EventId">稳定事件标识。</param>
/// <param name="Message">已脱敏消息。</param>
/// <remarks>
/// 记录本身只承载脱敏后的值。额外的写入前脱敏仍在
/// <see cref="JsonLineStructuredLogStore"/> 中执行，防止调用方绕过工厂方法。
/// </remarks>
public sealed record StructuredLogRecord(
    DateTimeOffset Timestamp,
    StructuredLogLevel Level,
    string Module,
    string RunId,
    string? TargetId,
    string EventId,
    string Message)
{
    /// <summary>
    /// 创建一条经过统一脱敏器处理的结构化记录。
    /// </summary>
    public static StructuredLogRecord CreateRedacted(
        DateTimeOffset timestamp,
        StructuredLogLevel level,
        string module,
        string runId,
        string? targetId,
        string eventId,
        string message,
        ITextRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);

        return new StructuredLogRecord(
            timestamp,
            level,
            redactor.Redact(module) ?? string.Empty,
            redactor.Redact(runId) ?? string.Empty,
            redactor.Redact(targetId),
            redactor.Redact(eventId) ?? string.Empty,
            redactor.Redact(message) ?? string.Empty);
    }
}

/// <summary>
/// 结构化日志文件存储契约。
/// </summary>
public interface IStructuredLogStore
{
    /// <summary>日志目录。</summary>
    string DirectoryPath { get; }

    /// <summary>当前日期的日志文件路径。</summary>
    string CurrentFilePath { get; }

    /// <summary>追加一条日志；实现必须在写入前再次脱敏。</summary>
    Task AppendAsync(StructuredLogRecord record, CancellationToken cancellationToken = default);

    /// <summary>读取当前日志文件的最近记录。</summary>
    Task<IReadOnlyList<StructuredLogRecord>> ReadRecentAsync(
        int maximumEntries,
        CancellationToken cancellationToken = default);
}
