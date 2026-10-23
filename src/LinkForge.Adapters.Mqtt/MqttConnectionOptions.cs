namespace LinkForge.Adapters.Mqtt;

/// <summary>MQTT 协议版本。</summary>
public enum MqttProtocolMode
{
    /// <summary>MQTT 3.1.1，兼容现有现场 Broker 的默认选择。</summary>
    V311 = 1,

    /// <summary>MQTT 5.0。</summary>
    V500 = 2
}

/// <summary>MQTT 客户端连接参数。</summary>
/// <remarks>
/// 密码和用户名只保存在本次连接上下文中，不进入日志、历史或导出。
/// 当前阶段不允许通过开关关闭 TLS 证书验证。
/// </remarks>
public sealed record MqttConnectionOptions(
    string Host,
    int Port,
    string ClientId,
    bool UseTls,
    string? Username = null,
    string? Password = null,
    TimeSpan? ConnectTimeout = null,
    MqttProtocolMode ProtocolMode = MqttProtocolMode.V311)
{
    /// <summary>连接超时；未指定时使用 10 秒。</summary>
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? TimeSpan.FromSeconds(10);
}
