using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.Adapters.Onvif;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Application.Diagnostics;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.App.ViewModels;

/// <summary>ONVIF 工具下拉项。</summary>
/// <param name="Tool">工具。</param>
/// <param name="Title">显示名称。</param>
/// <param name="Description">一句话说明。</param>
public sealed record OnvifToolOption(OnvifTool Tool, string Title, string Description)
{
    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>
/// ONVIF 标准接入诊断视图模型（LF-014）。
/// </summary>
/// <remarks>
/// 页面只执行 WS-Discovery、设备信息、能力与媒体 Profile/Stream URI 查询。
/// 不接收 RTP、不解码、不证明首帧，也未接入厂商 SDK 或 GB/T 28181。
/// 用户名与口令只进入诊断参数，不进入结论、日志、复制或导出文本。
/// </remarks>
public sealed partial class OnvifDiagnosticsViewModel : ObservableObject
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(2);

    private readonly OnvifDiagnosticAdapter _adapter;
    private readonly DiagnosticTaskRunner _runner;
    private readonly IOnvifResultExporter _exporter;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _uiContext;

    private CancellationTokenSource? _runCts;
    private string _currentRunId = string.Empty;
    private DiagnosticRunResult? _lastResult;

    /// <summary>创建 ONVIF 诊断视图模型。</summary>
    public OnvifDiagnosticsViewModel(
        OnvifDiagnosticAdapter adapter,
        DiagnosticTaskRunner runner,
        IOnvifResultExporter exporter,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(exporter);

        _adapter = adapter;
        _runner = runner;
        _exporter = exporter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _uiContext = SynchronizationContext.Current;

        Tools =
        [
            new(
                OnvifTool.Discover,
                "WS-Discovery 发现",
                "向标准 ONVIF 组播地址发送一次 Probe，并等待视频发送端响应。"),
            new(
                OnvifTool.Inspect,
                "设备服务查询",
                "查询设备信息、服务能力、媒体 Profile 与 RTSP Stream URI。")
        ];

        SelectedTool = Tools[1];
        Steps = [];
        Logs = [];
        SummaryItems = [];
        State = TaskRunState.Pending;
    }

    /// <summary>可用 ONVIF 工具。</summary>
    public IReadOnlyList<OnvifToolOption> Tools { get; }

    /// <summary>当前选择的工具。</summary>
    [ObservableProperty]
    public partial OnvifToolOption SelectedTool { get; set; }

    /// <summary>ONVIF 设备服务 URL。</summary>
    [ObservableProperty]
    public partial string ServiceUrl { get; set; } = "http://127.0.0.1/onvif/device_service";

    /// <summary>WS-Security 用户名。</summary>
    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    /// <summary>WS-Security 口令；不会进入结果文本。</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>媒体 Profile Token；为空时使用设备返回的第一个 Profile。</summary>
    [ObservableProperty]
    public partial string ProfileToken { get; set; } = string.Empty;

    /// <summary>单步超时文本（秒）。</summary>
    [ObservableProperty]
    public partial string TimeoutText { get; set; } = "10";

    /// <summary>用户确认已获得目标网络/设备的测试授权。</summary>
    [ObservableProperty]
    public partial bool IsAuthorized { get; set; }

    /// <summary>设备服务 URL 字段错误。</summary>
    [ObservableProperty]
    public partial string? ServiceUrlError { get; set; }

    /// <summary>用户名字段错误。</summary>
    [ObservableProperty]
    public partial string? UsernameError { get; set; }

    /// <summary>口令字段错误。</summary>
    [ObservableProperty]
    public partial string? PasswordError { get; set; }

    /// <summary>Profile Token 字段错误。</summary>
    [ObservableProperty]
    public partial string? ProfileTokenError { get; set; }

    /// <summary>超时字段错误。</summary>
    [ObservableProperty]
    public partial string? TimeoutError { get; set; }

    /// <summary>是否正在运行。</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>任务状态。</summary>
    [ObservableProperty]
    public partial TaskRunState State { get; private set; }

