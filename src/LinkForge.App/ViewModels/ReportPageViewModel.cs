using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.App.Services;
using LinkForge.Application.Diagnostics;
using LinkForge.Domain.Diagnostics;
using LinkForge.Reporting.Reports;

namespace LinkForge.App.ViewModels;

/// <summary>
/// "项目与报告"页面视图模型（LF-010）。
/// </summary>
/// <remarks>
/// <para>
/// 页面只有一个真实数据源：<see cref="IRunHistoryStore"/> 中的任务历史。
/// 没有历史就显示空状态并说明"先运行一次诊断"，而不是给出一份示例报告 ——
/// 示例报告会被当成真实证据转发出去。
/// </para>
/// <para>
/// 生成流程是<b>预览 → 扫描 → 保存</b>：用户先看到扫描结论，再决定保存位置。
/// 把保存放在最后一步，可以避免"生成了才发现不该导出"。
/// </para>
/// <para>
/// 所有 IO 异步且可取消；扫描命中时不写任何文件。
/// </para>
/// </remarks>
public sealed partial class ReportPageViewModel : ObservableObject
{
    /// <summary>可选历史记录的最大条数。</summary>
    public const int HistoryLimit = 50;

    private readonly IRunHistoryStore _historyStore;
    private readonly IReportGenerator _generator;
    private readonly IReportFileExporter _exporter;
    private readonly AppEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _uiContext;

    private string? _previewHtml;
    private ReportDocument? _previewDocument;

    /// <summary>创建报告页视图模型。</summary>
    public ReportPageViewModel(
        IRunHistoryStore historyStore,
        IReportGenerator generator,
        IReportFileExporter exporter,
        AppEnvironment environment,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(environment);

        _historyStore = historyStore;
        _generator = generator;
        _exporter = exporter;
        _environment = environment;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _uiContext = SynchronizationContext.Current;

        Runs = [];
        Steps = [];
        EnvironmentalFacts = [];

        IsLoading = true;
    }

    /// <summary>可生成报告的历史任务（倒序）。</summary>
    public ObservableCollection<DiagnosticRunResult> Runs { get; }

    /// <summary>当前选中任务的步骤（用于预览摘要）。</summary>
    public ObservableCollection<DiagnosticStepResult> Steps { get; }

    /// <summary>环境事实（写入报告）。</summary>
    public ObservableCollection<DiagnosticFact> EnvironmentalFacts { get; }

    /// <summary>是否正在读取历史。</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>是否正在生成报告（预览或保存）。</summary>
    [ObservableProperty]
    public partial bool IsGenerating { get; private set; }

    /// <summary>读取历史失败时的说明。</summary>
    [ObservableProperty]
    public partial string? LoadErrorMessage { get; private set; }

    /// <summary>操作反馈（成功路径、失败原因、取消）。</summary>
    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    /// <summary>操作反馈是否为错误（决定呈现样式）。</summary>
    [ObservableProperty]
    public partial bool HasActionError { get; private set; }

    /// <summary>当前选中任务；为空时不生成。</summary>
    [ObservableProperty]
    public partial DiagnosticRunResult? SelectedRun { get; set; }

    /// <summary>项目名称（写入报告；可留空）。</summary>
    [ObservableProperty]
    public partial string ProjectName { get; set; } = string.Empty;

    /// <summary>最近一次生成的报告路径。</summary>
    [ObservableProperty]
    public partial string? LastGeneratedPath { get; private set; }

    /// <summary>预览是否就绪（已执行扫描）。</summary>
    [ObservableProperty]
    public partial bool IsPreviewReady { get; private set; }

    /// <summary>预览扫描结论文本。</summary>
    [ObservableProperty]
    public partial string PreviewScanText { get; private set; } = string.Empty;

    /// <summary>预览扫描是否通过。</summary>
    [ObservableProperty]
    public partial bool IsPreviewClean { get; private set; }

    /// <summary>是否有可生成的历史任务。</summary>
    public bool HasRuns => Runs.Count > 0;

    /// <summary>
    /// 是否应显示空状态（无历史且不在加载中）。
    /// </summary>
    /// <remarks>
    /// 由视图模型给出而不是在 XAML 里用多重触发器表达：
    /// 多重触发器在"加载完成"这一瞬间的重算时机依赖属性通知顺序，
    /// 曾出现过"数据已空但占位不出现"的实际问题。一个显式布尔属性只有一处判断。
    /// </remarks>
    public bool IsEmpty => !HasRuns && !IsLoading;

