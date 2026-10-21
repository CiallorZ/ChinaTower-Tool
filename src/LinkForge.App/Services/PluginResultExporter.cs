using Microsoft.Win32;

namespace LinkForge.App.Services;

/// <summary>插件结果导出边界（剪贴板与文件对话框不进入视图模型）。</summary>
public interface IPluginResultExporter
{
    /// <summary>复制文本到剪贴板。</summary>
    void Copy(string text);

    /// <summary>导出文件；用户取消时返回 null。</summary>
    Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        CancellationToken cancellationToken = default);
}

/// <summary>Windows 剪贴板与保存对话框实现。</summary>
public sealed class PluginResultExporter : IPluginResultExporter
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
            Title = "导出插件运行结果",
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
