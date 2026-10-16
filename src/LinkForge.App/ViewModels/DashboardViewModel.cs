using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Application.Diagnostics;
using LinkForge.Application.Environment;
using LinkForge.Domain.Diagnostics;
using LinkForge.Domain.Environment;
using LinkForge.Plugin.Abstractions;

namespace LinkForge.App.ViewModels;

/// <summary>
/// 一条"环境事实"（工作台展示用）。
/// </summary>
/// <param name="Label">项目名称。</param>
/// <param name="Value">真实取值。</param>
public readonly record struct EnvironmentFact(string Label, string Value);

/// <summary>
/// 工作台视图模型。
/// </summary>
/// <remarks>
/// <para>
/// 依据 UI 规范 §4.1 与 DEV_STANDARDS §6.6：
/// <b>工作台只显示真实数据，没有真实数据时显示空状态，不编造数值。</b>
/// </para>
/// <para>
/// 因此本页呈现的四块内容分别是：
/// </para>
/// <list type="number">
///   <item>运行环境（版本、运行时、系统、架构、配置位置）——直接读取，必然为真。</item>
///   <item>依赖探测结果——真实探测，缺失就如实标注"未检测到"。</item>
///   <item>最近任务——来自 <see cref="IRunHistoryStore"/> 的真实历史，无记录时显示空状态。</item>
///   <item>运行环境自检——一个<b>真实</b>的诊断任务，可运行、可取消、有步骤结论与证据。</item>
/// </list>
/// <para>
/// "快捷动作"只列出已实现的动作。当前唯一已实现的诊断能力是运行环境自检，
/// 因此快捷动作区只有这一个入口，而不是放一排点了没反应的按钮。
/// </para>
/// <para>
/// 线程模型：所有耗时操作（依赖探测、历史读写、任务执行）都是 <c>async</c> 且可取消；
/// 集合的增删只发生在 UI 线程（进度通过 <c>Progress&lt;T&gt;</c> 回到捕获的上下文），
/// 避免在后台线程改 <c>ObservableCollection</c> 触发"集合已修改"异常。
/// </para>
/// </remarks>
public sealed partial class DashboardViewModel : ObservableObject
{
    /// <summary>最近任务列表的最大条数（规范 §4.1：常用工具入口限制数量）。</summary>
    public const int RecentRunLimit = 6;

    /// <summary>自检任务的总超时。</summary>
    private static readonly TimeSpan SelfCheckOverallTimeout = TimeSpan.FromSeconds(90);

    /// <summary>已运行时间的刷新间隔。</summary>
    private static readonly TimeSpan ElapsedTickInterval = TimeSpan.FromMilliseconds(250);

    private readonly AppEnvironment _environment;
    private readonly DependencyCatalog _dependencyCatalog;
    private readonly IRunHistoryStore _runHistoryStore;
    private readonly IDiagnosticAdapter _selfCheckAdapter;
    private readonly DiagnosticTaskRunner _taskRunner;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _uiContext;

    private CancellationTokenSource? _runCts;
    private int _loadCount;
    private int _dependencyLoadCount;
    private string _currentRunId = string.Empty;

    /// <summary>
    /// 创建工作台视图模型。
    /// </summary>
    /// <param name="environment">环境信息读取器。</param>
    /// <param name="dependencyCatalog">外部依赖探测（异步、可取消）。</param>
    /// <param name="runHistoryStore">任务历史存储。</param>
    /// <param name="selfCheckAdapter">运行环境自检适配器。</param>
    /// <param name="taskRunner">任务编排器。</param>
    /// <param name="timeProvider">时间源；null 时使用系统时钟。</param>
    public DashboardViewModel(
        AppEnvironment environment,
        DependencyCatalog dependencyCatalog,
        IRunHistoryStore runHistoryStore,
        IDiagnosticAdapter selfCheckAdapter,
        DiagnosticTaskRunner taskRunner,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(dependencyCatalog);
        ArgumentNullException.ThrowIfNull(runHistoryStore);
        ArgumentNullException.ThrowIfNull(selfCheckAdapter);
        ArgumentNullException.ThrowIfNull(taskRunner);

        _environment = environment;
        _dependencyCatalog = dependencyCatalog;
        _runHistoryStore = runHistoryStore;
        _selfCheckAdapter = selfCheckAdapter;
        _taskRunner = taskRunner;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // 构造时捕获：命令与进度回调都在 UI 线程发起，
        // 之后所有集合变更都经此上下文回到同一线程。
        _uiContext = SynchronizationContext.Current;

        EnvironmentFacts =
        [
            new("应用版本", _environment.ApplicationVersion),
            new("运行时", _environment.RuntimeDescription),
            new("操作系统", _environment.WindowsVersionName),
            new("进程架构", _environment.ProcessArchitecture),
            new("配置位置", _environment.SettingsFilePath ?? "未知"),
            new("数据目录", _environment.SettingsDirectory ?? "未知")
        ];

        Dependencies = [];
        MissingDependencies = [];
        RecentRuns = [];
        SelfCheckSteps = [];
        SelfCheckLogs = [];
        SummaryItems = [];

        // 首屏立即呈现加载态：数据尚未读取时不能显示"0 个依赖、0 条任务"，
        // 那会被读成"环境干净"，与未检测是两回事。
        IsLoading = true;
        SelfCheckState = TaskRunState.Pending;
        CurrentStage = "尚未运行";
        ElapsedText = "00:00.0";
    }

