namespace LinkForge.Adapters.Mqtt;

/// <summary>单条 Topic 的订阅结果。</summary>
public sealed record MqttSubscribeResult(string TopicFilter, int GrantedQualityOfService, string Reason);