    /// <summary>当前选中任务是否有步骤结论。</summary>
    public bool HasSteps => Steps.Count > 0;

    /// <summary>空状态文案（说明为什么为空、下一步做什么）。</summary>
    public string EmptyDescription =>
        "尚无任务记录可供生成报告。请先到“网络诊断”“API 工作台”或“视频接入”运行一次诊断，"
        + "结果会自动出现在这里。";

    /// <summary>是否可保存（预览通过且不在生成中）。</summary>
    public bool CanSave => IsPreviewReady && IsPreviewClean && !IsGenerating;

    /// <summary>是否可预览（有选中任务且不在生成中）。</summary>
    public bool CanPreview => SelectedRun is not null && !IsGenerating;

    /// <summary>是否有可定位的报告文件。</summary>
    public bool HasLastGeneratedPath => !string.IsNullOrWhiteSpace(LastGeneratedPath);

    /// <summary>预览 HTML（供调试与后续嵌入预览面板使用）。</summary>
    public string? PreviewHtml => _previewHtml;

    /// <summary>读取历史任务。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 所有属性变更都必须回到 UI 线程：绑定不会为后台线程的
        // PropertyChanged 重新求值（实际表现就是"数据已就绪但占位不消失"）。
        await PostAsync(() =>
        {
            IsLoading = true;
            LoadErrorMessage = null;
        }).ConfigureAwait(false);