    /// <summary>当前阶段。</summary>
    [ObservableProperty]
    public partial string CurrentStage { get; private set; } = "尚未运行";

    /// <summary>已运行时间。</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = "00:00.0";

    /// <summary>一句话结论。</summary>
    [ObservableProperty]
    public partial string Conclusion { get; private set; } = string.Empty;

    /// <summary>结论补充。</summary>
    [ObservableProperty]
    public partial string ConclusionNote { get; private set; } = string.Empty;

    /// <summary>是否已有结果。</summary>
    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    /// <summary>复制/导出反馈。</summary>
    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    /// <summary>是否处于发现模式。</summary>
    public bool IsDiscoverMode => SelectedTool.Tool == OnvifTool.Discover;

    /// <summary>是否处于设备服务查询模式。</summary>
    public bool IsInspectMode => SelectedTool.Tool == OnvifTool.Inspect;

    /// <summary>脱敏后的服务地址。</summary>
    public string RedactedServiceUrl => OnvifEndpoint.TryParse(ServiceUrl, out var endpoint, out _)
        ? endpoint.DisplayUrl
        : "服务 URL 尚未通过校验";

    /// <summary>是否需要设备服务 URL。</summary>
    public bool RequiresServiceUrl => IsInspectMode;

    /// <summary>是否需要授权确认。</summary>
    public bool RequiresAuthorization => IsDiscoverMode
        || OnvifEndpoint.TryParse(ServiceUrl, out var endpoint, out _)
        && !IsLoopbackOrLocalName(endpoint.Host);

    /// <summary>能否开始。</summary>
    public bool CanStart => !IsRunning;

    /// <summary>结构化步骤结果。</summary>
    public ObservableCollection<DiagnosticStepResult> Steps { get; }

    /// <summary>脱敏日志。</summary>
    public ObservableCollection<LogEntry> Logs { get; }

    /// <summary>摘要事实。</summary>
    public ObservableCollection<ResultSummaryItem> SummaryItems { get; }

    partial void OnSelectedToolChanged(OnvifToolOption value)
    {
        ServiceUrlError = null;
        UsernameError = null;
        PasswordError = null;
        ProfileTokenError = null;
        TimeoutError = null;
        OnPropertyChanged(nameof(IsDiscoverMode));
        OnPropertyChanged(nameof(IsInspectMode));
        OnPropertyChanged(nameof(RequiresServiceUrl));
        OnPropertyChanged(nameof(RequiresAuthorization));
    }

    partial void OnServiceUrlChanged(string value)
    {
        ServiceUrlError = null;
        OnPropertyChanged(nameof(RedactedServiceUrl));
        OnPropertyChanged(nameof(RequiresAuthorization));
    }

    /// <summary>执行 ONVIF 诊断。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!Validate())
        {
            return;
        }

        if (RequiresAuthorization && !IsAuthorized)
        {
            Conclusion = IsDiscoverMode
                ? "尚未确认网络探测授权，ONVIF 设备发现未启动。"
                : "尚未确认授权范围，ONVIF 设备查询未启动。";
            ConclusionNote = IsDiscoverMode
                ? "发现操作会向本地网络发送标准组播 Probe，请确认你已获得该网络环境的测试授权。"
                : "请确认你已获得该视频设备的测试授权，再勾选授权确认。";
            State = TaskRunState.Pending;
            HasResult = true;
            return;
        }

        var runId = Guid.NewGuid().ToString("N");
        _currentRunId = runId;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = linked;
        var startedAt = _timeProvider.GetUtcNow();
        var progress = new Progress<DiagnosticProgress>(OnProgress);

