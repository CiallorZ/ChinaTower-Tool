using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.Adapters.Security;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Application.Diagnostics;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.App.ViewModels;

/// <summary>
/// 安全中心视图模型（LF-020）。
/// </summary>
/// <remarks>
/// 只执行用户显式列出的单目标、有限端口 TCP/TLS 基线检查。
/// 本页不提供漏洞利用、口令爆破、规避扫描、网段扫描或破坏性操作；
/// 端口开放与证书告警都必须保留为“需要进一步核验的事实”，不能升级为漏洞结论。
/// </remarks>
public sealed partial class SecurityCenterViewModel : ObservableObject
{
    private const int MaxTcpPorts = 32;
    private const int MaxTlsPorts = 8;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(2);

    private readonly SecurityDiagnosticAdapter _adapter;
    private readonly DiagnosticTaskRunner _runner;
    private readonly ISecurityResultExporter _exporter;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _uiContext;

    private CancellationTokenSource? _runCts;
    private string _currentRunId = string.Empty;
    private DiagnosticRunResult? _lastResult;

    /// <summary>创建安全中心视图模型。</summary>
    public SecurityCenterViewModel(
        SecurityDiagnosticAdapter adapter,
        DiagnosticTaskRunner runner,
        ISecurityResultExporter exporter,
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

    /// <summary>单一目标主机、域名或 IP。</summary>
    [ObservableProperty]
    public partial string Target { get; set; } = "127.0.0.1";

    /// <summary>显式 TCP 端口列表；支持逗号和闭区间。</summary>
    [ObservableProperty]
    public partial string PortsText { get; set; } = "443";

    /// <summary>需要 TLS 握手的端口；必须属于 TCP 范围。</summary>
    [ObservableProperty]
    public partial string TlsPortsText { get; set; } = "443";

    /// <summary>单个连接/握手超时秒数。</summary>
    [ObservableProperty]
    public partial string TimeoutText { get; set; } = "5";

    /// <summary>并发上限，1–4。</summary>
    [ObservableProperty]
    public partial string ConcurrencyText { get; set; } = "2";

    /// <summary>相邻检查启动延迟毫秒数。</summary>
    [ObservableProperty]
    public partial string DelayMillisecondsText { get; set; } = "200";

    /// <summary>用户确认已获得目标测试授权。</summary>
    [ObservableProperty]
    public partial bool IsAuthorized { get; set; }

    /// <summary>目标字段错误。</summary>
    [ObservableProperty]
    public partial string? TargetError { get; set; }

    /// <summary>TCP 端口字段错误。</summary>
    [ObservableProperty]
    public partial string? PortsError { get; set; }

    /// <summary>TLS 端口字段错误。</summary>
    [ObservableProperty]
    public partial string? TlsPortsError { get; set; }

    /// <summary>超时字段错误。</summary>
    [ObservableProperty]
    public partial string? TimeoutError { get; set; }

    /// <summary>并发字段错误。</summary>
    [ObservableProperty]
    public partial string? ConcurrencyError { get; set; }

    /// <summary>延迟字段错误。</summary>
    [ObservableProperty]
    public partial string? DelayError { get; set; }

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

    /// <summary>是否已有运行结果。</summary>
    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    /// <summary>复制或导出反馈。</summary>
    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    /// <summary>已脱敏的原始输出汇总。</summary>
    [ObservableProperty]
    public partial string RawOutputText { get; private set; } = string.Empty;

    /// <summary>步骤结论。</summary>
    public ObservableCollection<DiagnosticStepResult> Steps { get; }

    /// <summary>脱敏日志。</summary>
    public ObservableCollection<LogEntry> Logs { get; }

    /// <summary>摘要事实。</summary>
    public ObservableCollection<ResultSummaryItem> SummaryItems { get; }

    /// <summary>始终可见的当前检查范围。</summary>
    public string ScopeSummary
    {
        get
        {
            var tcp = SecurityPortParser.TryParse(PortsText, MaxTcpPorts, out var ports, out _)
                ? SecurityPortParser.Format(ports)
                : "待校验";
            var tls = string.IsNullOrWhiteSpace(TlsPortsText)
                ? "未请求"
                : SecurityPortParser.TryParse(TlsPortsText, MaxTlsPorts, out var tlsPorts, out _)
                    ? SecurityPortParser.Format(tlsPorts)
                    : "待校验";

            return $"目标 {Target.Trim()} · TCP {tcp} · TLS {tls} · 最多 {MaxTcpPorts} 个端口";
        }
    }

    /// <summary>外部目标是否需要授权确认。</summary>
    public bool RequiresAuthorization =>
        !string.IsNullOrWhiteSpace(Target)
        && !IsLoopbackOrLocalName(Target);

    /// <summary>是否可开始。</summary>
    public bool CanStart => !IsRunning;

    partial void OnTargetChanged(string value)
    {
        TargetError = null;
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(RequiresAuthorization));
    }

