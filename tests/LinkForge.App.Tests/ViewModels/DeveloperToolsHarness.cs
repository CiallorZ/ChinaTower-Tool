using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using LinkForge.DeveloperTools;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>开发者工具页测试夹具；替换执行与剪贴板边界。</summary>
internal sealed class DeveloperToolsHarness
{
    public DeveloperToolsHarness()
    {
        Executor = new FakeDeveloperTransformExecutor();
        Clipboard = new FakeDeveloperResultClipboard();
        ViewModel = new DeveloperToolsViewModel(Executor, Clipboard);
    }

    public FakeDeveloperTransformExecutor Executor { get; }

    public FakeDeveloperResultClipboard Clipboard { get; }

    public DeveloperToolsViewModel ViewModel { get; }

    public DeveloperToolDescriptor Tool(DeveloperToolKind kind) =>
        DeveloperToolDescriptor.All.Single(tool => tool.Kind == kind);
}

/// <summary>可编排的执行边界，用于取消与异常路径。</summary>
internal sealed class FakeDeveloperTransformExecutor : IDeveloperTransformExecutor
{
    public Func<DeveloperToolKind, string, CancellationToken, Task<DeveloperToolResult>>? Behavior { get; set; }

    public int CallCount { get; private set; }

    public Task<DeveloperToolResult> ExecuteAsync(
        DeveloperToolKind kind,
        string input,
        CancellationToken cancellationToken)
    {
        CallCount++;

        return Behavior?.Invoke(kind, input, cancellationToken)
            ?? Task.FromResult(DeveloperTransforms.Execute(kind, input, cancellationToken));
    }
}

/// <summary>记录复制内容，不触碰真实剪贴板。</summary>
internal sealed class FakeDeveloperResultClipboard : IDeveloperResultClipboard
{
    public string? CopiedText { get; private set; }

    public int CopyCount { get; private set; }

    public Exception? Failure { get; set; }

    public void Copy(string text)
    {
        CopyCount++;

        if (Failure is not null)
        {
            throw Failure;
        }

        CopiedText = text;
    }
}
