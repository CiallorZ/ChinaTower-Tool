using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Plugin.Hosting;

/// <summary>
/// 进程外插件代理适配器。
/// </summary>
/// <remarks>
/// <para>
/// 本类<b>不加载插件程序集</b>：每次执行通过 <see cref="ProcessStartInfo.ArgumentList"/>
/// 启动清单指定的入口程序，用标准输入/输出交换单行 JSON。
/// 因此插件内的原生 SDK 崩溃、AccessViolation 或死循环只会终止子进程，
/// 不会把宿主 WPF 进程带走。
/// </para>
/// <para>
/// 参数白名单：只发送 <see cref="AllowedParameters"/> 中的键。凭据引用
/// （<c>secretRef</c>）仅作为引用键下发，宿主不会把秘密库对象或秘密明文交给插件；
/// 插件若需要秘密，必须在后续版本通过显式的受限解码通道申请。
/// </para>
/// </remarks>
public sealed class OutOfProcessDiagnosticAdapter : IDiagnosticAdapter
{
    /// <summary>允许发送给插件的参数键（大小写不敏感）。</summary>
    public static IReadOnlySet<string> AllowedParameters { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "target",
            "host",
            "port",
            "protocol",
            "timeoutSeconds",
            "secretRef",
            "capabilityProbe"
        };

    private readonly PluginDescriptor _descriptor;
    private readonly string _entryPath;
    private readonly TimeSpan _handshakeTimeout;
    private readonly int _maxResponseBytes;
    private readonly string _workingDirectory;

    /// <summary>创建进程外适配器。</summary>
    /// <param name="descriptor">已扫描且可加载的插件。</param>
    /// <param name="entryPath">入口可执行文件绝对路径。</param>
    /// <param name="workingDirectory">子进程工作目录（插件目录）。</param>
    /// <param name="handshakeTimeout">握手超时。</param>
    /// <param name="maxResponseBytes">单行响应最大字节数。</param>
    public OutOfProcessDiagnosticAdapter(
        PluginDescriptor descriptor,
        string entryPath,
        string workingDirectory,
        TimeSpan? handshakeTimeout = null,
        int maxResponseBytes = 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (!descriptor.IsLoadable || descriptor.Manifest is null)
        {
            throw new InvalidOperationException(
                $"插件 {descriptor.Manifest?.Id ?? Path.GetFileName(descriptor.PluginDirectory)} "
                + $"状态为 {descriptor.Status}，不允许创建适配器。");
        }

        _descriptor = descriptor;
        _entryPath = Path.GetFullPath(entryPath);
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _maxResponseBytes = Math.Clamp(maxResponseBytes, 4096, 8 * 1024 * 1024);
    }

    /// <inheritdoc />
    public string Id => _descriptor.Manifest!.Id;

    /// <inheritdoc />
    public string DisplayName => _descriptor.Manifest!.DisplayName;

    /// <inheritdoc />
    public Version ApiVersion => _descriptor.Manifest!.ApiVersion;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Capabilities => _descriptor.Manifest!.Capabilities;

    /// <summary>插件清单（供插件页展示来源与哈希）。</summary>
    public PluginManifest Manifest => _descriptor.Manifest!;

    /// <summary>
    /// 启动前是否需要用户显式确认插件来源。
    /// </summary>
    /// <remarks>
    /// 清单里的签名字段只是<b>声明</b>，宿主没有证书链验证能力，
    /// 因此无论声明为何都必须确认。把这个判断放在宿主侧而不是页面侧，
    /// 是为了让"未确认就运行"在任何调用路径上都不可能发生。
    /// </remarks>
    public bool RequiresExplicitTrust => _descriptor.RequiresExplicitTrust;

