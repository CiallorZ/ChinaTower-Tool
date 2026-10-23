using System.Globalization;
using System.IO;
using LinkForge.Application.Projects;
using Microsoft.Win32;

namespace LinkForge.App.Services;

/// <summary>用户选中的项目包文件。</summary>
/// <param name="FileName">完整路径。</param>
/// <param name="Content">文件字节。</param>
public readonly record struct ProjectPackageImportFile(string FileName, byte[] Content);

/// <summary>
/// 项目包的文件选择与读写边界（LF-015）。
/// </summary>
/// <remarks>
/// 与 CSV 文件服务相同，视图模型只依赖接口；测试可以覆盖成功、取消、读取失败，
/// 而不需要真的弹出系统对话框。格式与秘密边界由
/// <see cref="ProjectPackageCodec"/> 负责，本接口只搬运字节。
/// </remarks>
public interface IProjectPackageFileService
{
    /// <summary>选择并读取项目包。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件；用户取消时返回 null。</returns>
    Task<ProjectPackageImportFile?> PickAndReadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>选择保存位置并写入项目包。</summary>
    /// <param name="suggestedFileName">建议文件名。</param>
    /// <param name="content">文件字节。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否写出；false 表示用户取消。</returns>
    Task<bool> PickAndWriteAsync(
        string suggestedFileName,
        byte[] content,
        CancellationToken cancellationToken = default);
}

/// <summary>使用 Windows 文件对话框的项目包读写实现。</summary>
public sealed class ProjectPackageFileService : IProjectPackageFileService
{
    /// <inheritdoc />
    public async Task<ProjectPackageImportFile?> PickAndReadAsync(
        CancellationToken cancellationToken = default)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 LinkForge 项目包",
            Filter = "LinkForge 项目包 (*.lfpkg;*.json)|*.lfpkg;*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(dialog.FileName, cancellationToken)
            .ConfigureAwait(true);

        return new ProjectPackageImportFile(dialog.FileName, bytes);
    }

    /// <inheritdoc />
    public async Task<bool> PickAndWriteAsync(
        string suggestedFileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var dialog = new SaveFileDialog
        {
            Title = "导出 LinkForge 项目包",
            FileName = suggestedFileName,
            DefaultExt = ".lfpkg",
            Filter = "LinkForge 项目包 (*.lfpkg)|*.lfpkg|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        await File.WriteAllBytesAsync(dialog.FileName, content, cancellationToken)
            .ConfigureAwait(true);

        return true;
    }
}

/// <summary>项目包建议文件名。</summary>
public static class ProjectPackageFileNames
{
    /// <summary>按项目名与本地时间生成可读文件名。</summary>
    /// <param name="projectName">项目名称。</param>
    /// <param name="timestamp">时间。</param>
    /// <returns>文件名。</returns>
    public static string Build(string? projectName, DateTimeOffset timestamp)
    {
        var safeName = Sanitize(projectName);
        var stamp = timestamp
            .ToLocalTime()
            .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        return string.IsNullOrWhiteSpace(safeName)
            ? $"linkforge-project-{stamp}.lfpkg"
            : $"linkforge-project-{safeName}-{stamp}.lfpkg";
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var ch in value.Trim())
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}