    /// <summary>运行环境事实。</summary>
    public IReadOnlyList<EnvironmentFact> EnvironmentFacts { get; }

    /// <summary>外部依赖探测结果（全部）。</summary>
    public ObservableCollection<DependencyProbe> Dependencies { get; }

    /// <summary>不可用的依赖（用于依赖缺失提示区）。</summary>
    public ObservableCollection<DependencyProbe> MissingDependencies { get; }

    /// <summary>最近任务（来自真实历史，倒序）。</summary>
    public ObservableCollection<DiagnosticRunResult> RecentRuns { get; }

    /// <summary>当前自检任务的步骤结论。</summary>
    public ObservableCollection<DiagnosticStepResult> SelfCheckSteps { get; }

    /// <summary>自检任务的日志（写入时即已脱敏）。</summary>
    public ObservableCollection<LogEntry> SelfCheckLogs { get; }

    /// <summary>自检结论摘要中的事实项。</summary>
    public ObservableCollection<ResultSummaryItem> SummaryItems { get; }

    /// <summary>是否正在读取工作台数据（首屏加载态）。</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>工作台数据读取失败时的说明；成功时为空。</summary>
    [ObservableProperty]
    public partial string? LoadErrorMessage { get; private set; }

    /// <summary>是否正在探测依赖。</summary>
    [ObservableProperty]
    public partial bool IsProbingDependencies { get; private set; }

    /// <summary>是否正在读取历史。</summary>
    [ObservableProperty]
    public partial bool IsLoadingHistory { get; private set; }

    /// <summary>自检任务是否处于执行期。</summary>
    [ObservableProperty]
    public partial bool IsSelfCheckRunning { get; private set; }

    /// <summary>
    /// 是否允许开始自检（同一时刻只允许一个任务）。
    /// </summary>
    /// <remarks>
    /// 作为 <see cref="ObservableProperty"/> 而不是普通只读属性：
    /// XAML 绑定它控制按钮的 <c>IsEnabled</c>，
    /// 若不发变更通知，按钮会在任务结束后一直保持禁用
    /// （命令自己的 CanExecuteChanged 只影响 Command 绑定的按钮，
    /// 这里是直接绑定布尔值的 IsEnabled，必须自己发通知）。
    /// </remarks>
    [ObservableProperty]
    public partial bool CanStartSelfCheck { get; private set; } = true;

    /// <summary>自检任务的任务层状态。</summary>
    [ObservableProperty]
    public partial TaskRunState SelfCheckState { get; private set; }

    /// <summary>自检当前阶段描述。</summary>
    [ObservableProperty]
    public partial string CurrentStage { get; private set; }

    /// <summary>自检已运行时间文本。</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; private set; }

    /// <summary>自检结论的一句话摘要。</summary>
    [ObservableProperty]
    public partial string Conclusion { get; private set; } = string.Empty;

    /// <summary>结论补充（步骤统计、跳过原因等）。</summary>
    [ObservableProperty]
    public partial string ConclusionNote { get; private set; } = string.Empty;

    /// <summary>是否已产出过自检结论。</summary>
    [ObservableProperty]
    public partial bool HasSelfCheckResult { get; private set; }

