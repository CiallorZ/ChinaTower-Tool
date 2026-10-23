using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LinkForge.Adapters.Onvif.Tests;

internal sealed class LocalSoapServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Func<HttpListenerContext, Task> _handler;

    public LocalSoapServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;

        var port = GetFreePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/onvif/device_service");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.IgnoreWriteExceptions = true;
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseUri { get; }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Close();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }

        _cts.Dispose();
    }

    public static async Task WriteAsync(
        HttpListenerContext context,
        int status,
        string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/soap+xml; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException) when (!_listener.IsListening)
            {
                return;
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await _handler(context).ConfigureAwait(false);
                    }
                    catch
                    {
                        try
                        {
                            context.Response.StatusCode = 500;
                            context.Response.Close();
                        }
                        catch
                        {
                        }
                    }
                },
                _cts.Token);
        }
    }

    private static int GetFreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