        try
        {
            IsRunning = true;
            State = TaskRunState.Preparing;
            HasResult = false;
            CurrentStage = "正在准备 ONVIF 诊断…";
            ElapsedText = "00:00.0";
            Conclusion = string.Empty;
            ConclusionNote = string.Empty;
            ActionFeedback = string.Empty;
            Steps.Clear();
            Logs.Clear();
            SummaryItems.Clear();
            NotifyCommands();

            AppendLog(LfLogLevel.Information, $"开始 {SelectedTool.Title}：{BuildTargetLabel()}");
            var ticker = TickElapsedAsync(startedAt, linked.Token);

            try
            {
                CurrentStage = IsDiscoverMode
                    ? "正在执行 WS-Discovery…"
                    : "正在查询 ONVIF 设备服务…";

                var request = new DiagnosticRequest(
                    runId,
                    BuildTargetLabel(),
                    BuildParameters(),
                    TimeSpan.FromSeconds(ParseTimeout()));

                var result = await _runner.RunAsync(
                    _adapter,
                    request,
                    [],
                    new DiagnosticRunOptions(TimeSpan.FromSeconds(ParseTimeout()), OverallTimeout),
                    progress,
                    linked.Token);

                ApplyResult(result);
            }
            finally
            {
                linked.Cancel();
                await ticker;
            }
        }
        catch (OperationCanceledException)
        {
            State = TaskRunState.Cancelled;
            CurrentStage = "已取消";
            Conclusion = "ONVIF 诊断已被取消，未产生完整结论。";
            ConclusionNote = "未执行的阶段不会记为失败；如需完整结果请重新运行。";
            HasResult = true;
            AppendLog(LfLogLevel.Warning, "任务已被取消。");
        }
        catch (Exception ex)
        {
            State = TaskRunState.Failed;
            CurrentStage = "执行失败";
            Conclusion = $"ONVIF 诊断未能完成：{ex.GetType().Name} — {ex.Message}";
            ConclusionNote = "请查看日志，并检查服务 URL、凭据、DNS、TCP/HTTP(S) 与网络权限。";
            HasResult = true;
            AppendLog(LfLogLevel.Critical, Conclusion);
        }
        finally
        {
            IsRunning = false;
            _runCts = null;
            NotifyCommands();
        }
    }

    /// <summary>取消当前任务。</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>复制脱敏摘要。</summary>
    [RelayCommand]
    private void CopySummary()
    {
        if (_lastResult is null)
        {
            return;
        }

        try
        {
            _exporter.Copy(BuildSummaryText(_lastResult));
            ActionFeedback = "已复制脱敏 ONVIF 摘要到剪贴板。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ActionFeedback = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>导出脱敏 JSON。</summary>
    [RelayCommand]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (_lastResult is null)
        {
            return;
        }

        try
        {
            var fileName = $"linkforge-onvif-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var path = await _exporter
                .ExportAsync(fileName, BuildExportJson(_lastResult), cancellationToken);

            ActionFeedback = path is null ? "已取消导出。" : $"已导出：{path}";
        }
        catch (OperationCanceledException)
        {
            ActionFeedback = "已取消导出。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionFeedback = $"导出失败：{ex.Message}";
        }
    }

    private bool Validate()
    {
        ServiceUrlError = null;
        UsernameError = null;
        PasswordError = null;
        ProfileTokenError = null;
        TimeoutError = null;
        var valid = true;

        if (RequiresServiceUrl
            && !OnvifEndpoint.TryParse(ServiceUrl, out _, out var endpointError))
        {
            ServiceUrlError = endpointError ?? "ONVIF 设备服务 URL 无效。";
            valid = false;
        }

        if (IsInspectMode && Username.Length > 256)
        {
            UsernameError = "用户名不能超过 256 个字符。";
            valid = false;
        }

        if (IsInspectMode && Password.Length > 1024)
        {
            PasswordError = "口令不能超过 1024 个字符。";
            valid = false;
        }

        if (IsInspectMode && ProfileToken.Trim().Length > 512)
        {
            ProfileTokenError = "Profile Token 不能超过 512 个字符。";
            valid = false;
        }

        if (!int.TryParse(TimeoutText, out var timeout) || timeout is < 1 or > 60)
        {
            TimeoutError = "超时必须是 1–60 秒的整数。";
            valid = false;
        }

        return valid;
    }

    private Dictionary<string, string?> BuildParameters() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [OnvifParameterNames.Tool] = SelectedTool.Tool.ToString(),
            [OnvifParameterNames.ServiceUrl] = IsInspectMode ? ServiceUrl.Trim() : null,
            [OnvifParameterNames.Username] = IsInspectMode ? Username : null,
            [OnvifParameterNames.Password] = IsInspectMode ? Password : null,
            [OnvifParameterNames.TimeoutSeconds] = ParseTimeout().ToString(),
            [OnvifParameterNames.ProfileToken] =
                IsInspectMode && !string.IsNullOrWhiteSpace(ProfileToken)
                    ? ProfileToken.Trim()
                    : null
        };

    private int ParseTimeout() => int.TryParse(TimeoutText, out var seconds) ? seconds : 10;

    private string BuildTargetLabel() => IsDiscoverMode
        ? "ONVIF WS-Discovery（标准组播）"
        : RedactedServiceUrl;

    private void ApplyResult(DiagnosticRunResult result)
    {
        _lastResult = result;

        foreach (var step in result.Steps)
        {
            Steps.Add(step);
            AppendLog(LogLevelFor(step.Status), step.Summary);

            if (step.HasSuggestion)
            {
                AppendLog(LfLogLevel.Debug, $"建议：{step.NextStepSuggestion}");
            }
        }

        State = result.State;
        HasResult = true;
        ElapsedText = DashboardViewModel.FormatElapsed(result.Duration ?? TimeSpan.Zero);
        CurrentStage = DescribeState(result.State);
        Conclusion = BuildConclusion(result);
        ConclusionNote = result.StepSummaryText;

        SummaryItems.Clear();
        SummaryItems.Add(new ResultSummaryItem { Label = "工具", Value = SelectedTool.Title });
        SummaryItems.Add(new ResultSummaryItem { Label = "目标", Value = BuildTargetLabel() });
        SummaryItems.Add(new ResultSummaryItem { Label = "耗时", Value = result.DurationText });
        SummaryItems.Add(new ResultSummaryItem
        {
            Label = "结论",
            Value = result.Steps.FirstOrDefault()?.Summary ?? "无步骤结论",
            Status = result.Steps.FirstOrDefault()?.Status
        });
    }

    private static string BuildConclusion(DiagnosticRunResult result) => result.State switch
    {
        TaskRunState.Completed when result.Steps.Any(static step => step.StepId == "onvif.discovery") =>
            "ONVIF WS-Discovery 完成；未发现设备时会保持未知，不代表网络中没有 ONVIF 设备。",
        TaskRunState.Completed =>
            "ONVIF 标准服务、媒体 Profile 与 Stream URI 查询完成；未接收 RTP、未解码，也未证明首帧。",
        TaskRunState.PartiallySucceeded => "ONVIF 诊断部分完成，请查看未通过或跳过的阶段。",
        TaskRunState.Failed => result.Steps.FirstOrDefault()?.Summary ?? "ONVIF 诊断失败。",
        TaskRunState.Cancelled => "ONVIF 诊断已被取消。",
        TaskRunState.Inconclusive when result.Steps.Any(static step => step.StepId == "onvif.discovery") =>
            "WS-Discovery 未在等待窗口内发现设备，结论保持未知；这不代表网络中没有 ONVIF 设备。",
        TaskRunState.Inconclusive => "ONVIF 诊断未得到确定性结论。",
        _ => "ONVIF 诊断尚未完成。"
    };

    private static string BuildSummaryText(DiagnosticRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("LinkForge ONVIF 标准接入诊断结果");
        builder.AppendLine($"目标：{SecretRedactor.Redact(result.TargetLabel)}");
        builder.AppendLine($"状态：{DescribeState(result.State)}");
        builder.AppendLine($"开始：{result.StartedAtText}");
        builder.AppendLine($"耗时：{result.DurationText}");
        builder.AppendLine($"步骤：{result.StepSummaryText}");
        builder.AppendLine();

        foreach (var step in result.Steps)
        {
            builder.AppendLine($"[{step.Status}] {step.Name}：{SecretRedactor.Redact(step.Summary)}");

            foreach (var fact in step.FactItems)
            {
                builder.AppendLine(
                    $"  {SecretRedactor.Redact(fact.Label)}: {SecretRedactor.Redact(fact.Value)}");
            }

            if (step.HasSuggestion)
            {
                builder.AppendLine($"  建议：{SecretRedactor.Redact(step.NextStepSuggestion)}");
            }
        }

        builder.AppendLine();
        builder.Append("说明：未接收 RTP、未解码、未验证首帧；用户名与口令不在此文本中。");
        return builder.ToString();
    }

    private static string BuildExportJson(DiagnosticRunResult result)
    {
        var document = new
        {
            product = "LinkForge",
            kind = "onvif-diagnostic",
            generatedAt = DateTimeOffset.Now,
            target = SecretRedactor.Redact(result.TargetLabel),
            state = result.State.ToString(),
            startedAt = result.StartedAt,
            duration = result.Duration,
            summary = SecretRedactor.Redact(result.StepSummaryText),
            steps = result.Steps.Select(step => new
            {
                id = step.StepId,
                name = SecretRedactor.Redact(step.Name),
                status = step.Status.ToString(),
                duration = step.Duration,
                summary = SecretRedactor.Redact(step.Summary),
                suggestion = SecretRedactor.Redact(step.NextStepSuggestion),
                skipReason = SecretRedactor.Redact(step.SkipReason),
                facts = step.FactItems.Select(fact => new
                {
                    label = SecretRedactor.Redact(fact.Label),
                    value = SecretRedactor.Redact(fact.Value)
                }),
                rawOutput = SecretRedactor.Redact(step.RawOutput)
            }),
            mediaNotice = "未接收 RTP、未解码、未验证首帧；Stream URI 可用不代表视频已经播放。",
            credentialsNotice = "用户名与口令只用于请求构造，不包含在导出中。",
            redactionNotice = "所有导出文本在写入前均已执行敏感信息脱敏。"
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private void OnProgress(DiagnosticProgress progress)
    {
        if (progress.Phase == DiagnosticStepPhase.Finished)
        {
            return;
        }

        CurrentStage = progress.Message;
        ElapsedText = DashboardViewModel.FormatElapsed(progress.Elapsed);

        if (State is TaskRunState.Pending or TaskRunState.Preparing)
        {
            State = TaskRunState.Running;
        }

        AppendLog(LfLogLevel.Information, progress.Message);
    }

    private async Task TickElapsedAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var elapsed = _timeProvider.GetUtcNow() - startedAt;
            _ = PostAsync(() => ElapsedText = DashboardViewModel.FormatElapsed(elapsed));
        }
    }

    private void AppendLog(LfLogLevel level, string message)
    {
        var entry = new LogEntry(
            DateTimeOffset.Now,
            level,
            _adapter.Id,
            _currentRunId,
            SecretRedactor.Redact(BuildTargetLabel()),
            SecretRedactor.Redact(message));

        _ = PostAsync(() => Logs.Add(entry));
    }

    private Task PostAsync(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();
        _uiContext.Post(
            _ =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            },
            null);

        return completion.Task;
    }

    private void NotifyCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStart));
    }

    private static bool IsLoopbackOrLocalName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address)
        && (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any));

    private static string DescribeState(TaskRunState state) => state switch
    {
        TaskRunState.Completed => "已完成",
        TaskRunState.PartiallySucceeded => "部分成功",
        TaskRunState.Failed => "失败",
        TaskRunState.Cancelled => "已取消",
        TaskRunState.Inconclusive => "无有效结论",
        TaskRunState.Running => "执行中",
        TaskRunState.Preparing => "准备中",
        _ => "待运行"
    };

    private static LfLogLevel LogLevelFor(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Passed => LfLogLevel.Information,
        DiagnosticStatus.Warning => LfLogLevel.Warning,
        DiagnosticStatus.Failed or DiagnosticStatus.TimedOut => LfLogLevel.Error,
        DiagnosticStatus.Skipped or DiagnosticStatus.Unknown or DiagnosticStatus.Cancelled => LfLogLevel.Warning,
        _ => LfLogLevel.Debug
    };
}
