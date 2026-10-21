namespace LinkForge.Adapters.Mqtt;

/// <summary>MQTT 传输异常分类。</summary>
public enum MqttFailureKind
{
    /// <summary>输入参数非法。</summary>
    InvalidOptions,

    /// <summary>连接超时。</summary>
    TimedOut,

    /// <summary>认证失败。</summary>
    AuthenticationFailed,

    /// <summary>网络不可达、TLS 失败或其他连接失败。</summary>
    ConnectionFailed,

    /// <summary>订阅被 Broker 拒绝。</summary>
    SubscriptionFailed,

    /// <summary>发布失败。</summary>
    PublishFailed
}

/// <summary>携带可读原因和分类的 MQTT 异常。</summary>
public sealed class MqttTransportException : Exception
{
    /// <summary>创建异常。</summary>
    public MqttTransportException(
        MqttFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>失败分类。</summary>
    public MqttFailureKind Kind { get; }
}
