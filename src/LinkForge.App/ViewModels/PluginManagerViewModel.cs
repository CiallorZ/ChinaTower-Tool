using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;
using LinkForge.Plugin.Hosting;

namespace LinkForge.App.ViewModels;

/// <summary>
/// 插件管理页（LF-012）。
/// </summary>
/// <remarks>
/// <para>
/// 本页的全部事实来自磁盘上的插件目录：没有插件就显示空状态并说明放置路径，
/// 校验失败的插件显示阻断原因而不是被隐藏，签名只显示"声明未验证"。
/// 用户据此决定是否执行一个第三方可执行文件，任何一条都不允许用占位数据代替。
/// </para>
/// <para>
/// 运行路径的三道边界都在宿主侧（<see cref="IPluginRunExecutor"/>）实现：
/// 清单校验通过、来源已确认、入口路径重新解析。页面只负责呈现与拒绝原因，
/// 不具备绕过任何一道的位置。
/// </para>
/// </remarks>
public sealed partial class PluginManagerViewModel : ObservableObject
{
    private readonly IPluginCatalogService _catalog;
    private readonly IPluginRunExecutor _executor;
    private readonly IPluginResultExporter _exporter;
    private readonly SynchronizationContext? _uiContext;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _runCts;
    private DiagnosticRunResult? _lastResult;
    private string _currentRunId = string.Empty;

    /// <summary>创建插件管理视图模型。</summary>
    public PluginManagerViewModel(
        IPluginCatalogService catalog,
        IPluginRunExecutor executor,
        IPluginResultExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(exporter);

        _catalog = catalog;
        _executor = executor;
        _exporter = exporter;
        _uiContext = SynchronizationContext.Current;

        Plugins = [];
        Steps = [];
        Logs = [];
        SummaryItems = [];
        ScanState = TaskRunState.Pending;

        // 集合变化必须显式转成属性通知：HasPlugins / IsEmptyAfterScan 是计算属性，
        // 不会因为 Plugins 被填充而自动触发，界面会出现"列表有内容、空状态也显示"的矛盾。
        Plugins.CollectionChanged += OnPluginsCollectionChanged;
    }

