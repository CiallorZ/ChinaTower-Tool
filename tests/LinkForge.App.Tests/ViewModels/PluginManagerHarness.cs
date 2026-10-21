using System.IO;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;
using LinkForge.Plugin.Hosting;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// 构造 <see cref="PluginManagerViewModel"/> 的测试夹具。
/// </summary>
/// <remarks>
/// 替换的只有"扫描目录"、"运行插件"和"剪贴板/对话框"三个系统边界：
/// 视图模型、编排器语义与脱敏仍是生产实现。真实的进程外执行由
/// <c>LinkForge.Plugin.Hosting.Tests</c> 用真子进程验证，此处不重复启动进程。
/// </remarks>
internal sealed class PluginManagerHarness
{
    public PluginManagerHarness()
    {
        Catalog = new FakePluginCatalogService();
        Executor = new FakePluginRunExecutor();
        Exporter = new FakePluginResultExporter();

        ViewModel = new PluginManagerViewModel(Catalog, Executor, Exporter);
    }

    public FakePluginCatalogService Catalog { get; }

    public FakePluginRunExecutor Executor { get; }

    public FakePluginResultExporter Exporter { get; }

    public PluginManagerViewModel ViewModel { get; }
}

/// <summary>可编排的插件目录扫描服务。</summary>
internal sealed class FakePluginCatalogService : IPluginCatalogService
{
    public List<PluginDescriptor> Descriptors { get; } = [];

    public Func<CancellationToken, Task<IReadOnlyList<PluginDescriptor>>>? Behavior { get; set; }

    public int ScanCallCount { get; private set; }

    public string PluginsDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "linkforge-test-plugins");

    public Task<IReadOnlyList<PluginDescriptor>> ScanAsync(CancellationToken cancellationToken)
    {
        ScanCallCount++;

        return Behavior?.Invoke(cancellationToken)
            ?? Task.FromResult<IReadOnlyList<PluginDescriptor>>(Descriptors.ToArray());
    }

    /// <summary>按测试需要构造一个插件描述。</summary>
    public static PluginDescriptor Descriptor(
        string id = "acme.vendor.sdk",
        string displayName = "厂商 SDK 插件",
        PluginValidationStatus status = PluginValidationStatus.Compatible,
        string detail = "入口 LinkForge.TestPluginHost.exe 校验通过。",
        PluginSignatureStatus signature = PluginSignatureStatus.Unsigned)
    {
        var manifest = new PluginManifest
        {
            Id = id,
            DisplayName = displayName,
            Version = new Version(1, 2, 0),
            ApiVersion = new Version(1, 0),
            Publisher = "Acme",
            Description = "测试插件。",
            Capabilities = ["video.vendor.acme"],
            Architectures = ["any"],
            EntryPoint = "LinkForge.TestPluginHost.exe",
            EntryPointSha256 = new string('a', 64),
            SignatureStatus = signature,
            Steps =
            [
                new PluginStepDeclaration(
                    "probe",
                    "能力探针",
                    TimeSpan.FromSeconds(5),
                    [],
                    [])
            ]
        };

        return new PluginDescriptor(
            Path.Combine("plugins", id),
            Path.Combine("plugins", id, "manifest.json"),
            status,
            detail,
            manifest,
            status == PluginValidationStatus.HashMismatch ? new string('b', 64) : new string('a', 64));
    }
}

/// <summary>不启动子进程的插件执行器，行为可编排。</summary>
internal sealed class FakePluginRunExecutor : IPluginRunExecutor
{
    public bool ThrowTrustNotConfirmed { get; set; }

    public Exception? Failure { get; set; }

    public Func<CancellationToken, Task<DiagnosticRunResult>>? Behavior { get; set; }

    /// <summary>在进入受控行为前上报一条进度，供页面测试验证运行中状态。</summary>
    public Action<IProgress<DiagnosticProgress>?>? ProgressHook { get; set; }

    public int RunCallCount { get; private set; }

    public bool? LastSourceConfirmed { get; private set; }

    public string? LastTarget { get; private set; }

    public DiagnosticRunResult? LastResult { get; private set; }

    /// <summary>与真实执行器一致：是否需要来源确认由描述决定。</summary>
    public bool RequiresTrust(PluginDescriptor descriptor) => descriptor.RequiresExplicitTrust;

    public async Task<DiagnosticRunResult> RunAsync(
        PluginDescriptor descriptor,
        string? target,
        bool sourceConfirmed,
        IProgress<DiagnosticProgress>? progress,
        CancellationToken cancellationToken)
    {
        RunCallCount++;
        LastSourceConfirmed = sourceConfirmed;
        LastTarget = target;

        if (ThrowTrustNotConfirmed && !sourceConfirmed)
        {
            throw new PluginTrustNotConfirmedException(
                $"插件“{descriptor.Manifest!.DisplayName}”的签名状态为" +
                $"{descriptor.Manifest.SignatureStatus}，运行前必须确认来源。");
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        ProgressHook?.Invoke(progress);

        LastResult = Behavior is null
            ? PassedResult(descriptor)
            : await Behavior(cancellationToken).ConfigureAwait(false);

        progress?.Report(new DiagnosticProgress(
            "run-1",
            "probe",
            DiagnosticStepPhase.Running,
            "已启动隔离进程，正在执行插件步骤“能力探针”…",
            DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(120)));

        return LastResult;
    }

    /// <summary>构造一个来自真实步骤语义的通过结果，供页面与视图模型测试共用。</summary>
    public static DiagnosticRunResult PassedResult(PluginDescriptor? descriptor = null) =>
        new()
        {
            RunId = "run-1",
            AdapterId = descriptor?.Manifest?.Id ?? "acme.vendor.sdk",
            DisplayName = descriptor?.Manifest?.DisplayName ?? "厂商 SDK 插件",
            TargetLabel = descriptor?.Manifest?.DisplayName ?? "厂商 SDK 插件",
            StartedAt = DateTimeOffset.Now,
            EndedAt = DateTimeOffset.Now.AddSeconds(1),
            Steps =
            [
                DiagnosticStepResults.Passed(
                    "probe",
                    "能力探针",
                    "插件步骤通过。",
                    DateTimeOffset.Now,
                    TimeSpan.FromMilliseconds(400),
                    facts: [new DiagnosticFact("入口哈希", new string('a', 64))])
            ]
        };
}

/// <summary>记录复制/导出内容，不触碰剪贴板与文件对话框。</summary>
internal sealed class FakePluginResultExporter : IPluginResultExporter
{
    public string? CopiedText { get; private set; }

    public string? ExportedFileName { get; private set; }

    public string? ExportedContent { get; private set; }

    public Func<string, string, CancellationToken, Task<string?>>? ExportBehavior { get; set; }

    public void Copy(string text) => CopiedText = text;

    public Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        ExportedFileName = suggestedFileName;
        ExportedContent = content;

        return ExportBehavior?.Invoke(suggestedFileName, content, cancellationToken)
            ?? Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), suggestedFileName));
    }
}
