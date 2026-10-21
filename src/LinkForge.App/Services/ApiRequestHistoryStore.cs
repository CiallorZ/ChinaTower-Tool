using System.IO;
using System.Text.Json;
using LinkForge.App.Presentation;

namespace LinkForge.App.Services;

/// <summary>
/// API 请求历史存储契约。
/// </summary>
/// <remarks>
/// 历史只保存脱敏副本；本接口不接收完整请求对象，避免调用方误把凭据传进来。
/// </remarks>
public interface IApiRequestHistoryStore
{
    /// <summary>读取历史（最新在前）。</summary>
    IReadOnlyList<ApiHistoryEntry> Load();

    /// <summary>追加一条历史。</summary>
    void Append(ApiHistoryEntry entry);

    /// <summary>清空历史。</summary>
    void Clear();
}

/// <summary>
/// 本地 JSON 历史存储。
/// </summary>
/// <remarks>
/// 写入前再次调用 <see cref="SecretRedactor.Redact"/>，即使调用方已脱敏也不省略：
/// 这是历史文件不落明文凭据的最后一道防线。
/// </remarks>
public sealed class JsonApiRequestHistoryStore : IApiRequestHistoryStore
{
    private const int MaximumEntries = 100;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _gate = new();

    /// <summary>创建历史存储。</summary>
    public JsonApiRequestHistoryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.Combine(directory, "api-history.json");
    }

    /// <inheritdoc />
    public IReadOnlyList<ApiHistoryEntry> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            try
            {
                var entries = JsonSerializer.Deserialize<List<ApiHistoryEntry>>(
                    File.ReadAllText(_filePath),
                    SerializerOptions);

                return (entries ?? [])
                    .Select(static entry => entry with
                    {
                        DisplayUrl = SecretRedactor.Redact(entry.DisplayUrl) ?? string.Empty,
                        StatusText = SecretRedactor.Redact(entry.StatusText)
                    })
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    /// <inheritdoc />
    public void Append(ApiHistoryEntry entry)
    {
        lock (_gate)
        {
            var entries = Load().ToList();
            entries.Insert(
                0,
                entry with
                {
                    DisplayUrl = SecretRedactor.Redact(entry.DisplayUrl) ?? string.Empty,
                    StatusText = SecretRedactor.Redact(entry.StatusText)
                });

            if (entries.Count > MaximumEntries)
            {
                entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);
            }

            Write(entries);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
    }

    private void Write(IReadOnlyList<ApiHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            _filePath,
            JsonSerializer.Serialize(entries, SerializerOptions));
    }
}
