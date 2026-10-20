using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.Adapters.Network;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Application.Diagnostics;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.App.ViewModels;

/// <summary>网络工具下拉项。</summary>
/// <param name="Tool">工具。</param>
/// <param name="Title">显示名称。</param>
/// <param name="Description">一句话说明。</param>
public sealed record NetworkToolOption(NetworkTool Tool, string Title, string Description)
{
    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>
/// 网络诊断页面视图模型（LF-006）。
/// </summary>
/// <remarks>
/// <para>
/// 页面只执行用户选择的单个工具；参数在启动前校验，错误贴近字段显示。
/// 长任务全部异步、可取消，进度与结论来自真实适配器和统一编排器。
/// </para>
/// <para>
/// “授权确认”只针对用户主动输入的外部目标。本机网络与回环地址不需要确认，
/// 这样不会让常规本机排障被无意义的确认弹窗打断。
/// </para>
/// </remarks>
public sealed partial class NetworkDiagnosticsViewModel : ObservableObject
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromMinutes(2);

    private readonly NetworkDiagnosticAdapter _adapter;
    private readonly DiagnosticTaskRunner _runner;
    private readonly INetworkResultExporter _exporter;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _uiContext;

    private CancellationTokenSource? _runCts;
    private string _currentRunId = string.Empty;
    private DiagnosticRunResult? _lastResult;

    /// <summary>创建网络诊断视图模型。</summary>
    public NetworkDiagnosticsViewModel(
        NetworkDiagnosticAdapter adapter,
        DiagnosticTaskRunner runner,
        INetworkResultExporter exporter,
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
            new(NetworkTool.Ping, "Ping / ICMP", "检查是否收到 ICMP 回应；无回应不能单独证明离线。"),
            new(NetworkTool.Tcp, "TCP 端口", "检查目标端口能否建立 TCP 连接。"),
            new(NetworkTool.Dns, "DNS 查询", "解析域名并查看返回地址。"),
            new(NetworkTool.Local, "本机网络", "读取网卡、地址、网关、DNS 与 IPv4 路由。"),
            new(NetworkTool.Traceroute, "Traceroute", "查看路径跳点；中间跳点无响应不代表目标离线。")
        ];