    private void OnPluginsCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(IsEmptyAfterScan));
    }

    /// <summary>扫描到的插件列表（含被阻断项）。</summary>
    public ObservableCollection<PluginDisplayItem> Plugins { get; }

    /// <summary>当前选中的插件。</summary>
    [ObservableProperty]
    public partial PluginDisplayItem? SelectedPlugin { get; set; }

    /// <summary>插件目录（告诉用户把插件放在哪里）。</summary>
    public string PluginsDirectory => _catalog.PluginsDirectory;

    /// <summary>是否正在扫描。</summary>
    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    /// <summary>扫描状态。</summary>
    [ObservableProperty]
    public partial TaskRunState ScanState { get; private set; }

    /// <summary>扫描是否已执行过（区分"尚未扫描"与"扫描后为空"）。</summary>
    [ObservableProperty]
    public partial bool HasScanned { get; private set; }

    /// <summary>扫描错误（目录不可读等）。</summary>
    [ObservableProperty]
    public partial string ScanError { get; private set; } = string.Empty;

    /// <summary>扫描结果说明。</summary>
    [ObservableProperty]
    public partial string ScanSummary { get; private set; } = string.Empty;

    /// <summary>运行目标（可选，作为白名单参数下发给插件）。</summary>
    [ObservableProperty]
    public partial string Target { get; set; } = string.Empty;

    /// <summary>用户已确认该插件来源。</summary>
    [ObservableProperty]
    public partial bool IsSourceConfirmed { get; set; }

    /// <summary>是否正在运行插件。</summary>
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

    /// <summary>是否已有运行结论。</summary>
    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    /// <summary>复制或导出的反馈。</summary>
    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    /// <summary>步骤结论。</summary>
    public ObservableCollection<DiagnosticStepResult> Steps { get; }

    /// <summary>日志（写入即脱敏）。</summary>
    public ObservableCollection<LogEntry> Logs { get; }

    /// <summary>摘要事实。</summary>
    public ObservableCollection<ResultSummaryItem> SummaryItems { get; }

    /// <summary>是否存在插件（含被阻断项）。</summary>
    public bool HasPlugins => Plugins.Count > 0;

    /// <summary>是否已选中插件（决定右侧详情面板是否可见）。</summary>
    public bool HasSelectedPlugin => SelectedPlugin is not null;

    /// <summary>右侧是否显示"尚未选择插件"的空状态。</summary>
    public bool IsNoPluginSelected => SelectedPlugin is null;

    /// <summary>扫描是否因目录不可读而失败（与"目录为空"是两种状态）。</summary>
    public bool HasScanFailure => ScanState == TaskRunState.Failed && ScanError.Length > 0;

    /// <summary>是否显示"尚未扫描"的空状态。</summary>
    public bool IsInitialEmpty => !HasScanned && !IsScanning;

    /// <summary>是否显示"目录中没有插件"的空状态。</summary>
    /// <remarks>
    /// 必须排除<b>读取失败</b>："目录读不到"和"目录是空的"是两种结论，
    /// 若都显示"插件目录为空"，用户会以为插件没放对位置，而真实原因是权限或路径问题。
    /// </remarks>
    public bool IsEmptyAfterScan =>
        HasScanned && !HasPlugins && !IsScanning && !HasScanFailure;

    /// <summary>当前插件是否需要来源确认。</summary>
    public bool RequiresTrust =>
        SelectedPlugin is { } plugin && _executor.RequiresTrust(plugin.Descriptor);

    /// <summary>当前插件能否运行。</summary>
    public bool CanRunPlugin =>
        !IsRunning
        && !IsScanning
        && SelectedPlugin is { IsLoadable: true }
        && (!RequiresTrust || IsSourceConfirmed);

    /// <summary>能否扫描。</summary>
    public bool CanScan => !IsScanning && !IsRunning;

    partial void OnSelectedPluginChanged(PluginDisplayItem? value)
    {
        // 切换插件时必须撤销上一次的信任确认：确认只对具体插件生效，
        // 若保留下来就等于"确认了 A，顺手也信任了 B"。
        IsSourceConfirmed = false;
        ClearRunState();
        NotifySelectionState();
        NotifyRunState();
    }

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(HasSelectedPlugin));
        OnPropertyChanged(nameof(IsNoPluginSelected));
        OnPropertyChanged(nameof(RequiresTrust));
    }

    partial void OnIsSourceConfirmedChanged(bool value) => NotifyRunState();

    partial void OnScanErrorChanged(string value) => OnPropertyChanged(nameof(HasScanFailure));

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInitialEmpty));
        OnPropertyChanged(nameof(IsEmptyAfterScan));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanRunPlugin));
        ScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanRunPlugin));
        ScanCommand.NotifyCanExecuteChanged();
        RunPluginCommand.NotifyCanExecuteChanged();
        CancelRunCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasScannedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInitialEmpty));
        OnPropertyChanged(nameof(IsEmptyAfterScan));
    }

    /// <summary>扫描插件目录。</summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scanCts = linked;

        try
        {
            await PostAsync(() =>
            {
                IsScanning = true;
                ScanState = TaskRunState.Running;
                ScanError = string.Empty;
                ScanSummary = $"正在读取 {PluginsDirectory}…";
            }).ConfigureAwait(false);

            IReadOnlyList<PluginDescriptor> descriptors;

            try
            {
                descriptors = await _catalog.ScanAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // 取消扫描不改写已有列表：用户可能在对照上一个结果。
                await PostAsync(() =>
                {
                    IsScanning = false;
                    ScanState = TaskRunState.Cancelled;
                    ScanSummary = "已取消扫描，列表保持上一次结果。";
                }).ConfigureAwait(false);

                return;
            }

            var items = descriptors
                .Select(static descriptor => new PluginDisplayItem { Descriptor = descriptor })
                .ToArray();

            await PostAsync(() =>
            {
                Plugins.Clear();

                foreach (var item in items)
                {
                    Plugins.Add(item);
                }

                SelectedPlugin = items.FirstOrDefault(static item => item.IsLoadable)
                    ?? items.FirstOrDefault();
                HasScanned = true;
                IsScanning = false;
                ScanState = DescribeScanState(items);
                ScanSummary = BuildScanSummary(items);

                // 空目录时 SelectedPlugin 由 null 赋值为 null 不会触发变更通知，
                // 因此选择相关状态在这里统一刷新一次。
                NotifySelectionState();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() =>
            {
                IsScanning = false;
                ScanState = TaskRunState.Cancelled;
                ScanSummary = "已取消扫描。";
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException)
        {
            // 目录不可读是环境问题，不等于"没有插件"：必须给出可读原因（规范 §5.6）。
            await PostAsync(() =>
            {
                IsScanning = false;
                ScanState = TaskRunState.Failed;
                HasScanned = true;
                ScanError = $"{ex.GetType().Name} — {ex.Message}";
                ScanSummary = "插件目录读取失败。";
            }).ConfigureAwait(false);
        }
        finally
        {
            _scanCts = null;
            await PostAsync(() =>
            {
                IsScanning = false;
                NotifyRunState();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>取消扫描。</summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>运行选中插件声明的步骤。</summary>
    [RelayCommand(CanExecute = nameof(CanRunPlugin))]
    private async Task RunPluginAsync(CancellationToken cancellationToken)
    {
        var plugin = SelectedPlugin;

        if (plugin is null)
        {
            return;
        }

        // CanExecute 只是界面层约束：AsyncRelayCommand.Execute 与自动化调用都可能绕过它，
        // 因此这里必须再判一次。"未通过清单校验就不能启动进程"不能只靠按钮置灰。
        if (!plugin.IsLoadable)
        {
            State = TaskRunState.Failed;
            CurrentStage = "已拒绝运行";
            Conclusion = $"插件“{plugin.DisplayName}”未通过清单校验，宿主拒绝运行。";
            ConclusionNote = plugin.ValidationText;
            HasResult = true;
            return;
        }

        if (_executor.RequiresTrust(plugin.Descriptor) && !IsSourceConfirmed)
        {
            // 拒绝启动不是执行失败：没有进程被创建，也没有步骤结论。
            State = TaskRunState.Pending;
            CurrentStage = "未确认来源，插件未启动";
            Conclusion = "该插件的签名未经验证，运行前必须确认来源，任务未启动。";
            ConclusionNote = "核对名称、发布者、版本与入口哈希后勾选来源确认；取消确认不算失败。";
            HasResult = true;
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = linked;
        _currentRunId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.Now;
        var progress = new Progress<DiagnosticProgress>(OnProgress);
        var ticker = TickElapsedAsync(startedAt, linked.Token);

        try
        {
            await PostAsync(() =>
            {
                IsRunning = true;
                State = TaskRunState.Preparing;
                HasResult = false;
                CurrentStage = $"正在为 {plugin.DisplayName} 启动隔离进程…";
                ElapsedText = "00:00.0";
                Conclusion = string.Empty;
                ConclusionNote = string.Empty;
                ActionFeedback = string.Empty;
                Steps.Clear();
                Logs.Clear();
                SummaryItems.Clear();
            }).ConfigureAwait(false);

            AppendLog(LfLogLevel.Information, $"开始运行插件“{plugin.DisplayName}”。");

            DiagnosticRunResult result;

            try
            {
                result = await _executor
                    .RunAsync(
                        plugin.Descriptor,
                        Target,
                        IsSourceConfirmed,
                        progress,
                        linked.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                linked.Cancel();
                await ticker.ConfigureAwait(false);
            }

            await PostAsync(() => ApplyResult(plugin, result)).ConfigureAwait(false);
        }
        catch (PluginTrustNotConfirmedException ex)
        {
            // 拒绝运行不是执行失败：没有启动任何进程，也不产生步骤结论。
            await PostAsync(() =>
            {
                State = TaskRunState.Pending;
                CurrentStage = "未确认来源，插件未启动";
                Conclusion = ex.Message;
                ConclusionNote = "勾选来源确认后才会启动插件进程。";
                HasResult = true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() =>
            {
                State = TaskRunState.Cancelled;
                CurrentStage = "已取消";
                Conclusion = "插件运行已取消，未产生完整结论。";
                ConclusionNote = "取消不等于失败；子进程已终止，主程序未受影响。";
                HasResult = true;
            }).ConfigureAwait(false);

            AppendLog(LfLogLevel.Warning, "任务已被取消。");
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or PluginProcessException)
        {
            await PostAsync(() =>
            {
                State = TaskRunState.Failed;
                CurrentStage = "运行失败";
                Conclusion = $"插件未能运行：{ex.GetType().Name} — {ex.Message}";
                ConclusionNote = "若为插件进程崩溃，主程序不受影响；请核对入口哈希与插件版本。";
                HasResult = true;
            }).ConfigureAwait(false);

            AppendLog(LfLogLevel.Critical, SecretRedactor.Redact(ex.Message) ?? "插件运行失败。");
        }
        finally
        {
            _runCts = null;
            await PostAsync(() =>
            {
                IsRunning = false;
                NotifyRunState();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>停止当前插件进程。</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void CancelRun() => _runCts?.Cancel();

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
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            ActionFeedback = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>导出脱敏 JSON 结果。</summary>
    [RelayCommand]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        if (_lastResult is null)
        {
            return;
        }

        try
        {
            var fileName = $"linkforge-plugin-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var path = await _exporter
                .ExportAsync(fileName, BuildExportJson(_lastResult), cancellationToken)
                .ConfigureAwait(false);

            ActionFeedback = path is null ? "已取消导出。" : $"已导出：{path}";
        }
        catch (OperationCanceledException)
        {
            ActionFeedback = "已取消导出。";
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            ActionFeedback = $"导出失败：{ex.Message}";
        }
    }

    private static TaskRunState DescribeScanState(PluginDisplayItem[] items)
    {
        if (items.Length == 0)
        {
            return TaskRunState.Completed;
        }

        var loadable = items.Count(static item => item.IsLoadable);

        return loadable switch
        {
            0 => TaskRunState.Failed,
            var count when count == items.Length => TaskRunState.Completed,
            _ => TaskRunState.PartiallySucceeded
        };
    }

    private static string BuildScanSummary(PluginDisplayItem[] items)
    {
        if (items.Length == 0)
        {
            return "插件目录中没有发现任何插件。";
        }

        var loadable = items.Count(static item => item.IsLoadable);
        var blocked = items.Length - loadable;

        return blocked == 0
            ? $"发现 {items.Length} 个插件，全部通过清单校验。"
            : $"发现 {items.Length} 个插件：{loadable} 个可加载，{blocked} 个已阻断。";
    }

    private void ClearRunState()
    {
        _lastResult = null;
        Steps.Clear();
        Logs.Clear();
        SummaryItems.Clear();
        HasResult = false;
        State = TaskRunState.Pending;
        CurrentStage = "尚未运行";
        ElapsedText = "00:00.0";
        Conclusion = string.Empty;
        ConclusionNote = string.Empty;
        ActionFeedback = string.Empty;
    }

    private void ApplyResult(PluginDisplayItem plugin, DiagnosticRunResult result)
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
        Conclusion = BuildConclusion(plugin, result);
        ConclusionNote = result.StepSummaryText;

        SummaryItems.Clear();
        SummaryItems.Add(new ResultSummaryItem { Label = "插件", Value = plugin.DisplayName });
        SummaryItems.Add(new ResultSummaryItem { Label = "版本", Value = plugin.Version });
        SummaryItems.Add(new ResultSummaryItem { Label = "API", Value = plugin.ApiVersion });
        SummaryItems.Add(new ResultSummaryItem { Label = "签名", Value = plugin.SignatureText });
        SummaryItems.Add(new ResultSummaryItem { Label = "入口哈希", Value = plugin.HashPrefix });
        SummaryItems.Add(new ResultSummaryItem { Label = "目标", Value = result.TargetLabel });
        SummaryItems.Add(new ResultSummaryItem { Label = "耗时", Value = result.DurationText });
    }

    private static string BuildConclusion(PluginDisplayItem plugin, DiagnosticRunResult result) =>
        result.State switch
        {
            TaskRunState.Completed => $"插件“{plugin.DisplayName}”全部步骤通过。",
            TaskRunState.PartiallySucceeded => $"插件“{plugin.DisplayName}”部分步骤未通过。",
            TaskRunState.Failed => $"插件“{plugin.DisplayName}”执行失败。",
            TaskRunState.Cancelled => $"插件“{plugin.DisplayName}”已被取消。",
            TaskRunState.Inconclusive => $"插件“{plugin.DisplayName}”未给出确定性结论。",
            _ => "插件运行尚未完成。"
        };

    private static string BuildSummaryText(DiagnosticRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("LinkForge 插件运行结果");
        builder.AppendLine($"插件：{SecretRedactor.Redact(result.DisplayName)}");
        builder.AppendLine($"状态：{DescribeState(result.State)}");
        builder.AppendLine($"开始：{result.StartedAtText}");
        builder.AppendLine($"耗时：{result.DurationText}");
        builder.AppendLine($"步骤：{result.StepSummaryText}");
        builder.AppendLine();

        foreach (var step in result.Steps)
        {
            // 页面层再脱敏一次：编排器已经做过，但"复制内容必然脱敏"不应依赖上游是否漏做。
            // 脱敏是幂等的，重复执行只会更安全。
            builder.AppendLine(
                $"[{step.Status}] {SecretRedactor.Redact(step.Name)}：" +
                SecretRedactor.Redact(step.Summary));

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
        builder.Append("说明：插件返回的文本在进入剪贴板前已由宿主再次脱敏。");
        return builder.ToString();
    }

    private static string BuildExportJson(DiagnosticRunResult result)
    {
        var document = new
        {
            product = "LinkForge",
            kind = "plugin-run",
            generatedAt = DateTimeOffset.Now,
            pluginId = result.AdapterId,
            displayName = SecretRedactor.Redact(result.DisplayName),
            target = SecretRedactor.Redact(result.TargetLabel),
            state = result.State.ToString(),
            startedAt = result.StartedAt,
            duration = result.Duration,
            summary = result.StepSummaryText,
            isolation = "进程外执行：每个步骤使用独立子进程，插件崩溃不影响主程序。",
            signatureNotice = "签名状态来自插件清单声明，宿主未验证证书链。",
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

            var elapsed = DateTimeOffset.Now - startedAt;
            _ = PostAsync(() => ElapsedText = DashboardViewModel.FormatElapsed(elapsed));
        }
    }

    private void AppendLog(LfLogLevel level, string message)
    {
        var entry = new LogEntry(
            DateTimeOffset.Now,
            level,
            SelectedPlugin?.Id ?? "plugins",
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

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private void NotifyRunState()
    {
        OnPropertyChanged(nameof(CanRunPlugin));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(RequiresTrust));
        ScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
        RunPluginCommand.NotifyCanExecuteChanged();
        CancelRunCommand.NotifyCanExecuteChanged();
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
