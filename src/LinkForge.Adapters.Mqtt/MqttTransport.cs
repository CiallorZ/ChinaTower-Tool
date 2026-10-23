using System.Net.Sockets;
using System.Buffers;
using System.Text;
using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace LinkForge.Adapters.Mqtt;

/// <summary>基于 MQTTnet 的客户端传输实现。</summary>
/// <remarks>
/// 本类型只负责协议与连接生命周期，不持有 UI 状态、不写历史、不做导出。
/// TLS 证书验证不可关闭；MQTT 3.1.1/5.0 由上层按目标 Broker 选择。
/// </remarks>
public sealed class MqttTransport : IMqttTransport, IDisposable
{
    private readonly IMqttClient _client;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _disposed;

    /// <summary>创建传输实例。</summary>
    public MqttTransport()
    {
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <inheritdoc />
    public bool IsConnected => _client.IsConnected;

    /// <inheritdoc />
    public event EventHandler<MqttReceivedMessage>? MessageReceived;

    /// <inheritdoc />
    public event EventHandler<MqttConnectionStatusChanged>? ConnectionStatusChanged;

    /// <inheritdoc />
    public async Task ConnectAsync(
        MqttConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateOptions(options);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_client.IsConnected)
            {
                throw new InvalidOperationException("MQTT 客户端已连接，请先断开。");
            }

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(options.Host, options.Port)
                .WithClientId(options.ClientId)
                .WithProtocolVersion(options.ProtocolMode switch
                {
                    MqttProtocolMode.V311 => MqttProtocolVersion.V311,
                    MqttProtocolMode.V500 => MqttProtocolVersion.V500,
                    _ => throw new MqttTransportException(
                        MqttFailureKind.InvalidOptions,
                        "MQTT 协议版本无效。")
                })
                .WithCleanStart(true)
                .WithTimeout(options.EffectiveConnectTimeout)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

            if (!string.IsNullOrEmpty(options.Username))
            {
                builder.WithCredentials(options.Username, options.Password ?? string.Empty);
            }

            if (options.UseTls)
            {
                builder.WithTlsOptions(tls =>
                {
                    tls.UseTls();
                    tls.WithTargetHost(options.Host);
                    tls.WithAllowUntrustedCertificates(false);
                    tls.WithIgnoreCertificateChainErrors(false);
                    tls.WithIgnoreCertificateRevocationErrors(false);
                });
            }

            try
            {
                var result = await _client
                    .ConnectAsync(builder.Build(), cancellationToken)
                    .ConfigureAwait(false);

                if (result.ResultCode != MqttClientConnectResultCode.Success)
                {
                    throw MapConnectResult(result.ResultCode);
                }
            }
            catch (MqttTransportException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MqttConnectingFailedException ex)
            {
                throw MapConnectingFailure(ex, options.EffectiveConnectTimeout);
            }
            catch (SocketException ex)
            {
                throw new MqttTransportException(
                    MqttFailureKind.ConnectionFailed,
                    $"无法连接 {options.Host}:{options.Port}：{ex.Message}",
                    ex);
            }
            catch (Exception ex) when (ex is not MqttTransportException)
            {
                throw new MqttTransportException(
                    MqttFailureKind.ConnectionFailed,
                    $"MQTT 连接失败：{ex.Message}",
                    ex);
            }

            ConnectionStatusChanged?.Invoke(
                this,
                new MqttConnectionStatusChanged(
                    true,
                    $"已连接到 {options.Host}:{options.Port}"));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_client.IsConnected)
        {
            return;
        }

        await _client
            .DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MqttSubscribeResult>> SubscribeAsync(
        IReadOnlyList<MqttSubscription> subscriptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_client.IsConnected)
        {
            throw new MqttTransportException(
                MqttFailureKind.ConnectionFailed,
                "MQTT 尚未连接，不能订阅。");
        }

        var builder = new MqttClientSubscribeOptionsBuilder();

        foreach (var subscription in subscriptions)
        {
            builder.WithTopicFilter(
                subscription.TopicFilter,
                QualityOfServiceLevel(subscription.QualityOfService));
        }

        MqttClientSubscribeResult result;