        SelectedTool = Tools[0];
        Steps = [];
        Logs = [];
        SummaryItems = [];
        State = TaskRunState.Pending;
    }

    /// <summary>可用网络工具。</summary>
    public IReadOnlyList<NetworkToolOption> Tools { get; }

    /// <summary>当前选择的工具。</summary>
    [ObservableProperty]
    public partial NetworkToolOption SelectedTool { get; set; }

    /// <summary>目标主机、IP 或域名。</summary>
    [ObservableProperty]
    public partial string Target { get; set; } = string.Empty;

    /// <summary>TCP 端口文本。</summary>
    [ObservableProperty]
    public partial string PortText { get; set; } = "554";

    /// <summary>单步超时文本（秒）。</summary>
    [ObservableProperty]
    public partial string TimeoutText { get; set; } = "5";

    /// <summary>Traceroute 最大跳数文本。</summary>
    [ObservableProperty]
    public partial string MaxHopsText { get; set; } = "16";

    /// <summary>用户确认其有权测试该目标。</summary>
    [ObservableProperty]
    public partial bool IsAuthorized { get; set; }

    /// <summary>目标字段错误。</summary>
    [ObservableProperty]
    public partial string? TargetError { get; set; }

    /// <summary>端口字段错误。</summary>
    [ObservableProperty]
    public partial string? PortError { get; set; }

    /// <summary>超时字段错误。</summary>
    [ObservableProperty]
    public partial string? TimeoutError { get; set; }

    /// <summary>跳数字段错误。</summary>
    [ObservableProperty]
    public partial string? MaxHopsError { get; set; }

    /// <summary>是否正在执行。</summary>
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

    /// <summary>复制或导出的结果反馈。</summary>
    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    /// <summary>步骤结论。</summary>
    public ObservableCollection<DiagnosticStepResult> Steps { get; }

    /// <summary>日志（写入即脱敏）。</summary>
    public ObservableCollection<LogEntry> Logs { get; }

    /// <summary>摘要事实。</summary>
    public ObservableCollection<ResultSummaryItem> SummaryItems { get; }

    /// <summary>是否需要端口字段。</summary>
    public bool RequiresPort => SelectedTool.Tool == NetworkTool.Tcp;

    /// <summary>是否需要目标字段。</summary>
    public bool RequiresTarget => SelectedTool.Tool != NetworkTool.Local;

    /// <summary>是否需要跳数。</summary>
    public bool RequiresMaxHops => SelectedTool.Tool == NetworkTool.Traceroute;

    /// <summary>外部目标是否需要授权确认。</summary>
    public bool RequiresAuthorization =>
        RequiresTarget
        && !string.IsNullOrWhiteSpace(Target)
        && !IsLoopbackOrLocalName(Target);

    /// <summary>能否开始。</summary>
    public bool CanStart => !IsRunning;

    /// <summary>工具选择变化时刷新字段可见性。</summary>
    partial void OnSelectedToolChanged(NetworkToolOption value)
    {
        OnPropertyChanged(nameof(RequiresPort));
        OnPropertyChanged(nameof(RequiresTarget));
        OnPropertyChanged(nameof(RequiresMaxHops));
        OnPropertyChanged(nameof(RequiresAuthorization));

        TargetError = null;
        PortError = null;
        MaxHopsError = null;
    }

    /// <summary>目标变化时刷新授权提示。</summary>
    partial void OnTargetChanged(string value)
    {
        TargetError = null;
        OnPropertyChanged(nameof(RequiresAuthorization));
    }

    /// <summary>执行网络诊断。</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!Validate())
        {
            return;
        }

        if (RequiresAuthorization && !IsAuthorized)
        {
            Conclusion = "尚未确认授权范围，任务未启动。";
            ConclusionNote = "请确认你已获得测试该外部目标的授权，再勾选授权确认。";
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
            CurrentStage = "正在准备网络诊断…";
            ElapsedText = "00:00.0";
            Conclusion = string.Empty;
            ConclusionNote = string.Empty;
            Steps.Clear();
            Logs.Clear();
            SummaryItems.Clear();
            NotifyCommands();

            AppendLog(LfLogLevel.Information, $"开始“{_adapter.DisplayName}”：{SelectedTool.Title}");

            var ticker = TickElapsedAsync(startedAt, linked.Token);

            try
            {
                CurrentStage = "正在执行网络探测…";

                var request = new DiagnosticRequest(
                    runId,
                    BuildTargetLabel(),
                    BuildParameters(),
                    TimeSpan.FromSeconds(ParseTimeout()));

                var result = await _runner
                    .RunAsync(
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
            Conclusion = "网络诊断已被取消，未产生完整结论。";
            ConclusionNote = "未执行的探测不会记为失败；如需完整结果请重新运行。";
            HasResult = true;
            AppendLog(LfLogLevel.Warning, "任务已被取消。");
        }
        catch (Exception ex)
        {
            State = TaskRunState.Failed;
            CurrentStage = "执行失败";
            Conclusion = $"网络诊断未能完成：{ex.GetType().Name} — {ex.Message}";
            ConclusionNote = "请查看日志中的技术信息，并检查参数与系统网络权限。";
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

    /// <summary>复制脱敏结论摘要。</summary>
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
            ActionFeedback = "已复制脱敏结论摘要到剪贴板。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ActionFeedback = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>导出结构化 JSON 结果。</summary>
    [RelayCommand]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (_lastResult is null)
        {
            return;
        }

        try
        {
            var fileName = $"linkforge-network-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var path = await _exporter
                .ExportAsync(fileName, BuildExportJson(_lastResult), cancellationToken);

            ActionFeedback = path is null
                ? "已取消导出。"
                : $"已导出：{path}";
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

    /// <summary>校验输入。</summary>
    private bool Validate()
    {
        TargetError = null;
        PortError = null;
        TimeoutError = null;
        MaxHopsError = null;

        var valid = true;

        if (RequiresTarget)
        {
            var target = Target.Trim();

            if (string.IsNullOrWhiteSpace(target))
            {
                TargetError = "请输入主机名、域名或 IP 地址。";
                valid = false;
            }
            else if (!IsValidTarget(target))
            {
                TargetError = "目标格式无效：请勿包含协议、路径或空格。";
                valid = false;
            }
        }

        if (RequiresPort
            && (!int.TryParse(PortText, out var port) || port is < 1 or > 65535))
        {
            PortError = "端口必须是 1–65535 的整数。";
            valid = false;
        }

        if (!int.TryParse(TimeoutText, out var timeout) || timeout is < 1 or > 60)
        {
            TimeoutError = "超时必须是 1–60 秒的整数。";
            valid = false;
        }

        if (RequiresMaxHops
            && (!int.TryParse(MaxHopsText, out var hops) || hops is < 1 or > 30))
        {
            MaxHopsError = "最大跳数必须是 1–30 的整数。";
            valid = false;
        }

        return valid;
    }

    /// <summary>
    /// 判断目标格式。
    /// </summary>
    /// <remarks>
    /// 只接受主机名/IP，不接受 URL。这样既避免把 <c>http://</c> 或路径误送进网络 API，
    /// 也避免完整带凭据的 URL 进入日志。
    /// </remarks>
    private static bool IsValidTarget(string target)
    {
        if (target.Contains("://", StringComparison.Ordinal)
            || target.Contains('/')
            || target.Contains('\\')
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
            [NetworkParameterNames.Tool] = SelectedTool.Tool.ToString(),
            [NetworkParameterNames.Target] = Target.Trim(),
            [NetworkParameterNames.Port] = PortText.Trim(),
            [NetworkParameterNames.TimeoutSeconds] = ParseTimeout().ToString(),
            [NetworkParameterNames.MaxHops] = MaxHopsText.Trim()
        };

    private int ParseTimeout() => int.TryParse(TimeoutText, out var seconds) ? seconds : 5;

    private string BuildTargetLabel() => SelectedTool.Tool == NetworkTool.Local
        ? "本机网络"
        : SelectedTool.Tool == NetworkTool.Tcp
            ? $"{Target.Trim()}:{PortText.Trim()}"
            : Target.Trim();

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
        SummaryItems.Add(new ResultSummaryItem { Label = "目标", Value = result.TargetLabel });
        SummaryItems.Add(new ResultSummaryItem { Label = "耗时", Value = result.DurationText });
        SummaryItems.Add(new ResultSummaryItem
        {
            Label = "结论",
            Value = result.Steps.FirstOrDefault()?.Summary ?? "无步骤结论",
            Status = result.Steps.FirstOrDefault()?.Status
        });

    }

    private static string BuildSummaryText(DiagnosticRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("LinkForge 网络诊断结果");
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
        builder.Append("说明：以上文本在进入剪贴板前已执行敏感信息脱敏。");
        return builder.ToString();
    }

    private static string BuildExportJson(DiagnosticRunResult result)
    {
        var document = new
        {
            product = "LinkForge",
            kind = "network-diagnostic",
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
            redactionNotice = "所有文本在导出前均已执行敏感信息脱敏。"
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string BuildConclusion(DiagnosticRunResult result)
    {
        var first = result.Steps.FirstOrDefault();

        return result.State switch
        {
            TaskRunState.Completed => first?.Summary ?? "网络诊断完成。",
            TaskRunState.PartiallySucceeded => first?.Summary ?? "网络诊断部分完成。",
            TaskRunState.Failed => first?.Summary ?? "网络诊断失败。",
            TaskRunState.Cancelled => "网络诊断已被取消。",
            TaskRunState.Inconclusive => first?.Summary ?? "未得到确定性结论。",
            _ => "网络诊断尚未完成。"
        };
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
            SecretRedactor.Redact(Target.Trim()),
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
