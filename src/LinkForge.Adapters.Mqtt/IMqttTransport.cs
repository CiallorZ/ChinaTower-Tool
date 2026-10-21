namespace LinkForge.Adapters.Mqtt;

/// <summary>
/// MQTT 传输边界。
/// </summary>
/// <remarks>
/// UI 不直接依赖 MQTTnet。传输层只表达连接、断开、订阅、发布和消息流，
/// 凭据、日志、导出与 UI 状态由上层负责。
/// </remarks>
public interface IMqttTransport : IAsyncDisposable
{
    /// <summary>当前是否已连接；断线后必须立即变为 false。</summary>
    bool IsConnected { get; }

    /// <summary>收到消息；回调在线程池上触发，订阅方负责投递到 UI 线程。</summary>
    event EventHandler<MqttReceivedMessage>? MessageReceived;

    /// <summary>连接状态变化；断线、认证失败等都通过状态回调表达。</summary>
    event EventHandler<MqttConnectionStatusChanged>? ConnectionStatusChanged;

    /// <summary>连接 Broker。</summary>
    Task ConnectAsync(MqttConnectionOptions options, CancellationToken cancellationToken = default);

    /// <summary>断开连接；未连接时安全返回。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>订阅一个或多个 Theme，返回每个 Topic 的授权结果。</summary>
    Task<IReadOnlyList<MqttSubscribeResult>> SubscribeAsync(
        IReadOnlyList<MqttSubscription> subscriptions,
        CancellationToken cancellationToken = default);

    /// <summary>发布一条消息。</summary>
    Task PublishAsync(MqttPublishRequest request, CancellationToken cancellationToken = default);
}

/// <summary>连接状态快照。</summary>
public sealed record MqttConnectionStatusChanged(bool IsConnected, string Summary, string? Detail = null);
