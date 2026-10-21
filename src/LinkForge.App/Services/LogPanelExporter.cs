using System.IO;
using Microsoft.Win32;

namespace LinkForge.App.Services;

/// <summary>
/// 日志面板复制与导出边界。
/// </summary>
/// <remarks>
/// 与结果页导出器相同，把系统对话框和剪贴板隔离在接口后，
/// 视图模型测试可以验证“实际复制/写出了什么”，而不弹出真实窗口。
/// </remarks>
public interface ILogPanelExporter
{
    /// <summary>复制文本到剪贴板。</summary>
    void Copy(string text);

    /// <summary>导出日志文本；用户取消时返回 null。</summary>
    Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        CancellationToken cancellationToken = default);
}

/// <summary>Windows 剪贴板与日志文件保存实现。</summary>
public sealed class LogPanelExporter : ILogPanelExporter
{
    /// <inheritdoc />
    public void Copy(string text) => System.Windows.Clipboard.SetText(text);

    /// <inheritdoc />
    public async Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 LinkForge 日志",
            FileName = suggestedFileName,
            DefaultExt = ".log",
            AddExtension = true,
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        await File
            .WriteAllTextAsync(dialog.FileName, content, cancellationToken)
            .ConfigureAwait(false);

        return dialog.FileName;
    }
}
