using System.Net;
using System.Net.Sockets;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace LinkForge.Adapters.Mqtt.Tests;

/// <summary>测试专用进程内 MQTT Broker，不依赖 Docker 或公网。</summary>
internal sealed class LocalMqttBroker : IAsyncDisposable
{
    private readonly MqttServer _server;
    private bool _disposed;

    private LocalMqttBroker(MqttServer server, int port)
    {
        _server = server;
        Port = port;
    }

    public int Port { get; }

    public static async Task<LocalMqttBroker> StartAsync(
        string? requiredUser = null,
        string? requiredPassword = null)
    {
        var port = GetFreePort();
        var options = new MqttServerFactory()
            .CreateServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(port)
            .Build();

        var server = new MqttServerFactory().CreateMqttServer(options);

        if (requiredUser is not null)
        {
            server.ValidatingConnectionAsync += args =>
            {
                if (!string.Equals(args.UserName, requiredUser, StringComparison.Ordinal)
                    || !string.Equals(args.Password, requiredPassword, StringComparison.Ordinal))
                {
                    args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                    args.ReasonString = "测试 Broker 拒绝该凭据。";
                }

                return Task.CompletedTask;
            };
        }

        await server.StartAsync();
        return new LocalMqttBroker(server, port);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _server.StopAsync(new MqttServerStopOptions());
        }
        finally
        {
            _server.Dispose();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
