using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LinkForge.Application.Environment;
using LinkForge.Domain.Diagnostics;
using LinkForge.Domain.Environment;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.Application.Diagnostics;

/// <summary>
/// 运行环境自检适配器（阶段 04 的首个<b>真实</b>诊断能力）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么工作台需要一个真实的诊断能力，而不是一个“示例任务”：
/// 工作台要显示任务进度、步骤结论、证据与取消入口，
/// 若这些状态来自写死的样例，那整页都是在演示而不是在工作 ——
/// 这正是 UI 规范 §4.1 与 DEV_STANDARDS §6.6 明令禁止的“用假数据伪装真实功能”。
/// </para>
/// <para>
/// 本适配器做的都是真实检查，每一项都能独立复现：
/// <list type="number">
///   <item><b>配置目录可写</b>：真的写一个临时文件再删除。</item>
///   <item><b>可用磁盘空间</b>：真的读 <see cref="DriveInfo"/>，低于阈值时给警告而非失败。</item>
///   <item><b>抓包依赖</b>：真的探测 Npcap；缺失时该步骤由编排器标记为跳过。</item>
///   <item><b>导出环境证据</b>：真的把环境事实写成文件并计算 SHA-256。</item>
/// </list>
/// </para>
/// <para>
/// 所有耗时动作都是 <c>async</c> 且响应取消；不访问网络、不修改系统配置，
/// 因此在任何机器上运行都是安全的。
/// </para>
/// </remarks>
public sealed class EnvironmentSelfCheckAdapter : IDiagnosticAdapter
{
    /// <summary>适配器标识。</summary>
    public const string AdapterId = "environment.selfcheck";

    /// <summary>磁盘可用空间低于该值时给出警告。</summary>
    private const long LowDiskSpaceThresholdBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _workingDirectory;
    private readonly ITextRedactor _redactor;

    /// <summary>
    /// 创建自检适配器。
    /// </summary>
    /// <param name="workingDirectory">证据与临时文件的工作目录（通常是 <c>%LOCALAPPDATA%\LinkForge</c>）。</param>
    /// <param name="redactor">脱敏实现，用于清理写入证据文件的文本。</param>
    public EnvironmentSelfCheckAdapter(string workingDirectory, ITextRedactor redactor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(redactor);

        _workingDirectory = workingDirectory;
        _redactor = redactor;
    }

    /// <inheritdoc />
    public string Id => AdapterId;

    /// <inheritdoc />
    public string DisplayName => "运行环境自检";

    /// <inheritdoc />
    public Version ApiVersion => new(1, 0);

    /// <inheritdoc />
    public IReadOnlyCollection<string> Capabilities => ["environment.selfcheck", "filesystem.write", "evidence.export"];

    /// <inheritdoc />
    /// <remarks>
    /// 计划是纯内存计算（不读磁盘、不探测），因此可以同步返回，
    /// 工作台能在用户点击“运行自检”之前就把步骤清单显示出来。
    /// </remarks>
    public IReadOnlyList<DiagnosticStepPlan> Plan(DiagnosticRequest request) =>
    [
        new(
            "workspace",
            "工作目录可写",
            Timeout: TimeSpan.FromSeconds(10)),

        new(
            "disk",
            "可用磁盘空间",
            DependsOnSteps: ["workspace"],
            Timeout: TimeSpan.FromSeconds(10)),

        new(
            "capture",
            "抓包组件可用性",
            RequiredDependencies: [DependencyCatalog.Ids.Npcap],
            DependsOnSteps: ["workspace"],
            Timeout: TimeSpan.FromSeconds(10)),

        new(
            "evidence",
            "导出环境证据",
            DependsOnSteps: ["workspace", "disk"],
            Timeout: TimeSpan.FromSeconds(20))
    ];

    /// <inheritdoc />
    public async Task<DiagnosticStepResult> ExecuteStepAsync(
        DiagnosticStepContext context,
        IProgress<DiagnosticProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.StepId switch
        {
            "workspace" => await CheckWorkspaceAsync(context, progress, cancellationToken).ConfigureAwait(false),
            "disk" => CheckDisk(context),
            "capture" => CheckCaptureDependency(context),
            "evidence" => await ExportEvidenceAsync(context, cancellationToken).ConfigureAwait(false),
            _ => DiagnosticStepResults.Unknown(
                context.StepId,
                context.Name,
                $"适配器不认识步骤 {context.StepId}，未执行任何检查。",
                context.StartedAt,
                TimeSpan.Zero,
                "这是实现缺陷，请反馈。")
        };
    }

    /// <summary>真实地写一个临时文件再删除，验证工作目录可写。</summary>
    private async Task<DiagnosticStepResult> CheckWorkspaceAsync(
        DiagnosticStepContext context,
        IProgress<DiagnosticProgress> progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_workingDirectory, "selfcheck");