    /// <summary>是否存在缺失依赖。</summary>
    public bool HasMissingDependencies => MissingDependencies.Count > 0;

    /// <summary>是否存在最近任务。</summary>
    public bool HasRecentRuns => RecentRuns.Count > 0;

    /// <summary>
    /// 是否允许开始自检（同一时刻只允许一个任务）。
    /// </summary>
    /// <remarks>
    /// 公开而非私有：XAML 用它绑定快捷动作按钮的 <c>IsEnabled</c>，
    /// 让"不可点"有视觉表达，而不是点了没反应（规范 §4.2：
    /// 不允许用户误以为重复点击会启动第二个任务）。
    /// </remarks>

    /// <summary>
    /// 是否已有项目。
    /// </summary>
    /// <remarks>
    /// 阶段 04 仍未引入项目持久化（LF-004 交付），因此恒为 false 并显示空状态。
    /// 保留为属性：接入仓储后只需改为读取真实数据，页面与空状态判断无需改动。
    /// </remarks>
    public bool HasProjects => false;

    /// <summary>空状态文案：说明为什么是空的，以及下一步该做什么。</summary>
    public string ProjectsEmptyDescription =>
        "尚未创建项目。项目管理功能将在后续阶段交付，届时可在此新建或导入项目。";

    /// <inheritdoc cref="ProjectsEmptyDescription"/>
    public string RunsEmptyDescription =>
        "尚无任务记录。点击上方“运行环境自检”即可产生第一条记录，结果会汇总到这里。";

    /// <summary>
    /// 读取工作台数据（依赖探测 + 最近任务）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 由组合根在窗口显示后调用。失败不抛：把原因写进
    /// <see cref="LoadErrorMessage"/> 交由 <c>ErrorPanel</c> 呈现并给出重试入口 ——
    /// 一个读不到历史文件的异常若冒到启动流程，会让整个应用起不来。
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 并发重入保护：用户连续点击"重新检测"会并发改同一个集合，
        // 表现为依赖列表闪烁甚至重复项。
        if (Interlocked.Increment(ref _loadCount) > 1)
        {
            Interlocked.Decrement(ref _loadCount);
            return;
        }

