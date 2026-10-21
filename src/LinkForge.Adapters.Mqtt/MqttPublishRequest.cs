using System.Text;

namespace LinkForge.Adapters.Mqtt;

/// <summary>一条待发布消息。</summary>
public sealed record MqttPublishRequest(
    string Topic,
    string Payload,
    int QualityOfService = 0,
    bool Retain = false)
{
    /// <summary>UTF-8 载荷字节数（用于 UI 显示，不重复编码正文）。</summary>
    public int PayloadBytes => Encoding.UTF8.GetByteCount(Payload);
}
