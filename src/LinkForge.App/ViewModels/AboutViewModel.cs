using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.App.Services;

namespace LinkForge.App.ViewModels;

/// <summary>Read-only V1.0 release and environment information.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly IAboutInfoService _infoService;
    private readonly IAboutActionService _actionService;
    private CancellationTokenSource? _loadCts;

    public AboutViewModel(
        IAboutInfoService infoService,
        IAboutActionService actionService)
    {
        ArgumentNullException.ThrowIfNull(infoService);
        ArgumentNullException.ThrowIfNull(actionService);

        _infoService = infoService;
        _actionService = actionService;
        Facts = [];
        SignatureNotes = [];
    }

    /// <summary>Environment facts shown after a successful load.</summary>
    public ObservableCollection<AboutFact> Facts { get; }

    /// <summary>Honest signature boundary notes.</summary>
    public ObservableCollection<string> SignatureNotes { get; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool HasLoaded { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string ProductName { get; private set; } = "LinkForge";

    [ObservableProperty]
    public partial string Version { get; private set; } = "未知";

    [ObservableProperty]
    public partial string Commit { get; private set; } = "未知";

    [ObservableProperty]
    public partial string SignatureSummary { get; private set; } = "尚未读取";

    [ObservableProperty]
    public partial bool HasSignatureWarning { get; private set; }

    [ObservableProperty]
    public partial string DataDirectory { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LogsDirectory { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? RepositoryUrl { get; private set; }

    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasActionError { get; private set; }

    /// <summary>Reads the current executable and installation facts asynchronously.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _loadCts.Token;

        IsLoading = true;
        HasLoaded = false;
        ErrorMessage = null;
        ActionFeedback = string.Empty;
        HasActionError = false;

        try
        {
            var snapshot = await _infoService.ReadAsync(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();

            ProductName = snapshot.Product;
            Version = snapshot.Version;
            Commit = snapshot.Commit;
            SignatureSummary = snapshot.SignatureSummary;
            HasSignatureWarning = snapshot.SignatureState != LfCodeSignatureState.Trusted;
            DataDirectory = snapshot.DataDirectory;
            LogsDirectory = snapshot.LogsDirectory;
            RepositoryUrl = snapshot.RepositoryUrl;

            Facts.Clear();
            Facts.Add(new("产品", snapshot.Product));
            Facts.Add(new("程序版本", snapshot.Version));
            Facts.Add(new("Git 提交", snapshot.Commit));
            Facts.Add(new("运行时", snapshot.Runtime));
            Facts.Add(new("操作系统", snapshot.OperatingSystem));
            Facts.Add(new("进程架构", snapshot.ProcessArchitecture));
            Facts.Add(new("运行时标识", snapshot.RuntimeIdentifier));
            Facts.Add(new("安装形态", snapshot.InstallationKind));
            Facts.Add(new("程序路径", snapshot.ExecutablePath));
            Facts.Add(new("数据目录", snapshot.DataDirectory));
            Facts.Add(new("日志目录", snapshot.LogsDirectory));

            SignatureNotes.Clear();
            SignatureNotes.Add("应用内验证只检查 Windows Authenticode 信任结果，不代表软件供应链已完成审计。");
            SignatureNotes.Add("未签名正式包会触发 SmartScreen 提示；发布说明不得宣称“已验证发布者”。");
            SignatureNotes.Add("应用不在线检查更新，也不会显示未经服务器确认的“已是最新版本”。");

            HasLoaded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ActionFeedback = "已取消读取版本信息。";
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException)
        {
            ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Copies the visible facts; no secret values are read into this page.</summary>
    [RelayCommand(CanExecute = nameof(HasLoaded))]
    private void CopySummary()
    {
        var lines = new List<string>
        {
            ProductName,
            $"版本：{Version}",
            $"签名：{SignatureSummary}"
        };
        lines.AddRange(Facts.Select(static fact => $"{fact.Label}: {fact.Value}"));
        lines.Add("说明：此页只读取程序集、系统与本机路径事实，不包含凭据。");
        RunAction(() => _actionService.CopyText(string.Join(Environment.NewLine, lines)), "已复制版本与诊断信息。");
    }

    /// <summary>Opens the real application data directory.</summary>
    [RelayCommand(CanExecute = nameof(HasLoaded))]
    private void OpenDataDirectory() =>
        RunAction(() => _actionService.OpenDirectory(DataDirectory), "已打开数据目录。");

    /// <summary>Opens the real structured-log directory.</summary>
    [RelayCommand(CanExecute = nameof(HasLoaded))]
    private void OpenLogsDirectory() =>
        RunAction(() => _actionService.OpenDirectory(LogsDirectory), "已打开日志目录。");

    /// <summary>Opens the project repository in the default browser.</summary>
    [RelayCommand(CanExecute = nameof(HasLoaded))]
    private void OpenRepository()
    {
        if (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var uri))
        {
            SetActionFeedback("仓库地址无效，未执行打开操作。", isError: true);
            return;
        }

        RunAction(() => _actionService.OpenUrl(uri), "已在浏览器中打开项目主页。");
    }

    partial void OnHasLoadedChanged(bool value)
    {
        CopySummaryCommand.NotifyCanExecuteChanged();
        OpenDataDirectoryCommand.NotifyCanExecuteChanged();
        OpenLogsDirectoryCommand.NotifyCanExecuteChanged();
        OpenRepositoryCommand.NotifyCanExecuteChanged();
    }

    private void RunAction(Action action, string successMessage)
    {
        try
        {
            action();
            SetActionFeedback(successMessage, isError: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.ExternalException
            or ArgumentException)
        {
            SetActionFeedback($"操作失败：{ex.GetType().Name} - {ex.Message}", isError: true);
        }
    }

    private void SetActionFeedback(string message, bool isError)
    {
        HasActionError = isError;
        ActionFeedback = message;
    }
}
