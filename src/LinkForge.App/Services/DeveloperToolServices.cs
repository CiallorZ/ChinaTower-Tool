using LinkForge.DeveloperTools;

namespace LinkForge.App.Services;

/// <summary>开发者工具执行边界，便于视图模型测试注入取消与异常行为。</summary>
public interface IDeveloperTransformExecutor
{
    Task<DeveloperToolResult> ExecuteAsync(
        DeveloperToolKind kind,
        string input,
        CancellationToken cancellationToken);
}

/// <summary>把纯转换库调度到线程池，避免大输入占用 UI 线程。</summary>
public sealed class DeveloperTransformExecutor : IDeveloperTransformExecutor
{
    /// <inheritdoc />
    public Task<DeveloperToolResult> ExecuteAsync(
        DeveloperToolKind kind,
        string input,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => DeveloperTransforms.Execute(kind, input, cancellationToken),
            cancellationToken);
}

/// <summary>开发者工具结果剪贴板边界；页面不直接调用 WPF 静态类。</summary>
public interface IDeveloperResultClipboard
{
    void Copy(string text);
}

/// <summary>Windows 剪贴板实现。</summary>
public sealed class DeveloperResultClipboard : IDeveloperResultClipboard
{
    /// <inheritdoc />
    public void Copy(string text) => System.Windows.Clipboard.SetText(text);
}
