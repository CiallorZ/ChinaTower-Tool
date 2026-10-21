using MQTTnet.Server;

namespace LinkForge.Adapters.Mqtt.Tests;

/// <summary>MQTT 传输边界集成测试。</summary>
public class MqttTransportTests
{
    [Fact]
    public async Task Subscribe_publish_and_disconnect_should_use_a_real_local_broker()
    {
        await using var broker = await LocalMqttBroker.StartAsync();
        await using var transport = new MqttTransport();
        var received = new TaskCompletionSource<MqttReceivedMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        transport.MessageReceived += (_, message) => received.TrySetResult(message);

        await transport.ConnectAsync(Options(broker.Port));
        transport.IsConnected.ShouldBeTrue();

        var subscriptions = await transport.SubscribeAsync(
            [new MqttSubscription("linkforge/test", 1)]);

        subscriptions.ShouldHaveSingleItem().GrantedQualityOfService.ShouldBe(1);

        await transport.PublishAsync(
            new MqttPublishRequest("linkforge/test", "{\"online\":true}", 1));

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        message.Topic.ShouldBe("linkforge/test");
        message.Payload.ShouldBe("{\"online\":true}");
        message.QualityOfService.ShouldBe(1);

        await transport.DisconnectAsync();
        transport.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public async Task Authentication_failure_should_be_reported_as_authentication_failed()
    {
        await using var broker = await LocalMqttBroker.StartAsync("device", "correct");
        await using var transport = new MqttTransport();

        var exception = await Should.ThrowAsync<MqttTransportException>(
            () => transport.ConnectAsync(Options(broker.Port, "device", "wrong")));

        exception.Kind.ShouldBe(MqttFailureKind.AuthenticationFailed);
        transport.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public async Task Broker_disconnect_should_update_connection_state()
    {
        var broker = await LocalMqttBroker.StartAsync();
        await using var transport = new MqttTransport();
        var stateChanged = new TaskCompletionSource<MqttConnectionStatusChanged>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        transport.ConnectionStatusChanged += (_, status) =>
        {
            if (!status.IsConnected)
            {
                stateChanged.TrySetResult(status);
            }
        };

        try
        {
            await transport.ConnectAsync(Options(broker.Port));
            transport.IsConnected.ShouldBeTrue();

            await broker.DisposeAsync();

            var status = await stateChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            status.IsConnected.ShouldBeFalse();
            transport.IsConnected.ShouldBeFalse();
        }
        finally
        {
            await broker.DisposeAsync();
        }
    }

    [Fact]
    public async Task Subscribe_without_connection_should_fail_explicitly()
    {
        await using var transport = new MqttTransport();

        var exception = await Should.ThrowAsync<MqttTransportException>(
            () => transport.SubscribeAsync([new MqttSubscription("#", 0)]));

        exception.Kind.ShouldBe(MqttFailureKind.ConnectionFailed);
    }

    [Fact]
    public async Task Publish_topic_wildcards_should_be_rejected_before_send()
    {
        await using var broker = await LocalMqttBroker.StartAsync();
        await using var transport = new MqttTransport();

        await transport.ConnectAsync(Options(broker.Port));

        var exception = await Should.ThrowAsync<MqttTransportException>(
            () => transport.PublishAsync(new MqttPublishRequest("camera/+/status", "x")));

        exception.Kind.ShouldBe(MqttFailureKind.InvalidOptions);
    }

    [Fact]
    public async Task Connect_should_validate_host_and_port()
    {
        await using var transport = new MqttTransport();

        var exception = await Should.ThrowAsync<MqttTransportException>(
            () => transport.ConnectAsync(
                new MqttConnectionOptions("", 1883, "client", UseTls: false)));

        exception.Kind.ShouldBe(MqttFailureKind.InvalidOptions);
    }

    [Fact]
    public async Task Dispose_should_be_idempotent()
    {
        var transport = new MqttTransport();

        await transport.DisposeAsync();
        await transport.DisposeAsync();
    }

    [Fact]
    public void Sync_dispose_should_satisfy_di_container_shutdown()
    {
        var transport = new MqttTransport();

        transport.Dispose();
        transport.Dispose();
    }

    private static MqttConnectionOptions Options(
        int port,
        string? username = null,
        string? password = null) =>
        new(
            "127.0.0.1",
            port,
            "linkforge-test-" + Guid.NewGuid().ToString("N"),
            UseTls: false,
            Username: username,
            Password: password,
            ConnectTimeout: TimeSpan.FromSeconds(3));
}
