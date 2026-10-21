using LinkForge.Application.Diagnostics;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;
using LinkForge.Plugin.Hosting;

namespace LinkForge.App.Services;

/// <summary>
/// 插件运行边界。
/// </summary>
/// <remarks>
/// 视图模型只依赖这个契约：真实的进程外适配器、清单入口解析与编排器都藏在实现里，
/// 测试才能在不启动子进程的前提下验证"未确认来源不运行、取消不是失败"这些规则。
/// </remarks>
public interface IPluginRunExecutor
{
    /// <summary>该插件在运行前是否需要用户显式确认来源。</summary>
    bool RequiresTrust(PluginDescriptor descriptor);

    /// <summary>
    /// 运行插件声明的全部步骤。
    /// </summary>
    /// <param name="descriptor">已通过扫描校验的插件。</param>
    /// <param name="target">用户提供的目标描述；可为空。</param>
    /// <param name="sourceConfirmed">用户是否已确认插件来源。</param>
    /// <param name="progress">进度上报器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="PluginTrustNotConfirmedException">来源尚未确认。</exception>
    Task<DiagnosticRunResult> RunAsync(
        PluginDescriptor descriptor,
        string? target,
        bool sourceConfirmed,
        IProgress<DiagnosticProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>插件来源未确认（不是执行失败，不得渲染为 Failed）。</summary>
public sealed class PluginTrustNotConfirmedException : InvalidOperationException
{
    /// <summary>创建异常。</summary>
    public PluginTrustNotConfirmedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 使用进程外适配器与统一编排器的真实实现。
/// </summary>
/// <remarks>
/// <para>
/// 三道边界都在这一层：
/// <list type="number">
///   <item>只有 <see cref="PluginDescriptor.IsLoadable"/> 为真的插件才可能启动进程。</item>
///   <item>需要信任确认的插件未确认时直接拒绝，不进入编排器。</item>
///   <item>入口路径由清单重新解析并校验哈希，不接受页面传入的路径。</item>
/// </list>
/// </para>
/// <para>
/// 页面参数只发送 <see cref="OutOfProcessDiagnosticAdapter.AllowedParameters"/> 中的键，
/// 宿主不提供任何凭据输入通道，因此插件不可能从本工具拿到秘密明文。
/// </para>
/// </remarks>
public sealed class PluginRunExecutor : IPluginRunExecutor
{
    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly DiagnosticTaskRunner _runner;

    /// <summary>创建执行器。</summary>
    public PluginRunExecutor(DiagnosticTaskRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    /// <inheritdoc />
    public bool RequiresTrust(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return descriptor.RequiresExplicitTrust;
    }

    /// <inheritdoc />
    public async Task<DiagnosticRunResult> RunAsync(
        PluginDescriptor descriptor,
        string? target,
        bool sourceConfirmed,
        IProgress<DiagnosticProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!descriptor.IsLoadable || descriptor.Manifest is null)
        {
            throw new InvalidOperationException(
                $"插件状态为 {descriptor.Status}，不允许运行。");
        }

        if (descriptor.RequiresExplicitTrust && !sourceConfirmed)
        {
            throw new PluginTrustNotConfirmedException(
                $"插件“{descriptor.Manifest.DisplayName}”的签名状态为" +
                $"{descriptor.Manifest.SignatureStatus}，运行前必须确认来源。");
        }

        // 入口路径与哈希在此重新解析：页面只持有扫描结果，
        // 若把路径当成参数传进来，用户（或一个有缺陷的页面）就能让宿主执行任意可执行文件。
        var (entryPath, _) = await PluginManifestReader
            .ResolveEntryPointAsync(descriptor.PluginDirectory, descriptor.Manifest, cancellationToken)
            .ConfigureAwait(false);

        var adapter = new OutOfProcessDiagnosticAdapter(
            descriptor,
            entryPath,
            descriptor.PluginDirectory,
            HandshakeTimeout);

        var request = new DiagnosticRequest(
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(target) ? descriptor.Manifest.DisplayName : target.Trim(),
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["target"] = string.IsNullOrWhiteSpace(target) ? null : target.Trim()
            },
            DefaultStepTimeout);

        return await _runner
            .RunAsync(
                adapter,
                request,
                [],
                new DiagnosticRunOptions(DefaultStepTimeout, OverallTimeout),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
