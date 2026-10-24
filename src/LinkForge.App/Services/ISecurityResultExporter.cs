using Microsoft.Win32;

namespace LinkForge.App.Services;

/// <summary>安全基线结果的复制/导出契约。</summary>
public interface ISecurityResultExporter
{
    /// <summary>复制脱敏摘要。</summary>
    void Copy(string text);

    /// <summary>导出 JSON；用户取消时返回 null。</summary>
    Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        CancellationToken cancellationToken = default);
}

/// <summary>Windows 剪贴板与保存对话框实现。</summary>
public sealed class SecurityResultExporter : ISecurityResultExporter
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
            Title = "导出安全基线结果",
            FileName = suggestedFileName,
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "JSON 结果 (*.json)|*.json|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        await System.IO.File
            .WriteAllTextAsync(dialog.FileName, content, cancellationToken)
            .ConfigureAwait(false);

        return dialog.FileName;
    }
}
