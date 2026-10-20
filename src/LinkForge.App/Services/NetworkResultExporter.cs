using Microsoft.Win32;

namespace LinkForge.App.Services;

/// <summary>
/// 网络诊断结果导出契约。
/// </summary>
/// <remarks>
/// 用接口隔开文件对话框与剪贴板，视图模型才能在不触碰 UI 的测试里验证
/// “导出了什么内容、失败时是否给出可读反馈”。
/// </remarks>
public interface INetworkResultExporter
{
    /// <summary>把脱敏摘要复制到剪贴板。</summary>
    void Copy(string text);

    /// <summary>导出结果文件；用户取消时返回 null。</summary>
    Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows 文件对话框与剪贴板实现。
/// </summary>
public sealed class NetworkResultExporter : INetworkResultExporter
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
            Title = "导出网络诊断结果",
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