        try
        {
            var runs = await _historyStore
                .LoadAsync(HistoryLimit, cancellationToken)
                .ConfigureAwait(false);

            await PostAsync(() => ApplyRuns(runs)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消不写错误：保留上一次的真实列表。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var message = $"读取任务历史失败：{ex.GetType().Name} — {ex.Message}";
            await PostAsync(() => LoadErrorMessage = message).ConfigureAwait(false);
        }
        finally
        {
            await PostAsync(() =>
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
                NotifyCommands();
            }).ConfigureAwait(false);
        }
    }

    private void ApplyRuns(IReadOnlyList<DiagnosticRunResult> runs)
    {
        Runs.Clear();

        foreach (var run in runs)
        {
            Runs.Add(run);
        }

        SelectedRun ??= Runs.FirstOrDefault();
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// 预览报告：构建文档、执行扫描，并把步骤与环境事实同步到界面。
    /// </summary>
    /// <remarks>
    /// 预览本身不写文件，因此可以无成本地反复执行；
    /// 这也让"扫描不通过"能在保存<b>之前</b>就被用户看到。
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync(CancellationToken cancellationToken)
    {
        if (SelectedRun is not { } run)
        {
            return;
        }

        await PostAsync(() =>
        {
            IsGenerating = true;
            HasActionError = false;
            ActionFeedback = string.Empty;
        }).ConfigureAwait(false);

        try
        {
            var document = BuildDocument(run);
            var scan = await Task.Run(() => _generator.Scan(document), cancellationToken)
                .ConfigureAwait(false);
            var html = await Task.Run(() => ReportHtmlRenderer.Render(document), cancellationToken)
                .ConfigureAwait(false);

            _previewDocument = document;
            _previewHtml = html;

            await PostAsync(() =>
            {
                IsPreviewClean = scan.IsClean;
                PreviewScanText = scan.IsClean
                    ? "敏感信息扫描通过：报告可安全保存。"
                    : $"{scan.Describe()}。请先处理后再保存。";
                IsPreviewReady = true;
                ActionFeedback = scan.IsClean
                    ? "预览已生成，可选择保存位置导出 HTML。"
                    : "预览已生成，但敏感信息扫描未通过，保存已禁用。";
                HasActionError = !scan.IsClean;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() => ActionFeedback = "已取消预览。").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = $"生成预览失败：{ex.GetType().Name} — {ex.Message}";
            await PostAsync(() =>
            {
                HasActionError = true;
                ActionFeedback = message;
            }).ConfigureAwait(false);
        }
        finally
        {
            await PostAsync(() =>
            {
                IsGenerating = false;
                NotifyCommands();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>保存报告。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_previewDocument is not { } document)
        {
            return;
        }

        var suggestedName = _generator is ReportGenerator concrete
            ? concrete.SuggestFileName(document.Run.AdapterId)
            : "linkforge-report.html";
        var destination = _exporter.ChooseDestination(suggestedName);

        if (destination is null)
        {
            await PostAsync(() =>
            {
                ActionFeedback = "已取消保存。";
                HasActionError = false;
            }).ConfigureAwait(false);
            return;
        }

        await PostAsync(() => IsGenerating = true).ConfigureAwait(false);

        try
        {
            var result = await _generator
                .GenerateAsync(document, destination, cancellationToken)
                .ConfigureAwait(false);

            await PostAsync(() =>
            {
                HasActionError = !result.Succeeded;
                ActionFeedback = result.Message;
                LastGeneratedPath = result.Succeeded ? result.FilePath : null;

                if (result.FailureKind == ReportFailureKind.SensitiveDataDetected)
                {
                    // 保存阶段才发现问题说明预览后数据被改动过，必须让用户重新预览。
                    IsPreviewClean = false;
                    PreviewScanText = result.Scan.Describe();
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await PostAsync(() =>
            {
                ActionFeedback = "已取消保存。";
                HasActionError = false;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = $"保存报告失败：{ex.GetType().Name} — {ex.Message}";
            await PostAsync(() =>
            {
                HasActionError = true;
                ActionFeedback = message;
            }).ConfigureAwait(false);
        }
        finally
        {
            await PostAsync(() =>
            {
                IsGenerating = false;
                NotifyCommands();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>在资源管理器中定位刚生成的报告。</summary>
    [RelayCommand(CanExecute = nameof(HasLastGeneratedPath))]
    private void RevealReport()
    {
        if (LastGeneratedPath is not { Length: > 0 } path)
        {
            return;
        }

        try
        {
            _exporter.RevealInExplorer(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or FileNotFoundException)
        {
            HasActionError = true;
            ActionFeedback = $"无法打开文件位置：{ex.Message}";
        }
    }

    /// <summary>构建报告文档（只用真实数据）。</summary>
    private ReportDocument BuildDocument(DiagnosticRunResult run) =>
        new()
        {
            Metadata = new ReportMetadata
            {
                ProductVersion = _environment.ApplicationVersion,
                ProjectName = string.IsNullOrWhiteSpace(ProjectName) ? null : ProjectName.Trim(),
                GeneratedAt = _timeProvider.GetLocalNow(),
                EnvironmentFacts =
                [
                    new DiagnosticFact("运行时", _environment.RuntimeDescription),
                    new DiagnosticFact("操作系统", _environment.WindowsVersionName),
                    new DiagnosticFact("进程架构", _environment.ProcessArchitecture),
                    new DiagnosticFact("配置目录", _environment.SettingsDirectory ?? "未知")
                ]
            },
            Run = run
        };

    partial void OnSelectedRunChanged(DiagnosticRunResult? value)
    {
        // 切换任务后旧预览立即失效：否则用户会保存上一份任务的报告，
        // 而界面上显示的是新任务 —— 这类"张冠李戴"的报告最难被发现。
        _previewDocument = null;
        _previewHtml = null;
        IsPreviewReady = false;
        IsPreviewClean = false;
        PreviewScanText = string.Empty;
        LastGeneratedPath = null;

        Steps.Clear();

        if (value is not null)
        {
            foreach (var step in value.Steps)
            {
                Steps.Add(step);
            }
        }

        OnPropertyChanged(nameof(HasSteps));
        NotifyCommands();
    }

    partial void OnProjectNameChanged(string value)
    {
        if (!IsPreviewReady)
        {
            return;
        }

        IsPreviewReady = false;
        IsPreviewClean = false;
        PreviewScanText = "内容已变化，请重新预览。";
        ActionFeedback = "项目名称已修改，请重新预览后再保存。";
        HasActionError = false;
        NotifyCommands();
    }

    partial void OnIsGeneratingChanged(bool value) => NotifyCommands();

    partial void OnLastGeneratedPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLastGeneratedPath));
        RevealReportCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCommands()
    {
        PreviewCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        RevealReportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanSave));
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
}
