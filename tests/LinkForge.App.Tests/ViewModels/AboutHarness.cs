using LinkForge.App.Services;
using LinkForge.App.ViewModels;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>Fixture that replaces the About page's system boundaries.</summary>
internal sealed class AboutHarness : IDisposable
{
    public AboutHarness()
    {
        ViewModel = new AboutViewModel(Info, Actions);
    }

    public FakeAboutInfoService Info { get; } = new();

    public FakeAboutActionService Actions { get; } = new();

    public AboutViewModel ViewModel { get; }

    public void Dispose()
    {
    }

    public static AboutSnapshot Snapshot(
        LfCodeSignatureState signatureState = LfCodeSignatureState.Unsigned,
        string repositoryUrl = "https://github.com/CiallorZ/ChinaTower-Tool") =>
        new(
            Product: "LinkForge 视联网调试工具箱",
            Version: "1.0.0",
            InformationalVersion: "1.0.0+0123456789ab",
            Commit: "0123456789ab",
            Runtime: ".NET 10.0.0",
            OperatingSystem: "Windows 10 (build 19045)",
            ProcessArchitecture: "X64",
            RuntimeIdentifier: "win-x64",
            ExecutablePath: @"C:\Program Files\LinkForge\LinkForge.App.exe",
            InstallationKind: "安装版（Inno Setup）",
            SignatureState: signatureState,
            SignatureSummary: signatureState switch
            {
                LfCodeSignatureState.Trusted => "已签名，Windows 信任链验证通过",
                LfCodeSignatureState.Unsigned => "未签名（未提供代码签名证书）",
                LfCodeSignatureState.Invalid => "签名无效或证书链不受信任",
                _ => "签名状态未知（本地校验未返回确定结果）"
            },
            DataDirectory: @"C:\Users\tester\AppData\Local\LinkForge",
            LogsDirectory: @"C:\Users\tester\AppData\Local\LinkForge\logs",
            RepositoryUrl: repositoryUrl);
}

internal sealed class FakeAboutInfoService : IAboutInfoService
{
    public AboutSnapshot Result { get; set; } = AboutHarness.Snapshot();

    public Func<CancellationToken, Task<AboutSnapshot>>? Behavior { get; set; }

    public int CallCount { get; private set; }

    public Task<AboutSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Behavior?.Invoke(cancellationToken) ?? Task.FromResult(Result);
    }
}

internal sealed class FakeAboutActionService : IAboutActionService
{
    public string? CopiedText { get; private set; }

    public string? OpenedDirectory { get; private set; }

    public Uri? OpenedUrl { get; private set; }

    public Exception? Failure { get; set; }

    public void CopyText(string text)
    {
        ThrowIfConfigured();
        CopiedText = text;
    }

    public void OpenDirectory(string path)
    {
        ThrowIfConfigured();
        OpenedDirectory = path;
    }

    public void OpenUrl(Uri uri)
    {
        ThrowIfConfigured();
        OpenedUrl = uri;
    }

    private void ThrowIfConfigured()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}
