namespace LinkForge.App.Presentation;

/// <summary>MQTT 连接状态。</summary>
public enum MqttUiConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting
}

/// <summary>一条脱敏后的 MQTT 消息；仅保存用于 UI、复制和导出的安全副本。</summary>
/// <remarks>
/// <para>
/// 构造时立即脱敏，且<b>只保留脱敏后的文本</b>：明文载荷不会留在可视化树、
/// 复制内容或导出文件中。
/// </para>
/// <para>
/// 载荷超过 <see cref="DisplayLimit"/> 字符时截断，并通过
/// <see cref="IsPayloadTruncated"/> 与 <see cref="PayloadNotice"/> 明确告知，
/// 而不是静默丢内容。截断同时作用于显示和导出，两者口径一致。
/// </para>
/// </remarks>
public sealed class MqttMessageEntry
{
    /// <summary>单条消息保留的最大字符数。</summary>
    public const int DisplayLimit = 8192;

    private readonly string _displayPayload;

    /// <summary>创建消息条目。</summary>
    public MqttMessageEntry(
        DateTimeOffset receivedAt,
        string topic,
        string payload,
        int qualityOfService,
        bool retain,
        int payloadBytes,
        bool isPublished)
    {
        ReceivedAt = receivedAt;
        Topic = SecretRedactor.Redact(topic) ?? string.Empty;

        var redacted = SecretRedactor.Redact(payload) ?? string.Empty;
        if (redacted.Length > DisplayLimit)
        {
            _displayPayload = redacted[..DisplayLimit];
            IsPayloadTruncated = true;
        }
        else
        {
            _displayPayload = redacted;
        }

        QualityOfService = qualityOfService;
        Retain = retain;
        PayloadBytes = payloadBytes;
        IsPublished = isPublished;
    }

    /// <summary>接收/发送时间。</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>Topic（已脱敏）。</summary>
    public string Topic { get; }

    /// <summary>载荷（已脱敏）。</summary>
    public string DisplayPayload => _displayPayload;

    /// <summary>载荷是否因超长被截断。</summary>
    public bool IsPayloadTruncated { get; }

    /// <summary>截断提示；未截断时为空。</summary>
    public string PayloadNotice => IsPayloadTruncated
        ? $"载荷超过 {DisplayLimit:N0} 字符，显示与导出均已截断。"
        : string.Empty;

    /// <summary>方向：发布/接收。</summary>
    public string DirectionText => IsPublished ? "发布" : "接收";

    /// <summary>是否由当前客户端发布。</summary>
    public bool IsPublished { get; }

    /// <summary>QoS。</summary>
    public int QualityOfService { get; }

    /// <summary>Retain 标志。</summary>
    public bool Retain { get; }

    /// <summary>载荷字节数。</summary>
    public int PayloadBytes { get; }

    /// <summary>时间文本。</summary>
    public string TimeText => ReceivedAt.ToLocalTime()
        .ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>附带元数据。</summary>
    public string MetadataText =>
        $"QoS {QualityOfService} · {(Retain ? "Retain" : "非 Retain")} · {PayloadBytes} B";
}
