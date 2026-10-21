using System.Text.Json;

namespace LinkForge.TestPluginHost;

/// <summary>
/// 测试用插件进程。
/// </summary>
/// <remarks>
/// 行为由 <c>executionMode</c> 参数控制，仅用于验证宿主边界：
/// 正常返回、未知状态、中文摘要、崩溃、静默超时、回显参数、故意回传凭据。
/// 本进程不访问网络、文件或任何凭据。
/// </remarks>
internal static class Program
{
    private const string PluginId = "test.sample";

    private static async Task<int> Main(string[] args)
    {
        if (!args.Contains("--linkforge-plugin", StringComparer.Ordinal))
        {
            return 64;
        }

        using var stdin = Console.In;
        using var stdout = Console.Out;

        while (await stdin.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString() ?? string.Empty;
            var requestId = root.GetProperty("requestId").GetString() ?? string.Empty;
            var payload = root.GetProperty("payload");

            if (method == "hello")
            {
                WriteResponse(stdout, requestId, ok: true, new
                {
                    id = PluginId,
                    displayName = "测试插件",
                    version = "1.0.0",
                    apiVersion = "1.0",
                    capabilities = new[] { "test.echo" }
                });
                continue;
            }

            if (method == "executeStep")
            {
                var parameters = payload.TryGetProperty("parameters", out var p)
                    ? p
                    : default;
                var mode = parameters.ValueKind == JsonValueKind.Object
                    && parameters.TryGetProperty("capabilityProbe", out var modeElement)
                    ? modeElement.GetString()
                    : "success";

                if (mode == "crash")
                {
                    Environment.FailFast("夹具插件按测试要求崩溃");
                }

                if (mode == "silent")
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));
                    continue;
                }

                var parameterText = parameters.ValueKind == JsonValueKind.Object
                    ? parameters.GetRawText()
                    : "{}";

                if (mode == "leaky")
                {
                    // 模拟一个有缺陷或恶意的插件：把凭据塞进摘要、事实与原始输出。
                    // 这里的字符串不是真实秘密，只用于证明宿主会在出口再过滤一次。
                    WriteResponse(stdout, requestId, ok: true, new
                    {
                        stepId = payload.GetProperty("stepId").GetString(),
                        name = payload.GetProperty("name").GetString(),
                        // 用 warning 而不是 passed：Passed 结论不携带建议字段，
                        // 会漏掉"建议文本里的凭据"这条出口。warning 同时带摘要、建议、事实与原始输出。
                        status = "warning",
                        summary = "登录成功 password=LeakySecret123",
                        suggestion = "请复用 passwd: LeakySecret123 重试",
                        facts = new[]
                        {
                            new { label = "account", value = "admin token=LeakySecret123" }
                        },
                        rawOutput = "SDK trace: Authorization: Bearer LeakySecret123"
                    });
                    continue;
                }

                WriteResponse(stdout, requestId, ok: true, new
                {
                    stepId = payload.GetProperty("stepId").GetString(),
                    name = payload.GetProperty("name").GetString(),
                    status = mode == "unknown" ? "unknown" : "passed",
                    summary = mode == "unknown"
                        ? "夹具插件返回未判定。"
                        : "夹具插件完成；参数白名单检查通过。",
                    suggestion = (string?)null,
                    facts = new[]
                    {
                        new { label = "receivedParameters", value = parameterText }
                    },
                    rawOutput = "camera-01 在线"
                });
                continue;
            }

            WriteResponse(stdout, requestId, ok: false, result: null,
                error: "unsupported method", errorCode: "unsupported_method");
        }

        return 0;
    }

    private static void WriteResponse(
        TextWriter stdout,
        string requestId,
        bool ok,
        object? result,
        string? error = null,
        string? errorCode = null)
    {
        var response = new
        {
            protocol = "1.0",
            requestId,
            ok,
            result,
            error,
            errorCode
        };

        stdout.WriteLine(JsonSerializer.Serialize(response));
        stdout.Flush();
    }
}
