using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LinkForge.Plugin.Hosting;

/// <summary>
/// 一次插件子进程会话。
/// </summary>
/// <remarks>
/// 会话严格一进程一请求序列：握手 → 执行 → 退出。不跨步骤复用一个可能已损坏的进程，
/// 避免插件状态泄漏到下一次诊断。进程退出码、标准错误与超时都转换为
/// <see cref="PluginProcessException"/>，不会冒泡为宿主未处理异常。
/// </remarks>
internal sealed class PluginProcessSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private readonly int _maxResponseBytes;
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrLock = new();
    private readonly Task _stderrPump;
    private int _requestCounter;

    private PluginProcessSession(
        Process process,
        int maxResponseBytes)
    {
        _process = process;
        _maxResponseBytes = maxResponseBytes;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        _input.AutoFlush = true;
        _stderrPump = PumpStandardErrorAsync(process.StandardError);
    }

    /// <summary>启动子进程并完成握手。</summary>
    public static async Task<PluginProcessSession> StartAsync(
        string entryPath,
        string workingDirectory,
        PluginDescriptor descriptor,
        TimeSpan handshakeTimeout,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = entryPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        // 固定参数数组，不使用 shell 拼接；也不把任何凭据放进命令行。
        startInfo.ArgumentList.Add("--linkforge-plugin");
        startInfo.ArgumentList.Add("--protocol");
        startInfo.ArgumentList.Add(PluginHostContract.ProtocolVersion);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        PluginProcessSession? session = null;

        try
        {
            if (!process.Start())
            {
                throw new PluginProcessException("插件进程未能启动。", isCrash: false);
            }

            session = new PluginProcessSession(process, maxResponseBytes);

            using var timeoutCts = new CancellationTokenSource(handshakeTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            var payload = JsonSerializer.SerializeToElement(
                new
                {
                    host = "LinkForge",
                    protocol = PluginHostContract.ProtocolVersion,
                    expectedId = descriptor.Manifest!.Id
                },
                PluginProtocolJson.Options);

            var response = await session
                .SendAsync(PluginProtocolMethods.Hello, payload, linked.Token)
                .ConfigureAwait(false);

            if (!response.Ok || response.Result is null)
            {
                throw new PluginProcessException(
                    $"插件握手失败：{response.Error ?? "未返回握手结果"}",
                    isCrash: false);
            }

            var hello = response.Result.Value.Deserialize<PluginHello>(PluginProtocolJson.Options);

            if (hello is null
                || !string.Equals(hello.Id, descriptor.Manifest.Id, StringComparison.Ordinal))
            {
                throw new PluginProcessException(
                    $"插件进程报告的标识“{hello?.Id ?? "<空>"}”与清单“{descriptor.Manifest.Id}”不一致，已终止。",
                    isCrash: false);
            }

            return session;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                TryKill(process);
                process.Dispose();
            }

            throw new PluginProcessException(
                $"插件在 {handshakeTimeout.TotalSeconds:0.#} 秒内未完成握手。",
                isCrash: false);
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                TryKill(process);
                process.Dispose();
            }

            throw;
        }
    }

    /// <summary>发送一条请求并读取对应响应。</summary>
    public async Task<PluginResponse> SendAsync(
        string method,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref _requestCounter).ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        var request = new PluginRequest(
            PluginHostContract.ProtocolVersion,
            method,
            requestId,
            payload);

        var line = JsonSerializer.Serialize(request, PluginProtocolJson.Options);

        try
        {
            await _input.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw CreateProcessFailure("写入插件进程失败");
        }

        var responseLine = await ReadLimitedLineAsync(cancellationToken).ConfigureAwait(false);

        if (responseLine is null)
        {
            throw CreateProcessFailure("插件进程未返回响应");
        }

        PluginResponse? response;

        try
        {
            response = JsonSerializer.Deserialize<PluginResponse>(
                responseLine, PluginProtocolJson.Options);
        }
        catch (JsonException ex)
        {
            throw new PluginProcessException(
                $"插件返回了无效 JSON：{ex.Message}",
                isCrash: false,
                ex);
        }

        if (response is null
            || !string.Equals(response.Protocol, PluginHostContract.ProtocolVersion, StringComparison.Ordinal)
            || !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new PluginProcessException(
                "插件响应缺少协议版本或请求标识不匹配，已拒绝该响应。",
                isCrash: false);
        }

        return response;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _input.Close();

                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                try
                {
                    await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(_process);
                }
            }

            await _stderrPump.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or IOException)
        {
            TryKill(_process);
        }
        finally
        {
            _input.Dispose();
            _output.Dispose();
            _process.Dispose();
        }
    }

    private async Task<string?> ReadLimitedLineAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];

        while (true)
        {
            var read = await _output.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];

                if (ch == '\n')
                {
                    return builder.ToString().TrimEnd('\r');
                }

                builder.Append(ch);

                // 按 UTF-16 字符计数的上限是宽松的，但足以阻止无换行的无限响应
                // 把宿主内存吃光；真正的字节上限由插件进程输出编码决定。
                if (builder.Length > _maxResponseBytes)
                {
                    throw new PluginProcessException(
                        $"插件响应超过 {_maxResponseBytes} 字符上限，已终止读取。",
                        isCrash: false);
                }
            }
        }
    }

    private PluginProcessException CreateProcessFailure(string prefix)
    {
        var exitText = "仍在运行";

        try
        {
            if (_process.WaitForExit(250))
            {
                exitText = $"退出码 {_process.ExitCode}";
            }
        }
        catch (InvalidOperationException)
        {
            exitText = "已释放";
        }

        // 让 stderr 读取协程有机会把最后一段输出带回；
        // 但不无限等待，避免一个有问题的子进程拖住宿主。
        string stderr;

        lock (_stderrLock)
        {
            stderr = _stderr.ToString().Trim();
        }
        stderr = stderr.Length <= 2000 ? stderr : stderr[..2000] + "…";

        return new PluginProcessException(
            $"{prefix}（{exitText}）"
            + (stderr.Length > 0 ? $"。标准错误：{stderr}" : string.Empty),
            isCrash: true);
    }

    private async Task PumpStandardErrorAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrLock)
                {
                    if (_stderr.Length < 16 * 1024)
                    {
                        _stderr.AppendLine(line);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException
            or ObjectDisposedException
            or InvalidOperationException)
        {
            // 子进程退出时管道关闭是正常路径。
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            // 进程已退出或被系统回收，不需要再处理。
        }
    }
}