        try
        {
            progress.Report(new DiagnosticProgress(
                context.RunId, context.StepId, DiagnosticStepPhase.Running,
                $"正在写入探测文件：{directory}", DateTimeOffset.Now, TimeSpan.Zero));

            Directory.CreateDirectory(directory);

            var probeFile = Path.Combine(directory, $".write-probe-{context.RunId}.tmp");

            // 写入内容带 RunId，便于事后判断残留文件属于哪次运行。
            await File.WriteAllTextAsync(
                probeFile,
                $"linkforge write probe {context.RunId} {DateTimeOffset.Now:O}",
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);

            File.Delete(probeFile);

            return DiagnosticStepResults.Passed(
                context.StepId,
                context.Name,
                $"工作目录可写：{directory}",
                context.StartedAt,
                TimeSpan.Zero);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 目录不可写是真实失败，且用户有明确的下一步（换目录或修权限）。
            return DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                $"工作目录不可写：{ex.GetType().Name} — {ex.Message}",
                context.StartedAt,
                TimeSpan.Zero,
                "请在设置中把工作目录改到当前用户有写权限的位置（例如 %LOCALAPPDATA% 下的目录）。");
        }
    }

    /// <summary>读取工作目录所在卷的可用空间。</summary>
    private DiagnosticStepResult CheckDisk(DiagnosticStepContext context)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_workingDirectory));

            if (string.IsNullOrEmpty(root))
            {
                return DiagnosticStepResults.Unknown(
                    context.StepId,
                    context.Name,
                    "无法确定工作目录所在卷，未能读取磁盘信息。",
                    context.StartedAt,
                    TimeSpan.Zero,
                    "请检查工作目录配置是否为有效路径。");
            }

            var drive = new DriveInfo(root);
            var free = drive.AvailableFreeSpace;
            var freeText = FormatBytes(free);

            // 低于阈值只给警告：空间紧不等于功能坏，把它报成失败会掩盖真正的失败。
            return free < LowDiskSpaceThresholdBytes
                ? DiagnosticStepResults.Warning(
                    context.StepId,
                    context.Name,
                    $"卷 {drive.Name} 可用 {freeText}，低于建议的 {FormatBytes(LowDiskSpaceThresholdBytes)}。",
                    context.StartedAt,
                    TimeSpan.Zero,
                    "抓包与视频证据文件占用较大，建议清理磁盘或把工作目录改到空间更大的卷。")
                : DiagnosticStepResults.Passed(
                    context.StepId,
                    context.Name,
                    $"卷 {drive.Name} 可用 {freeText}（总计 {FormatBytes(drive.TotalSize)}）。",
                    context.StartedAt,
                    TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DiagnosticStepResults.Unknown(
                context.StepId,
                context.Name,
                $"读取磁盘信息失败：{ex.GetType().Name} — {ex.Message}",
                context.StartedAt,
                TimeSpan.Zero,
                "请确认工作目录路径有效且当前用户有读取权限。");
        }
    }

    /// <summary>再次探测抓包组件，并把结果写成可读结论。</summary>
    /// <remarks>
    /// 编排器已经按依赖跳过做过一次判定，这里再探测一次是有意的：
    /// 编排器的判定用的是任务开始时的快照，而用户可能在任务运行期间装好了 Npcap。
    /// 重新探测能把“刚才装的到底生效没有”如实反映出来。
    /// </remarks>
    private static DiagnosticStepResult CheckCaptureDependency(DiagnosticStepContext context)
    {
        var probe = DependencyCatalog.ProbeNpcap();

        return probe.IsAvailable
            ? DiagnosticStepResults.Passed(
                context.StepId,
                context.Name,
                $"{probe.Name} 可用：{probe.Detail}",
                context.StartedAt,
                TimeSpan.Zero)
            : DiagnosticStepResults.Unknown(
                context.StepId,
                context.Name,
                $"{probe.Name} 不可用：{probe.Detail}",
                context.StartedAt,
                TimeSpan.Zero,
                "如需抓包取证，请安装 Npcap 后重新运行自检。");
    }

    /// <summary>把环境事实写成证据文件，并返回其哈希与大小。</summary>
    private async Task<DiagnosticStepResult> ExportEvidenceAsync(
        DiagnosticStepContext context,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_workingDirectory, "evidence", context.RunId);
        var fileName = "environment.txt";

        try
        {
            Directory.CreateDirectory(directory);

            var content = BuildEvidenceText(context);
            var path = Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            var bytes = Encoding.UTF8.GetByteCount(content);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

            var artifact = new EvidenceArtifact
            {
                FileName = fileName,
                RelativePath = Path.Combine(context.RunId, fileName).Replace('\\', '/'),
                Sha256 = hash,
                SizeBytes = bytes,
                ContentType = "text/plain; charset=utf-8",
                IsRedacted = true,
                Description = "运行环境事实（已脱敏）"
            };

            return DiagnosticStepResults.Passed(
                context.StepId,
                context.Name,
                $"已导出证据 {fileName}（{artifact.SizeText}，SHA-256 {artifact.ShortHash}…）",
                context.StartedAt,
                TimeSpan.Zero,
                [artifact]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return DiagnosticStepResults.Failed(
                context.StepId,
                context.Name,
                $"写入证据文件失败：{ex.GetType().Name} — {ex.Message}",
                context.StartedAt,
                TimeSpan.Zero,
                "请检查工作目录剩余空间与写权限后重试。");
        }
    }

    /// <summary>
    /// 组装证据文本。
    /// </summary>
    /// <remarks>
    /// 内容只包含本机可复现的事实（版本、架构、路径），
    /// 且经过统一脱敏 —— 即使将来有人往这里加了含凭据的字段，也不会明文落盘。
    /// </remarks>
    private string BuildEvidenceText(DiagnosticStepContext context)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# LinkForge 运行环境证据");
        builder.AppendLine();
        builder.AppendLine($"运行 ID      : {context.RunId}");
        builder.AppendLine($"采集时间     : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"目标         : {context.TargetLabel}");
        builder.AppendLine($"操作系统     : {RuntimeInformation.OSDescription}");
        builder.AppendLine($"进程架构     : {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"运行时       : {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"工作目录     : {_workingDirectory}");
        builder.AppendLine($"Npcap        : {DependencyCatalog.ProbeNpcap().Detail}");
        builder.AppendLine();
        builder.AppendLine("说明：本文件由“运行环境自检”任务自动生成，仅包含本机可复现的环境事实，");
        builder.AppendLine("      不包含任何设备凭据、密码或令牌。");

        return _redactor.Redact(builder.ToString()) ?? string.Empty;
    }

    /// <summary>把字节数格式化为可读文本。</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB"
    };
}