    partial void OnPortsTextChanged(string value)
    {
        PortsError = null;
        OnPropertyChanged(nameof(ScopeSummary));
    }

    partial void OnTlsPortsTextChanged(string value)
    {
        TlsPortsError = null;
        OnPropertyChanged(nameof(ScopeSummary));
    }

    /// <summary>执行一次授权范围内的安全基线检查。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!Validate())
        {
            return;
        }

        if (RequiresAuthorization && !IsAuthorized)
        {
            Conclusion = "尚未确认授权范围，安全基线检查未启动。";
            ConclusionNote = "请确认你已获得该外部目标的书面测试授权，再勾选授权确认。";
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
            CurrentStage = "正在准备安全基线检查…";
            ElapsedText = "00:00.0";
            Conclusion = string.Empty;
            ConclusionNote = string.Empty;
            ActionFeedback = string.Empty;
            RawOutputText = string.Empty;
            Steps.Clear();
            Logs.Clear();
            SummaryItems.Clear();
            NotifyCommands();

            AppendLog(LfLogLevel.Information, $"开始安全基线检查：{BuildTargetLabel()}");
            var ticker = TickElapsedAsync(startedAt, linked.Token);

            try
            {
                CurrentStage = "正在检查显式端口与 TLS 证书基线…";
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
            Conclusion = "安全基线检查已被取消，未产生完整结论。";
            ConclusionNote = "未执行的端口检查不会记为失败；如需完整结果请重新运行。";
            HasResult = true;
            AppendLog(LfLogLevel.Warning, "任务已被取消。");
        }
        catch (Exception ex)
        {
            State = TaskRunState.Failed;
            CurrentStage = "执行失败";
            Conclusion = $"安全基线检查未能完成：{ex.GetType().Name} — {ex.Message}";
            ConclusionNote = "请查看日志中的技术信息，并检查目标、DNS、TLS 配置与网络权限。";
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

    /// <summary>取消当前检查。</summary>
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
            ActionFeedback = "已复制脱敏安全基线摘要到剪贴板。";
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
            var fileName = $"linkforge-security-{DateTime.Now:yyyyMMdd-HHmmss}.json";
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
        TargetError = null;
        PortsError = null;
        TlsPortsError = null;
        TimeoutError = null;
        ConcurrencyError = null;
        DelayError = null;
        var valid = true;

        var target = Target.Trim();

        if (string.IsNullOrWhiteSpace(target))
        {
            TargetError = "请输入单个主机名、域名或 IP 地址。";
            valid = false;
        }
        else if (!IsValidTarget(target))
        {
            TargetError = "目标格式无效：只填写一个主机名或 IP，不要包含协议、路径、端口或空格。";
            valid = false;
        }

        if (!SecurityPortParser.TryParse(PortsText, MaxTcpPorts, out var tcpPorts, out var portsError))
        {
            PortsError = portsError;
            valid = false;
            tcpPorts = [];
        }

        IReadOnlyList<int> tlsPorts = [];
        if (!string.IsNullOrWhiteSpace(TlsPortsText))
        {
            if (!SecurityPortParser.TryParse(
                    TlsPortsText,
                    MaxTlsPorts,
                    out tlsPorts,
                    out var tlsError))
            {
                TlsPortsError = tlsError;
                valid = false;
            }
            else if (tlsPorts.Any(port => !tcpPorts.Contains(port)))
            {
                TlsPortsError = "TLS 端口必须包含在本次 TCP 端口范围内。";
                valid = false;
            }
        }

        if (!int.TryParse(TimeoutText, out var timeout) || timeout is < 1 or > 60)
        {
            TimeoutError = "超时必须是 1–60 秒的整数。";
            valid = false;
        }

        if (!int.TryParse(ConcurrencyText, out var concurrency) || concurrency is < 1 or > 4)
        {
            ConcurrencyError = "并发必须是 1–4 的整数。";
            valid = false;
        }

        if (!int.TryParse(DelayMillisecondsText, out var delay) || delay is < 0 or > 2000)
        {
            DelayError = "检查间隔必须是 0–2000 毫秒的整数。";
            valid = false;
        }

        return valid;
    }

    private static bool IsValidTarget(string target)
    {
        if (target.Contains("://", StringComparison.Ordinal)
            || target.Contains('/')
            || target.Contains('\\')
            || target.Contains('@')
            || target.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (IPAddress.TryParse(target, out _))
        {
            return true;
        }

        if (target.Length > 253 || target.StartsWith('.') || target.EndsWith('.'))
        {
            return false;
        }

        return Uri.CheckHostName(target) != UriHostNameType.Unknown;
    }

    private static bool IsLoopbackOrLocalName(string target)
    {
        var trimmed = target.Trim();

        return trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
               || IPAddress.TryParse(trimmed, out var address)
               && (IPAddress.IsLoopback(address)
                   || address.Equals(IPAddress.Any)
                   || address.Equals(IPAddress.IPv6Any));
    }

    private Dictionary<string, string?> BuildParameters() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [SecurityParameterNames.Target] = Target.Trim(),
            [SecurityParameterNames.Ports] = PortsText.Trim(),
            [SecurityParameterNames.TlsPorts] = TlsPortsText.Trim(),
            [SecurityParameterNames.TimeoutSeconds] = ParseTimeout().ToString(),
            [SecurityParameterNames.Concurrency] = ParseConcurrency().ToString(),
            [SecurityParameterNames.DelayMilliseconds] = ParseDelay().ToString()
        };

    private int ParseTimeout() => int.TryParse(TimeoutText, out var value) ? value : 5;

    private int ParseConcurrency() => int.TryParse(ConcurrencyText, out var value) ? value : 2;

    private int ParseDelay() => int.TryParse(DelayMillisecondsText, out var value) ? value : 200;

    private string BuildTargetLabel() => $"{Target.Trim()} · TCP {PortsText.Trim()}";

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

        RawOutputText = string.Join(
            Environment.NewLine + Environment.NewLine,
            result.Steps
                .Where(static step => step.HasRawOutput)
                .Select(static step => step.RawOutput));

        State = result.State;
        HasResult = true;
        ElapsedText = DashboardViewModel.FormatElapsed(result.Duration ?? TimeSpan.Zero);
        CurrentStage = DescribeState(result.State);
        Conclusion = BuildConclusion(result);
        ConclusionNote = result.State == TaskRunState.Cancelled
            ? "未执行的端口检查不会记为失败；如需完整结果请重新运行。"
            : result.StepSummaryText;

        SummaryItems.Clear();
        SummaryItems.Add(new ResultSummaryItem { Label = "目标", Value = result.TargetLabel });
        SummaryItems.Add(new ResultSummaryItem { Label = "检查范围", Value = ScopeSummary });
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
        TaskRunState.Completed => "授权范围内的 TCP/TLS 基线检查完成；未发现需要提示的证书问题不等于“无漏洞”。",
        TaskRunState.PartiallySucceeded => "安全基线检查部分完成，请查看未通过、超时或跳过的项目。",
        TaskRunState.Failed => result.Steps.FirstOrDefault()?.Summary ?? "安全基线检查失败。",
        TaskRunState.Cancelled => "安全基线检查已被取消。",
        TaskRunState.Inconclusive => "安全基线检查未得到确定性结论；过滤或无响应不能证明主机离线。",
        _ => "安全基线检查尚未完成。"
    };

    private static string BuildSummaryText(DiagnosticRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("LinkForge 安全基线检查结果");
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
        builder.Append("边界：只覆盖用户显式列出的端口；端口开放、过滤或无响应不等于漏洞结论；不支持漏洞利用、口令爆破或网段扫描。");
        return builder.ToString();
    }

    private static string BuildExportJson(DiagnosticRunResult result)
    {
        var document = new
        {
            product = "LinkForge",
            kind = "security-baseline",
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
                facts = step.FactItems.Select(fact => new
                {
                    label = SecretRedactor.Redact(fact.Label),
                    value = SecretRedactor.Redact(fact.Value)
                }),
                rawOutput = SecretRedactor.Redact(step.RawOutput)
            }),
            boundary = "只覆盖用户显式列出的单目标端口；不包含漏洞利用、口令爆破、网段扫描或规避检测。",
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
