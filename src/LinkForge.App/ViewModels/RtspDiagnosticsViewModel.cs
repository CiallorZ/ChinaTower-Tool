using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.Adapters.Video;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Application.Diagnostics;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.App.ViewModels;

/// <summary>
/// RTSP 分阶段诊断页面视图模型（LF-009）。
/// </summary>
/// <remarks>
/// 页面只展示信令与 SDP 能证明的事实；媒体接收、解码、首帧在适配器中明确为
/// Skipped/Unknown，不使用静态占位图伪装播放。URL 中的 userinfo 只进入请求对象，
/// 日志、事实、复制与导出统一使用脱敏文本。
/// </remarks>
public sealed partial class RtspDiagnosticsViewModel : ObservableObject
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(2);

    private readonly RtspDiagnosticAdapter _adapter;
    private readonly DiagnosticTaskRunner _runner;
    private readonly IRtspResultExporter _exporter;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _uiContext;

    private CancellationTokenSource? _runCts;
    private string _currentRunId = string.Empty;
    private DiagnosticRunResult? _lastResult;

    /// <summary>创建 RTSP 诊断视图模型。</summary>
    public RtspDiagnosticsViewModel(
        RtspDiagnosticAdapter adapter,
        DiagnosticTaskRunner runner,
        IRtspResultExporter exporter,
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

        Steps = [];
        Logs = [];
        SummaryItems = [];
        State = TaskRunState.Pending;
    }

    /// <summary>RTSP URL；可包含 userinfo，但展示与导出会脱敏。</summary>
    [ObservableProperty]
    public partial string Url { get; set; } = "rtsp://127.0.0.1:554/live";

    /// <summary>单步超时文本（秒）。</summary>
    [ObservableProperty]
    public partial string TimeoutText { get; set; } = "8";

    /// <summary>URL 字段错误。</summary>
    [ObservableProperty]
    public partial string? UrlError { get; set; }

    /// <summary>超时字段错误。</summary>
    [ObservableProperty]
    public partial string? TimeoutError { get; set; }

    /// <summary>用户确认已获得目标测试授权。</summary>
    [ObservableProperty]
    public partial bool IsAuthorized { get; set; }

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

    /// <summary>是否已有结论。</summary>
    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    /// <summary>操作反馈。</summary>
    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    /// <summary>脱敏展示 URL。</summary>
    public string RedactedUrl => RtspUrlParser.TryParse(Url, out var endpoint, out _)
        ? endpoint.Redacted
        : "URL 尚未通过校验";

    /// <summary>是否需要外部目标授权。</summary>
    public bool RequiresAuthorization =>
        RtspUrlParser.TryParse(Url, out var endpoint, out _)
        && !IsLoopbackOrLocalName(endpoint.Host);

    /// <summary>是否可开始。</summary>
    public bool CanStart => !IsRunning;

    /// <summary>步骤结论。</summary>
    public ObservableCollection<DiagnosticStepResult> Steps { get; }

    /// <summary>脱敏日志。</summary>
    public ObservableCollection<LogEntry> Logs { get; }

    /// <summary>摘要事实。</summary>
    public ObservableCollection<ResultSummaryItem> SummaryItems { get; }

    partial void OnUrlChanged(string value)
    {
        UrlError = null;
        OnPropertyChanged(nameof(RedactedUrl));
        OnPropertyChanged(nameof(RequiresAuthorization));
    }

    /// <summary>执行 RTSP 诊断。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!Validate())
        {
            return;
        }

        if (RequiresAuthorization && !IsAuthorized)
        {
            Conclusion = "尚未确认授权范围，RTSP 诊断未启动。";
            ConclusionNote = "请确认你已获得该视频目标的测试授权，再勾选授权确认。";
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
            CurrentStage = "正在准备 RTSP 诊断…";
            ElapsedText = "00:00.0";
            Conclusion = string.Empty;
            ConclusionNote = string.Empty;
            ActionFeedback = string.Empty;
            Steps.Clear();
            Logs.Clear();
            SummaryItems.Clear();
            NotifyCommands();

            AppendLog(LfLogLevel.Information, $"开始 RTSP 诊断：{RedactedUrl}");
            var ticker = TickElapsedAsync(startedAt, linked.Token);

            try
            {
                CurrentStage = "正在执行 RTSP 信令诊断…";
                var request = new DiagnosticRequest(
                    runId,
                    RedactedUrl,
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [RtspParameterNames.Url] = Url.Trim(),
                        [RtspParameterNames.TimeoutSeconds] = ParseTimeout().ToString()
                    },
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
            Conclusion = "RTSP 诊断已被取消，未产生完整结论。";
            ConclusionNote = "未执行的阶段不会记为失败；如需完整结果请重新运行。";
            HasResult = true;
            AppendLog(LfLogLevel.Warning, "任务已被取消。");
        }
        catch (Exception ex)
        {
            State = TaskRunState.Failed;
            CurrentStage = "执行失败";
            Conclusion = $"RTSP 诊断未能完成：{ex.GetType().Name} — {ex.Message}";
            ConclusionNote = "请查看日志中的技术信息，并检查 URL、DNS 与网络权限。";
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
            ActionFeedback = "已复制脱敏 RTSP 摘要到剪贴板。";
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
            var fileName = $"linkforge-rtsp-{DateTime.Now:yyyyMMdd-HHmmss}.json";
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
        UrlError = null;
        TimeoutError = null;
        var valid = true;

        if (!RtspUrlParser.TryParse(Url, out _, out var error))
        {
            UrlError = error ?? "RTSP URL 无效。";
            valid = false;
        }

        if (!int.TryParse(TimeoutText, out var timeout) || timeout is < 1 or > 60)
        {
            TimeoutError = "超时必须是 1–60 秒的整数。";
            valid = false;
        }

        return valid;
    }

    private int ParseTimeout() => int.TryParse(TimeoutText, out var seconds) ? seconds : 8;

    private static bool IsLoopbackOrLocalName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(host, out var address)
        && (System.Net.IPAddress.IsLoopback(address)
            || address.Equals(System.Net.IPAddress.Any)
            || address.Equals(System.Net.IPAddress.IPv6Any));

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
        SummaryItems.Add(new ResultSummaryItem { Label = "URL", Value = RedactedUrl });
        SummaryItems.Add(new ResultSummaryItem { Label = "耗时", Value = result.DurationText });
        SummaryItems.Add(new ResultSummaryItem
        {
            Label = "结论",
            Value = result.Steps.FirstOrDefault()?.Summary ?? "无步骤结论",
            Status = result.Steps.FirstOrDefault()?.Status
        });
    }

    private string BuildTargetLabel() => RedactedUrl;

    private static string BuildConclusion(DiagnosticRunResult result) => result.State switch
    {
        TaskRunState.Completed => "RTSP 信令与 SDP 诊断完成；媒体接收/解码未执行。",
        TaskRunState.PartiallySucceeded => "RTSP 诊断部分完成，请查看未通过或跳过的阶段。",
        TaskRunState.Failed => result.Steps.FirstOrDefault()?.Summary ?? "RTSP 诊断失败。",
        TaskRunState.Cancelled => "RTSP 诊断已被取消。",
        TaskRunState.Inconclusive => "RTSP 诊断未得到确定性结论。",
        _ => "RTSP 诊断尚未完成。"
    };

    private static string BuildSummaryText(DiagnosticRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("LinkForge RTSP 分阶段诊断结果");
        builder.AppendLine($"目标：{result.TargetLabel}");
        builder.AppendLine($"状态：{DescribeState(result.State)}");
        builder.AppendLine($"开始：{result.StartedAtText}");
        builder.AppendLine($"耗时：{result.DurationText}");
        builder.AppendLine($"步骤：{result.StepSummaryText}");
        builder.AppendLine();

        foreach (var step in result.Steps)
        {
            builder.AppendLine($"[{step.Status}] {step.Name}：{step.Summary}");

            foreach (var fact in step.FactItems)
            {
                builder.AppendLine($"  {fact.Label}: {fact.Value}");
            }

            if (step.HasSuggestion)
            {
                builder.AppendLine($"  建议：{step.NextStepSuggestion}");
            }
        }

        builder.AppendLine();
        builder.Append("说明：媒体接收/解码未在本阶段执行；以上文本已执行敏感信息脱敏。");
        return builder.ToString();
    }

    private static string BuildExportJson(DiagnosticRunResult result)
    {
        var document = new
        {
            product = "LinkForge",
            kind = "rtsp-diagnostic",
            generatedAt = DateTimeOffset.Now,
            target = SecretRedactor.Redact(result.TargetLabel),
            state = result.State.ToString(),
            startedAt = result.StartedAt,
            duration = result.Duration,
            summary = result.StepSummaryText,
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
            mediaNotice = "媒体接收、解码与首帧未在本阶段执行；报告不代表已播放。",
            redactionNotice = "所有文本在导出前均已执行敏感信息脱敏。"
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
            message);

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
        _uiContext.Post(_ =>
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
        }, null);

        return completion.Task;
    }

    private void NotifyCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanStart));
    }

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