    /// <inheritdoc />
    public IReadOnlyList<DiagnosticStepPlan> Plan(DiagnosticRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 计划来自清单而非子进程：页面在用户点击运行前就要显示步骤，
        // 若为此启动进程，会把"展示计划"变成一次可崩溃的 I/O。
        return _descriptor.Manifest!.Steps
            .Select(static step => new DiagnosticStepPlan(
                step.StepId,
                step.Name,
                step.Dependencies,
                step.Prerequisites,
                step.Timeout))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<DiagnosticStepResult> ExecuteStepAsync(
        DiagnosticStepContext context,
        IProgress<DiagnosticProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var startedAt = context.StartedAt;
        var clock = Stopwatch.StartNew();

        try
        {
            await using var session = await PluginProcessSession
                .StartAsync(
                    _entryPath,
                    _workingDirectory,
                    _descriptor,
                    _handshakeTimeout,
                    _maxResponseBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            progress.Report(new DiagnosticProgress(
                context.RunId,
                context.StepId,
                DiagnosticStepPhase.Running,
                $"已启动隔离进程，正在执行插件步骤“{context.Name}”…",
                DateTimeOffset.Now,
                clock.Elapsed));

            var payload = JsonSerializer.SerializeToElement(
                new
                {
                    runId = context.RunId,
                    stepId = context.StepId,
                    name = context.Name,
                    target = context.TargetLabel,
                    parameters = FilterParameters(context.Parameters)
                },
                PluginProtocolJson.Options);

            var response = await session
                .SendAsync(PluginProtocolMethods.ExecuteStep, payload, cancellationToken)
                .ConfigureAwait(false);

            if (!response.Ok || response.Result is null)
            {
                return DiagnosticStepResults.Failed(
                    context.StepId,
                    context.Name,
                    $"插件返回失败：{response.Error ?? "未知错误"}",
                    startedAt,
                    clock.Elapsed,
                    "查看插件日志或联系插件发布者；宿主已终止该插件进程。");
            }

            return MapResult(context, response.Result.Value, clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PluginProcessException ex)
        {
            return DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                $"插件进程异常：{ex.Message}",
                startedAt,
                clock.Elapsed,
                ex.IsCrash
                    ? "插件进程已崩溃，但主程序未受影响。请检查插件与厂商 SDK 版本后重试。"
                    : "请确认插件入口可执行，并检查插件目录权限后重试。");
        }
    }

    /// <summary>
    /// 仅保留白名单参数，并拒绝疑似秘密的键。
    /// </summary>
    /// <remarks>
    /// 这是第二道边界：即使页面误把密码放进参数表，也不会下发到插件。
    /// 真正需要秘密的插件必须先通过受限引用通道申请，而不是直接收明文。
    /// </remarks>
    internal static Dictionary<string, string?> FilterParameters(
        IReadOnlyDictionary<string, string?> parameters)
    {
        var filtered = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in parameters)
        {
            if (!AllowedParameters.Contains(key) || LooksLikeSecret(key))
            {
                continue;
            }

            filtered[key] = value;
        }

        return filtered;
    }

    private static bool LooksLikeSecret(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("passwd", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secretValue", StringComparison.OrdinalIgnoreCase)
        || key.Contains("tokenValue", StringComparison.OrdinalIgnoreCase)
        || key.Contains("privateKey", StringComparison.OrdinalIgnoreCase)
        || key.Contains("authorization", StringComparison.OrdinalIgnoreCase);

    private static DiagnosticStepResult MapResult(
        DiagnosticStepContext context,
        JsonElement result,
        TimeSpan elapsed)
    {
        var dto = result.Deserialize<PluginStepResultDto>(PluginProtocolJson.Options);

        if (dto is null
            || string.IsNullOrWhiteSpace(dto.Summary)
            || !string.Equals(dto.StepId, context.StepId, StringComparison.Ordinal))
        {
            return DiagnosticStepResults.Unknown(
                context.StepId,
                context.Name,
                "插件返回了无法识别的结果（步骤标识不匹配或摘要为空），未采纳其结论。",
                context.StartedAt,
                elapsed,
                "请联系插件发布者修正协议实现。");
        }

        // 插件文本一律视为不可信来源：先过滤凭据形态，再进入摘要/事实/原始输出。
        // 编排器还会做一次统一脱敏，但那是最后一道防线，不能替代这里的边界过滤。
        var summary = PluginSecretFilter.Redact(dto.Summary);
        var suggestion = PluginSecretFilter.Redact(dto.Suggestion);

        var facts = dto.Facts
            .Take(32)
            .Select(static fact => new DiagnosticFact(
                PluginSecretFilter.Redact(fact.Label) ?? string.Empty,
                PluginSecretFilter.Redact(fact.Value) ?? string.Empty))
            .ToArray();
        var raw = Truncate(PluginSecretFilter.Redact(dto.RawOutput), 64 * 1024);
        summary = Truncate(summary, 2048)!;
        suggestion = Truncate(suggestion, 1024);

        return dto.Status.Trim().ToLowerInvariant() switch
        {
            "passed" => DiagnosticStepResults.Passed(
                context.StepId, context.Name, summary, context.StartedAt, elapsed,
                facts: facts, rawOutput: raw),
            "warning" => DiagnosticStepResults.Warning(
                context.StepId, context.Name, summary, context.StartedAt, elapsed,
                suggestion, facts: facts, rawOutput: raw),
            "failed" => DiagnosticStepResults.Failed(
                context.StepId, context.Name, summary, context.StartedAt, elapsed,
                suggestion, facts: facts, rawOutput: raw),
            "unknown" => DiagnosticStepResults.Unknown(
                context.StepId, context.Name, summary, context.StartedAt, elapsed,
                suggestion, facts, raw),
            "skipped" => DiagnosticStepResults.Skipped(
                context.StepId,
                context.Name,
                string.IsNullOrWhiteSpace(dto.Suggestion) ? summary : dto.Suggestion!),
            "cancelled" => DiagnosticStepResults.Cancelled(
                context.StepId, context.Name, context.StartedAt, elapsed),
            _ => DiagnosticStepResults.Unknown(
                context.StepId,
                context.Name,
                $"插件返回了未知状态“{dto.Status}”，未采纳为通过或失败。",
                context.StartedAt,
                elapsed,
                "请联系插件发布者修正状态映射。")
        };
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength] + "…（已截断）";
}

/// <summary>插件进程协议异常。</summary>
public sealed class PluginProcessException : Exception
{
    /// <summary>创建异常。</summary>
    public PluginProcessException(string message, bool isCrash, Exception? inner = null)
        : base(message, inner) => IsCrash = isCrash;

    /// <summary>是否由子进程崩溃/非零退出引发。</summary>
    public bool IsCrash { get; }
}
