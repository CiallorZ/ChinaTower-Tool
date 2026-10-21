namespace LinkForge.Adapters.Mqtt;

/// <summary>从 Broker 收到的消息快照。</summary>
public sealed record MqttReceivedMessage(
    DateTimeOffset ReceivedAt,
    string Topic,
    string Payload,
    int QualityOfService,
    bool Retain,
    int PayloadBytes);
