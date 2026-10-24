using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LinkForge.Adapters.Security.Tests;

public class WindowsSecurityProbeTests
{
    [Fact]
    public async Task Open_local_tcp_port_should_be_reported_as_open_with_resolved_address()
    {
        await using var server = await LocalTcpServer.StartAsync();
        var probe = new WindowsSecurityProbe();

        var observation = await probe.RunAsync(
            Request(server.Port),
            progress: null,
            CancellationToken.None);

        observation.Status.ShouldBe(SecurityObservationStatus.Passed);
        observation.Ports.ShouldHaveSingleItem().State.ShouldBe(SecurityPortState.Open);
        observation.Facts.ShouldContain(fact => fact.Label == "实际解析地址" && fact.Value == "127.0.0.1");
        observation.Summary.ShouldContain("不代表漏洞扫描或渗透测试");
    }

    [Fact]
    public async Task Self_signed_certificate_must_be_rejected_by_default_validation()
    {
        await using var server = await LocalTlsServer.StartAsync();
        var probe = new WindowsSecurityProbe();

        var observation = await probe.RunAsync(
            Request(server.Port, tlsPort: server.Port),
            progress: null,
            CancellationToken.None);

        observation.Status.ShouldBe(SecurityObservationStatus.Warning);
        var port = observation.Ports.ShouldHaveSingleItem();
        port.State.ShouldBe(SecurityPortState.Open);
        port.Tls.ShouldNotBeNull().State.ShouldBe(SecurityTlsState.CertificateRejected);
        port.Tls.Summary.ShouldContain("证书未通过默认校验");
        port.Tls.Facts.ShouldContain(fact => fact.Label == "证书校验");
    }

    [Fact]
    public async Task Pre_cancelled_probe_should_not_turn_cancellation_into_failure()
    {
        var probe = new WindowsSecurityProbe();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            probe.RunAsync(Request(443), progress: null, cts.Token));
    }

    [Fact]
    public async Task Empty_port_scope_should_fail_with_actionable_guidance()
    {
        var probe = new WindowsSecurityProbe();

        var observation = await probe.RunAsync(
            new SecurityBaselineRequest(
                "127.0.0.1",
                [],
                [],
                TimeSpan.FromSeconds(1),
                1,
                TimeSpan.Zero),
            progress: null,
            CancellationToken.None);

        observation.Status.ShouldBe(SecurityObservationStatus.Failed);
        observation.Suggestion.ShouldNotBeNull().ShouldContain("显式输入");
    }

    private static SecurityBaselineRequest Request(
        int port,
        int? tlsPort = null) =>
        new(
            "127.0.0.1",
            [port],
            tlsPort is { } value ? [value] : [],
            TimeSpan.FromSeconds(2),
            1,
            TimeSpan.Zero);

    private sealed class LocalTcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;

        private LocalTcpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptAsync(_stop.Token);
        }

        public int Port { get; }

        public static Task<LocalTcpServer> StartAsync()
        {
            return Task.FromResult(new LocalTcpServer());
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();

            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class LocalTlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly Task _acceptLoop;

        private LocalTlsServer()
        {
            _certificate = CreateSelfSignedCertificate();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptAsync(_stop.Token);
        }

        public int Port { get; }

        public static Task<LocalTlsServer> StartAsync()
        {
            return Task.FromResult(new LocalTlsServer());
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();

            try
            {
                await _acceptLoop;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException
                    or SocketException
                    or AuthenticationException
                    or IOException)
            {
            }

            _certificate.Dispose();
            _stop.Dispose();
        }

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        private static X509Certificate2 CreateSelfSignedCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());

            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(
                generated.Export(X509ContentType.Pkcs12),
                password: null);
        }
    }
}
