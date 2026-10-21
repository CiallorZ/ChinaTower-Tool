using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkForge.Application.Diagnostics;

namespace LinkForge.Application.Logging;

/// <summary>
/// 按日滚动的 JSON Lines 结构化日志存储。
/// </summary>
/// <remarks>
/// <para>
/// 选择 JSONL 而不是纯文本：每行是独立 JSON，便于后续过滤、导入与自动化分析，
/// 同时不需要引入额外日志框架。写入前统一经过 <see cref="ITextRedactor"/>，
/// 因此磁盘日志、UI 日志与报告共用同一套敏感信息规则。
/// </para>
/// <para>
/// 读取采用“跳过坏行”的策略：日志文件可能因断电留下半行，
/// 一条损坏记录不能让整个日志面板无法启动。
/// </para>
/// </remarks>
public sealed class JsonLineStructuredLogStore : IStructuredLogStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ITextRedactor _redactor;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 创建日志存储。
    /// </summary>
    /// <param name="directoryPath">日志目录。</param>
    /// <param name="redactor">统一脱敏器。</param>
    /// <param name="timeProvider">时间源；null 时使用系统时钟。</param>
    public JsonLineStructuredLogStore(
        string directoryPath,
        ITextRedactor redactor,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(redactor);

        DirectoryPath = Path.GetFullPath(directoryPath);
        _redactor = redactor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string DirectoryPath { get; }

    /// <inheritdoc />
    public string CurrentFilePath
    {
        get
        {
            var localTime = _timeProvider.GetLocalNow();
            var fileName = $"linkforge-{localTime:yyyyMMdd}.jsonl";

            return Path.Combine(DirectoryPath, fileName);
        }
    }

    /// <inheritdoc />
    public async Task AppendAsync(
        StructuredLogRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var safe = Redact(record);
        var line = JsonSerializer.Serialize(safe, SerializerOptions) + System.Environment.NewLine;

        Directory.CreateDirectory(DirectoryPath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(
                CurrentFilePath,
                line,
                Utf8WithoutBom,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StructuredLogRecord>> ReadRecentAsync(
        int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        if (maximumEntries <= 0)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = CurrentFilePath;
            if (!File.Exists(path))
            {
                return [];
            }

            var lines = await File
                .ReadAllLinesAsync(path, cancellationToken)
                .ConfigureAwait(false);

            var records = new List<StructuredLogRecord>();

            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var parsed = JsonSerializer.Deserialize<StructuredLogRecord>(line, SerializerOptions);
                    if (parsed is not null)
                    {
                        records.Add(Redact(parsed));
                    }
                }
                catch (JsonException)
                {
                    // 半行或损坏记录被跳过，避免一条坏日志拖垮整个日志面板。
                }
            }

            return records.Count <= maximumEntries
                ? records
                : records.Skip(records.Count - maximumEntries).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    private StructuredLogRecord Redact(StructuredLogRecord record) =>
        new(
            record.Timestamp,
            record.Level,
            _redactor.Redact(record.Module) ?? string.Empty,
            _redactor.Redact(record.RunId) ?? string.Empty,
            _redactor.Redact(record.TargetId),
            _redactor.Redact(record.EventId) ?? string.Empty,
            _redactor.Redact(record.Message) ?? string.Empty);
}
