using Microsoft.Win32;

namespace LinkForge.App.Services;

/// <summary>Windows 剪贴板与文件对话框实现。</summary>
public sealed class MqttResultExporter : IMqttResultExporter
{
    /// <inheritdoc />
    public void Copy(string text) => System.Windows.Clipboard.SetText(text);

    /// <inheritdoc />
    public async Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        string filter,
        CancellationToken cancellationToken = default)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 MQTT 调试结果",
            FileName = suggestedFileName,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = filter
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
