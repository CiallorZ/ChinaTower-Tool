using Microsoft.Win32;

namespace LinkForge.App.Services;

/// <summary>
/// 报告保存对话框契约。
/// </summary>
/// <remarks>
/// 单独抽象是为了让视图模型可以在没有 UI 的测试宿主里验证
/// "用户取消"、"写入失败"与"成功"三条反馈路径，
/// 而不是只能在手工点击时才能看到。
/// </remarks>
public interface IReportFileExporter
{
    /// <summary>
    /// 选择保存位置。
    /// </summary>
    /// <param name="suggestedFileName">建议文件名。</param>
    /// <returns>用户选定的完整路径；取消时返回 null。</returns>
    string? ChooseDestination(string suggestedFileName);

    /// <summary>
    /// 在系统文件管理器中定位文件。
    /// </summary>
    /// <param name="path">文件路径。</param>
    void RevealInExplorer(string path);
}

/// <summary>
/// Windows 保存对话框与资源管理器实现。
/// </summary>
public sealed class ReportFileExporter : IReportFileExporter
{
    /// <inheritdoc />
    public string? ChooseDestination(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存诊断报告",
            FileName = suggestedFileName,
            DefaultExt = ".html",
            AddExtension = true,
            Filter = "HTML 报告 (*.html)|*.html|所有文件 (*.*)|*.*",
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public void RevealInExplorer(string path)
    {
        // 只选中文件，不执行它：报告是 HTML，双击会用浏览器打开并可能触发外部请求。
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }
}
