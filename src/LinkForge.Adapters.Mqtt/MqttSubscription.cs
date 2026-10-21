namespace LinkForge.Adapters.Mqtt;

/// <summary>一条订阅请求。</summary>
public sealed record MqttSubscription(string TopicFilter, int QualityOfService);
