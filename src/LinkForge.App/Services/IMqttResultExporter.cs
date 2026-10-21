namespace LinkForge.App.Services;

/// <summary>MQTT 消息复制/导出契约。</summary>
public interface IMqttResultExporter
{
    /// <summary>复制脱敏文本。</summary>
    void Copy(string text);

    /// <summary>导出 JSON/CSV；用户取消时返回 null。</summary>
    Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        string filter,
        CancellationToken cancellationToken = default);
}
