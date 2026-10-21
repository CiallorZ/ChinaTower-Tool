using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LinkForge.Adapters.Video.Tests;

/// <summary>
/// 本地可控 RTSP 服务器：只用于验证真实 TCP 与 RTSP 报文，不依赖摄像头或公网。
/// </summary>
internal sealed class LocalRtspServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly bool _requireBasicAuth;
    private readonly string _sdp;
    private readonly bool _silent;
    private readonly List<string> _requests = [];

    private LocalRtspServer(bool requireBasicAuth, string sdp, bool silent)
    {
        _requireBasicAuth = requireBasicAuth;
        _sdp = sdp;
        _silent = silent;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    public int Port { get; }

    public IReadOnlyList<string> Requests => _requests;

    public static LocalRtspServer Start(
        bool requireBasicAuth = false,
        string? sdp = null,
        bool silent = false) =>
        new(requireBasicAuth, sdp ?? ValidSdp(), silent);

    public static string ValidSdp() =>
        "v=0\r\n" +
        "o=- 1 1 IN IP4 127.0.0.1\r\n" +
        "s=LinkForge Test Camera\r\n" +
        "t=0 0\r\n" +
        "m=video 0 RTP/AVP 96\r\n" +
        "a=rtpmap:96 H264/90000\r\n" +
        "a=control:trackID=1\r\n";

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            using var stream = client.GetStream();
            var buffer = new byte[8192];

            for (var i = 0; i < 4; i++)
            {
                var read = await ReadRequestAsync(stream, buffer, cancellationToken).ConfigureAwait(false);

                if (read <= 0)
                {
                    return;
                }

                var request = Encoding.ASCII.GetString(buffer, 0, read);
                _requests.Add(request);

                if (_silent)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var method = request.Split(' ', 2)[0];
                var cseq = HeaderValue(request, "CSeq") ?? "1";
                var authorized = request.Contains("Authorization: Basic ", StringComparison.Ordinal);

                if (_requireBasicAuth && !authorized)
                {
                    await WriteAsync(stream, "401 Unauthorized", cseq, string.Empty, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, "200 OK", cseq, string.Empty, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (method.Equals("DESCRIBE", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, "200 OK", cseq, _sdp, cancellationToken,
                            "Content-Type: application/sdp\r\n")
                        .ConfigureAwait(false);
                    continue;
                }

                await WriteAsync(stream, "454 Session Not Found", cseq, string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<int> ReadRequestAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;

            if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return total;
    }

    private static async Task WriteAsync(
        NetworkStream stream,
        string status,
        string cseq,
        string body,
        CancellationToken cancellationToken,
        string extraHeaders = "")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var response =
            $"RTSP/1.0 {status}\r\n" +
            $"CSeq: {cseq}\r\n" +
            "Server: LinkForge-Test-RTSP\r\n" +
            (status.StartsWith("401", StringComparison.Ordinal)
                ? "WWW-Authenticate: Basic realm=\"LinkForge-Test\"\r\n"
                : string.Empty) +
            extraHeaders +
            $"Content-Length: {bytes.Length}\r\n\r\n" +
            body;

        var encoded = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? HeaderValue(string request, string name)
    {
        foreach (var line in request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');

            if (colon > 0 && line[..colon].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }
}