        try
        {
            IsLoading = true;
            LoadErrorMessage = null;

            await RefreshDependenciesAsync(cancellationToken).ConfigureAwait(false);
            await LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消是用户主动行为，不是错误：保持当前数据，不写错误面板。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoadErrorMessage = $"读取工作台数据失败：{ex.GetType().Name} — {ex.Message}";
        }
        finally
        {
            Interlocked.Decrement(ref _loadCount);
            IsLoading = false;
        }
    }

    /// <summary>
    /// 重新探测依赖。
    /// </summary>
    /// <remarks>
    /// 用户可能在应用运行期间安装了 Npcap 或 FFmpeg，
    /// 因此需要手动刷新入口，而不是要求重启应用。
    /// 探测会启动外部进程，必须异步且可取消 —— 一个卡住的工具不该冻住界面。
    /// </remarks>
    [RelayCommand]
    private async Task RefreshDependenciesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _dependencyLoadCount) > 1)
        {
            Interlocked.Decrement(ref _dependencyLoadCount);
            return;
        }

        try
        {
            IsProbingDependencies = true;

            var probes = await _dependencyCatalog
                .ProbeAllAsync(cancellationToken)
                .ConfigureAwait(false);

            await PostAsync(() =>
            {
                Dependencies.Clear();

                foreach (var probe in probes)
                {
                    Dependencies.Add(probe);
                }

                MissingDependencies.Clear();

                foreach (var probe in probes.Where(static p => !p.IsAvailable))
                {
                    MissingDependencies.Add(probe);
                }

                OnPropertyChanged(nameof(HasMissingDependencies));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消时不改动列表：保留上一次的真实结果比清空更符合预期。
        }
        finally
        {
            Interlocked.Decrement(ref _dependencyLoadCount);
            IsProbingDependencies = false;
        }
    }

    /// <summary>读取最近任务。</summary>
    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        IsLoadingHistory = true;

        try
        {
            var runs = await _runHistoryStore
                .LoadAsync(RecentRunLimit, cancellationToken)
                .ConfigureAwait(false);

            await PostAsync(() =>
            {
                RecentRuns.Clear();

                foreach (var run in runs)
                {
                    RecentRuns.Add(run);
                }

                OnPropertyChanged(nameof(HasRecentRuns));
            }).ConfigureAwait(false);
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }

    /// <summary>
    /// 运行"运行环境自检"。
    /// </summary>
    /// <param name="cancellationToken">取消令牌（由命令提供）。</param>
    /// <remarks>
    /// 与 <see cref="CancelSelfCheck"/> 配对：长任务必须提供取消出口（规范 §6）。
    /// 取消与超时在编排器内被映射为 Cancelled / TimedOut，绝不记成失败。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStartSelfCheck))]
    private async Task RunSelfCheckAsync(CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");

        // 日志的 RunId 关联必须在首个 await 之前完成：
        // 进度事件与结论日志共用它，事后补会让首批日志落不到本次运行上。
        _currentRunId = runId;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 保存引用供取消命令调用；using 保证退出时释放，不会泄漏 CTS。
        _runCts = linked;

        var startedAt = _timeProvider.GetUtcNow();

        // 必须在首个 await 之前创建：Progress<T> 捕获构造时的上下文，
        // 之后所有进度回调都会回到 UI 线程，集合变更才是安全的。
        var progress = new Progress<DiagnosticProgress>(OnProgress);

        try
        {
            IsSelfCheckRunning = true;
            CanStartSelfCheck = false;
            HasSelfCheckResult = false;
            SelfCheckState = TaskRunState.Preparing;
            CurrentStage = "正在准备任务…";
            ElapsedText = "00:00.0";
            Conclusion = string.Empty;
            ConclusionNote = string.Empty;
            SelfCheckSteps.Clear();
            SummaryItems.Clear();

            RunSelfCheckCommand.NotifyCanExecuteChanged();
            CancelSelfCheckCommand.NotifyCanExecuteChanged();

            AppendLog(LfLogLevel.Information, $"开始“{_selfCheckAdapter.DisplayName}”（运行 ID {runId}）");

            var ticker = TickElapsedAsync(startedAt, linked.Token);

            try
            {
                // 先探测依赖：步骤的依赖跳过判定要用本次任务的实时快照，
                // 用户在自检期间装好 Npcap 应当被如实反映。
                CurrentStage = "正在检测外部依赖…";

                var dependencies = await _dependencyCatalog
                    .ProbeAllAsync(linked.Token)
                    .ConfigureAwait(false);

                // 必须等列表真正更新完再往下走：编排器要用 Dependencies 做依赖跳过判定，
                // 若这里 fire-and-forget，编排器可能读到上一次的快照（甚至空列表），
                // 表现为"明明装好了 Npcap，抓包步骤还是被跳过"。
                await PostAsync(() =>
                {
                    Dependencies.Clear();

                    foreach (var probe in dependencies)
                    {
                        Dependencies.Add(probe);
                    }

                    MissingDependencies.Clear();

                    foreach (var probe in dependencies.Where(static p => !p.IsAvailable))
                    {
                        MissingDependencies.Add(probe);
                    }

                    OnPropertyChanged(nameof(HasMissingDependencies));
                }).ConfigureAwait(false);

                if (HasMissingDependencies)
                {
                    AppendLog(
                        LfLogLevel.Warning,
                        $"检测到 {MissingDependencies.Count} 项依赖不可用，相关步骤将被跳过（不计入失败）。");
                }

                CurrentStage = "正在执行检查步骤…";

                var request = new DiagnosticRequest(runId, "本机");
                var options = new DiagnosticRunOptions(
                    DiagnosticRunOptions.Default.DefaultStepTimeout,
                    SelfCheckOverallTimeout);

                var result = await _taskRunner
                    .RunAsync(
                        _selfCheckAdapter,
                        request,
                        Dependencies,
                        options,
                        progress,
                        linked.Token)
                    .ConfigureAwait(false);

                await ApplyResultAsync(result).ConfigureAwait(false);
            }
            finally
            {
                // 先停计时器再改状态：否则最后一个 tick 可能把已结束任务的时间继续往前推。
                linked.Cancel();
                await ticker.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            SelfCheckState = TaskRunState.Cancelled;
            CurrentStage = "已取消";
            Conclusion = "自检已被取消，未产生完整结论。";
            ConclusionNote = "如需完整结论，请重新运行。";
            HasSelfCheckResult = true;

            AppendLog(LfLogLevel.Warning, "任务已被取消，未执行的步骤记为“已跳过”而非失败。");
        }
        catch (Exception ex)
        {
            SelfCheckState = TaskRunState.Failed;
            CurrentStage = "执行失败";
            Conclusion = $"自检未能完成：{ex.GetType().Name} — {ex.Message}";
            ConclusionNote = "这是程序缺陷或环境异常，请查看日志中的技术详情。";
            HasSelfCheckResult = true;

            AppendLog(LfLogLevel.Critical, $"任务执行失败：{ex.GetType().Name} — {ex.Message}");
        }
        finally
        {
            IsSelfCheckRunning = false;
            CanStartSelfCheck = true;
            _runCts = null;

            RunSelfCheckCommand.NotifyCanExecuteChanged();
            CancelSelfCheckCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 取消正在运行的自检。
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsSelfCheckRunning))]
    private void CancelSelfCheck()
    {
        // 置灰而不是隐藏，但真正点击时必须确有可取消对象；
        // 空引用时不做任何事，避免"点了取消却什么都没发生"的错觉无法解释。
        _runCts?.Cancel();
    }

    /// <summary>清空任务历史。</summary>
    [RelayCommand]
    private async Task ClearHistoryAsync(CancellationToken cancellationToken)
    {
        await _runHistoryStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        await LoadHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 定时刷新已运行时间。
    /// </summary>
    /// <remarks>
    /// 用异步循环而不是 <c>DispatcherTimer</c>：后者依赖 WPF 调度器，
    /// 在无调度器的单元测试里会静默不跑；异步循环在任何宿主下行为一致。
    /// </remarks>
    private async Task TickElapsedAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ElapsedTickInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var elapsed = _timeProvider.GetUtcNow() - startedAt;

            _ = PostAsync(() =>
            {
                ElapsedText = FormatElapsed(elapsed);
            });
        }
    }

    /// <summary>接收进度事件（已在 UI 线程）。</summary>
    private void OnProgress(DiagnosticProgress progress)
    {
        if (progress.Phase == DiagnosticStepPhase.Finished)
        {
            return;
        }

        CurrentStage = progress.Message;
        ElapsedText = FormatElapsed(progress.Elapsed);

        if (SelfCheckState is TaskRunState.Preparing or TaskRunState.Pending)
        {
            SelfCheckState = TaskRunState.Running;
        }

        AppendLog(LfLogLevel.Information, progress.Message);
    }

    /// <summary>
    /// 把任务结果落到界面与历史。
    /// </summary>
    /// <remarks>
    /// 必须异步等待历史写入完成：<c>fire-and-forget</c> 会让
    /// "任务已结束"与"历史里出现这条记录"之间产生一个窗口，
    /// 用户在此期间关闭应用会丢掉记录（表现为"刚才跑的任务不见了"），
    /// 而"最近任务"的刷新也慢一拍。
    /// </remarks>
    private async Task ApplyResultAsync(DiagnosticRunResult result)
    {
        SelfCheckSteps.Clear();

        foreach (var step in result.Steps)
        {
            SelfCheckSteps.Add(step);

            AppendLog(LogLevelFor(step.Status), step.Summary);

            if (step.HasSuggestion)
            {
                AppendLog(LfLogLevel.Debug, $"建议：{step.NextStepSuggestion}");
            }
        }

        SelfCheckState = result.State;
        HasSelfCheckResult = true;
        ElapsedText = FormatElapsed(result.Duration ?? TimeSpan.Zero);
        CurrentStage = DescribeState(result.State);
        Conclusion = BuildConclusion(result);
        ConclusionNote = BuildNote(result);

        SummaryItems.Clear();
        SummaryItems.Add(new ResultSummaryItem { Label = "目标", Value = result.TargetLabel });
        SummaryItems.Add(new ResultSummaryItem { Label = "开始时间", Value = result.StartedAtText });
        SummaryItems.Add(new ResultSummaryItem { Label = "耗时", Value = result.DurationText });
        SummaryItems.Add(new ResultSummaryItem { Label = "步骤", Value = result.StepSummaryText });

        AppendLog(LfLogLevel.Information, $"任务结束：{DescribeState(result.State)}（{result.StepSummaryText}）");

        // 历史写入失败不应让"本次结论"消失：结论已经在屏幕上，
        // 只在日志里说明原因，并保留用户可重试的路径（重新运行）。
        await PersistRunAsync(result).ConfigureAwait(false);
    }

    /// <summary>把结果写入历史并刷新列表。</summary>
    private async Task PersistRunAsync(DiagnosticRunResult result)
    {
        try
        {
            await _runHistoryStore.AppendAsync(result).ConfigureAwait(false);
            await LoadHistoryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 历史刷新被取消：不影响已展示的结论。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppendLog(LfLogLevel.Error, $"写入任务历史失败：{ex.GetType().Name} — {ex.Message}");
        }
    }

    /// <summary>追加一条已脱敏的日志。</summary>
    private void AppendLog(LfLogLevel level, string message)
    {
        var entry = new LogEntry(
            DateTimeOffset.Now,
            level,
            _selfCheckAdapter.Id,
            _currentRunId,
            null,
            message);

        // 日志丢失不影响结论，因此这里不等待；
        // 但它仍走同一个 UI 线程投递，避免在后台线程改集合。
        _ = PostAsync(() => SelfCheckLogs.Add(entry));
    }

    /// <summary>
    /// 把操作投递回 UI 线程并等待其执行完成。
    /// </summary>
    /// <remarks>
    /// <b>必须可等待</b>：调用方（依赖探测、历史加载）在投递之后还要读取这些集合，
    /// 若投递是 fire-and-forget，后续读取到的就可能是旧内容 ——
    /// "清空历史后列表仍有数据"这类竞态正是这么产生的。
    /// 无同步上下文时（单元测试宿主）同步执行，行为一致。
    /// </remarks>
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

    /// <summary>把耗时格式化为 "mm:ss.f"（超过 1 小时加小时段）。</summary>
    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss\.f", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>把任务状态转成阶段文案。</summary>
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

    /// <summary>任务层一句话结论。</summary>
    private static string BuildConclusion(DiagnosticRunResult result) => result.State switch
    {
        TaskRunState.Completed => "运行环境自检全部通过，本机具备执行诊断任务的基本条件。",
        TaskRunState.PartiallySucceeded =>
            "自检部分通过：部分步骤未通过或未执行，请查看下方步骤明细。",
        TaskRunState.Failed => "自检失败：关键检查项未通过，请查看下方步骤明细与建议。",
        TaskRunState.Cancelled => "自检已被取消，未产生完整结论。",
        TaskRunState.Inconclusive => "自检未产生确定性结论：所有步骤均被跳过或未能判定。",
        _ => "自检尚未得出最终结论。"
    };

    /// <summary>
    /// 结论补充：步骤统计 + 跳过原因汇总。
    /// </summary>
    /// <remarks>
    /// 跳过原因必须汇总到结论层：用户不会逐条展开步骤去找"为什么没做"，
    /// 而"少了哪一项没做"恰恰是判断结论可信度的关键。
    /// </remarks>
    private static string BuildNote(DiagnosticRunResult result)
    {
        var text = result.StepSummaryText;

        var skipped = result.Steps
            .Where(static s => s.Status == DiagnosticStatus.Skipped && !string.IsNullOrWhiteSpace(s.SkipReason))
            .Select(static s => s.SkipReason!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (skipped.Count > 0)
        {
            text += "；未执行原因：" + string.Join("；", skipped);
        }

        var suggestions = result.Steps
            .Where(static s => s.HasSuggestion && s.Status is DiagnosticStatus.Failed or DiagnosticStatus.TimedOut or DiagnosticStatus.Unknown)
            .Select(static s => s.NextStepSuggestion!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (suggestions.Count > 0)
        {
            text += "。建议：" + string.Join("；", suggestions);
        }

        return text;
    }

    /// <summary>步骤结论对应的日志级别。</summary>
    private static LfLogLevel LogLevelFor(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Passed => LfLogLevel.Information,
        DiagnosticStatus.Warning => LfLogLevel.Warning,
        DiagnosticStatus.Failed => LfLogLevel.Error,
        DiagnosticStatus.TimedOut => LfLogLevel.Error,
        DiagnosticStatus.Skipped => LfLogLevel.Warning,
        DiagnosticStatus.Unknown => LfLogLevel.Warning,
        DiagnosticStatus.Cancelled => LfLogLevel.Warning,
        _ => LfLogLevel.Debug
    };
}