        try
        {
            result = await _client
                .SubscribeAsync(builder.Build(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MqttTransportException(
                MqttFailureKind.SubscriptionFailed,
                $"订阅失败：{ex.Message}",
                ex);
        }

        var items = result.Items
            .Select(item =>
            {
                var granted = item.ResultCode is MqttClientSubscribeResultCode.GrantedQoS0
                    ? 0
                    : item.ResultCode is MqttClientSubscribeResultCode.GrantedQoS1
                        ? 1
                        : item.ResultCode is MqttClientSubscribeResultCode.GrantedQoS2
                            ? 2
                            : -1;

                return new MqttSubscribeResult(
                    item.TopicFilter.Topic,
                    granted,
                    item.ResultCode.ToString());
            })
            .ToList();

        if (items.Any(static item => item.GrantedQualityOfService < 0))
        {
            throw new MqttTransportException(
                MqttFailureKind.SubscriptionFailed,
                string.Join("；", items.Select(static item => $"{item.TopicFilter}: {item.Reason}")));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        MqttPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_client.IsConnected)
        {
            throw new MqttTransportException(
                MqttFailureKind.ConnectionFailed,
                "MQTT 尚未连接，不能发布。");
        }

        if (string.IsNullOrWhiteSpace(request.Topic)
            || request.Topic.Contains('#', StringComparison.Ordinal)
            || request.Topic.Contains('+', StringComparison.Ordinal))
        {
            throw new MqttTransportException(
                MqttFailureKind.InvalidOptions,
                "发布 Topic 不能为空，也不能包含通配符 # 或 +。");
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(request.Topic)
            .WithPayload(request.Payload)
            .WithQualityOfServiceLevel(QualityOfServiceLevel(request.QualityOfService))
            .WithRetainFlag(request.Retain)
            .Build();

        try
        {
            var result = await _client
                .PublishAsync(message, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                throw new MqttTransportException(
                    MqttFailureKind.PublishFailed,
                    $"发布失败：{result.ReasonCode} {result.ReasonString}".Trim());
            }
        }
        catch (MqttTransportException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MqttTransportException(
                MqttFailureKind.PublishFailed,
                $"发布失败：{ex.Message}",
                ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
        _client.DisconnectedAsync -= OnDisconnectedAsync;

        try
        {
            if (_client.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _client.DisconnectAsync(
                    new MqttClientDisconnectOptionsBuilder().Build(),
                    timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // 释放路径不允许因对端已断开而抛出；连接状态由 Disconnected 事件表达。
        }
        finally
        {
            _client.Dispose();
            _operationLock.Dispose();
        }
    }

    /// <summary>
    /// 同步释放包装，供 DI 容器在应用退出时安全调用。
    /// </summary>
    /// <remarks>
    /// WPF 的 <c>Application.OnExit</c> 是同步方法，而本类型的释放路径包含关闭长连接；
    /// 这里委托给 <see cref="DisposeAsync"/>。正常连接/断开仍由视图模型异步取消，
    /// 此入口只处理进程退出时因外部断线或未断开连接造成的残余客户端。
    /// </remarks>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var message = args.ApplicationMessage;
        var payload = Encoding.UTF8.GetString(message.Payload.ToArray());

        MessageReceived?.Invoke(
            this,
            new MqttReceivedMessage(
                DateTimeOffset.Now,
                message.Topic,
                payload,
                (int)message.QualityOfServiceLevel,
                message.Retain,
                Encoding.UTF8.GetByteCount(payload)));

        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        var detail = args.Exception?.Message ?? args.ReasonString ?? args.Reason.ToString();

        ConnectionStatusChanged?.Invoke(
            this,
            new MqttConnectionStatusChanged(false, "MQTT 连接已断开。", detail));

        return Task.CompletedTask;
    }

    private static void ValidateOptions(MqttConnectionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new MqttTransportException(MqttFailureKind.InvalidOptions, "Broker 地址不能为空。");
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new MqttTransportException(MqttFailureKind.InvalidOptions, "端口必须在 1–65535 之间。");
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new MqttTransportException(MqttFailureKind.InvalidOptions, "Client ID 不能为空。");
        }

        if (!Enum.IsDefined(options.ProtocolMode))
        {
            throw new MqttTransportException(MqttFailureKind.InvalidOptions, "MQTT 协议版本无效。");
        }
    }

    private static MqttTransportException MapConnectResult(MqttClientConnectResultCode code) =>
        code switch
        {
            MqttClientConnectResultCode.BadUserNameOrPassword
                or MqttClientConnectResultCode.NotAuthorized
                or MqttClientConnectResultCode.Banned =>
                new MqttTransportException(
                    MqttFailureKind.AuthenticationFailed,
                    $"Broker 拒绝连接：{code}。请检查用户名、密码或访问策略。"),
            _ => new MqttTransportException(
                MqttFailureKind.ConnectionFailed,
                $"Broker 返回连接失败：{code}。")
        };

    private static MqttTransportException MapConnectingFailure(
        MqttConnectingFailedException exception,
        TimeSpan timeout)
    {
        var text = exception.ToString();

        if (text.Contains("BadUserNameOrPassword", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return new MqttTransportException(
                MqttFailureKind.AuthenticationFailed,
                "Broker 拒绝认证，请检查用户名、密码或访问策略。",
                exception);
        }

        if (text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return new MqttTransportException(
                MqttFailureKind.TimedOut,
                $"连接在 {timeout.TotalSeconds:0} 秒内未完成。",
                exception);
        }

        return new MqttTransportException(
            MqttFailureKind.ConnectionFailed,
            $"无法连接 MQTT Broker：{exception.Message}",
            exception);
    }

    private static MqttQualityOfServiceLevel QualityOfServiceLevel(int qualityOfService) =>
        qualityOfService switch
        {
            0 => MqttQualityOfServiceLevel.AtMostOnce,
            1 => MqttQualityOfServiceLevel.AtLeastOnce,
            2 => MqttQualityOfServiceLevel.ExactlyOnce,
            _ => throw new MqttTransportException(
                MqttFailureKind.InvalidOptions,
                "QoS 只能是 0、1 或 2。")
        };
}
