using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LinkForge.Adapters.Http.Tests;

/// <summary>
/// 本地可控 HTTP 服务；每个测试只监听回环地址，不依赖公网。
/// </summary>
internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Func<HttpListenerContext, Task> _handler;

    public LocalHttpServer(Func<HttpListenerContext, Task> handler)
    {
        _handler = handler;

        var port = GetFreePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
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
        string body,
        string contentType = "text/plain; charset=utf-8",
        IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;

        foreach (var (name, value) in headers ?? [])
        {
            context.Response.Headers[name] = value;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    /// <summary>写入固定字节数，用于构造超过客户端上限的响应。</summary>
    public static async Task WriteLargeAsync(
        HttpListenerContext context,
        int totalBytes,
        string contentType = "text/plain; charset=utf-8")
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = totalBytes;

        var chunk = Encoding.ASCII.GetBytes(new string('x', 4096));
        var remaining = totalBytes;

        while (remaining > 0)
        {
            var count = Math.Min(chunk.Length, remaining);
            await context.Response.OutputStream.WriteAsync(chunk.AsMemory(0, count));
            remaining -= count;
        }

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
