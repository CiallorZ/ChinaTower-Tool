using System.Text.Json;
using System.Text.Json.Serialization;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.Application.Diagnostics;

/// <summary>
/// 诊断运行历史的存储契约。
/// </summary>
/// <remarks>
/// <para>
/// 阶段 04 先用文件级实现（LF-004 会换成 SQLite 仓储，届时本接口不变）。
/// 之所以现在就要有真实存储：工作台的“最近任务”必须来自真实记录，
/// 若只放在内存里，重启后工作台又变回空状态，
/// 用户会以为“刚才跑的任务丢了”。
/// </para>
/// <para>
/// 存储的内容<b>不含</b>请求参数（参数可能含凭据）：
/// 只保留步骤结论、状态、耗时与证据元数据，这些在写入前都已脱敏。
/// </para>
/// </remarks>
public interface IRunHistoryStore
{
    /// <summary>
    /// 读取历史记录（按开始时间倒序）。
    /// </summary>
    /// <param name="limit">最多返回多少条；null 表示不限制。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>历史记录；文件不存在或损坏时返回空列表，不抛异常。</returns>
    Task<IReadOnlyList<DiagnosticRunResult>> LoadAsync(
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 追加一条运行结果（原子写入）。
    /// </summary>
    /// <param name="result">运行结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task AppendAsync(DiagnosticRunResult result, CancellationToken cancellationToken = default);

    /// <summary>清空历史记录。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 诊断历史的 JSON 文件实现。
/// </summary>
/// <remarks>
/// <para>
/// 三条与 <c>AppSettingsStore</c> 一致的原则：
/// 损坏文件不得让应用无法启动（隔离坏文件并回退空列表）、
/// 写入必须原子（临时文件 + 替换）、
/// 读取必须钳制（历史条数上限，避免文件无限增长）。
/// </para>
/// <para>
/// 历史文件里出现的所有面向用户文本都已在写入前脱敏
/// （步骤结论由 <see cref="DiagnosticTaskRunner"/> 统一过 <c>ITextRedactor</c>），
/// 本类不重复脱敏，也不提供“写入原始文本”的入口。
/// </para>
/// </remarks>
public sealed class JsonRunHistoryStore : IRunHistoryStore
{
    /// <summary>历史文件默认保留的最大条数。</summary>
    /// <remarks>
    /// 200 条足以覆盖现场一轮排障，同时把文件控制在几百 KB 量级。
    /// 超过后丢弃最旧的记录，而不是拒绝写入 —— 拒绝写入会让用户
    /// 在长时间使用后突然发现“任务跑完了但历史里没有”。
    /// </remarks>
    public const int MaxEntries = 200;

    private const string FileName = "run-history.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 创建历史存储。
    /// </summary>
    /// <param name="directory">存放目录（通常是 <c>%LOCALAPPDATA%\LinkForge</c>）。</param>
    public JsonRunHistoryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.Combine(directory, FileName);
    }

    /// <summary>历史文件完整路径（诊断与测试用）。</summary>
    public string FilePath => _filePath;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiagnosticRunResult>> LoadAsync(
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var entries = Deserialize(json);

            var ordered = entries
                .OrderByDescending(static e => e.StartedAt)
                .ToList();

            return limit is { } max && max > 0 && ordered.Count > max
                ? [.. ordered.Take(max)]
                : ordered;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 历史损坏不应阻断启动：隔离现场后当作“暂无历史”。
            QuarantineCorruptFile();
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task AppendAsync(DiagnosticRunResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var existing = File.Exists(_filePath)
                ? Deserialize(await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false))
                : [];

            var merged = new List<DiagnosticRunResult>(existing.Count + 1) { result };
            merged.AddRange(existing.OrderByDescending(static e => e.StartedAt).Take(MaxEntries - 1));

            await WriteAtomicallyAsync(merged, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 读取旧文件失败时不能把新记录一起丢掉：
            // 隔离坏文件后重写一份只含本次结果的干净历史。
            QuarantineCorruptFile();
            await WriteAtomicallyAsync([result], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败不抛：调用方（设置页/工作台）会把“清空后仍有记录”如实呈现，
            // 抛异常只会得到一个没有上下文的中断。
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<DiagnosticRunResult> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<DiagnosticRunResult>>(json, SerializerOptions) ?? [];

    private async Task WriteAtomicallyAsync(
        IReadOnlyList<DiagnosticRunResult> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _filePath + ".tmp";

        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(entries, SerializerOptions),
            cancellationToken).ConfigureAwait(false);

        // File.Move(overwrite) 在 NTFS 上是替换语义，不会留下半写文件。
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            var stamp = DateTimeOffset.Now.ToString(
                "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture);

            File.Move(_filePath, $"{_filePath}.corrupt-{stamp}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 连改名都失败时放弃保留，但不能让调用方拿到异常。
        }
    }
}