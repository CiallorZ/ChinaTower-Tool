using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.DeveloperTools;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.ViewModels;

/// <summary>
/// 通用开发者工具页（LF-011）。
/// </summary>
/// <remarks>
/// 所有转换都是本地离线执行。输入、输出和 JWT 只驻留当前会话，
/// 不写日志、历史或报告；大输入通过线程池执行并可取消。
/// </remarks>
public sealed partial class DeveloperToolsViewModel : ObservableObject
{
    private readonly IDeveloperTransformExecutor _executor;
    private readonly IDeveloperResultClipboard _clipboard;
    private readonly SynchronizationContext? _uiContext;
    private CancellationTokenSource? _runCts;
    private DeveloperToolResult? _lastResult;

    /// <summary>创建开发者工具视图模型。</summary>
    public DeveloperToolsViewModel(
        IDeveloperTransformExecutor executor,
        IDeveloperResultClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(clipboard);

        _executor = executor;
        _clipboard = clipboard;
        _uiContext = SynchronizationContext.Current;
        Tools = [.. DeveloperToolDescriptor.All];
        SelectedTool = Tools[0];
        State = TaskRunState.Pending;
    }

    /// <summary>可用离线转换工具。</summary>
    public ObservableCollection<DeveloperToolDescriptor> Tools { get; }

    /// <summary>当前工具。</summary>
    [ObservableProperty]
    public partial DeveloperToolDescriptor SelectedTool { get; set; }

    /// <summary>输入文本。</summary>
    [ObservableProperty]
    public partial string Input { get; set; } = string.Empty;

    /// <summary>输出文本。</summary>
    [ObservableProperty]
    public partial string Output { get; private set; } = string.Empty;

    /// <summary>字段级错误。</summary>
    [ObservableProperty]
    public partial string? InputError { get; private set; }

    /// <summary>是否正在执行。</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>任务状态；取消与失败保持独立语义。</summary>
    [ObservableProperty]
    public partial TaskRunState State { get; private set; }

    /// <summary>当前阶段。</summary>
    [ObservableProperty]
    public partial string CurrentStage { get; private set; } = "尚未执行";

    /// <summary>耗时文本。</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = "00:00.0";

    /// <summary>一句话结论。</summary>
    [ObservableProperty]
    public partial string Conclusion { get; private set; } = string.Empty;

    /// <summary>结论说明。</summary>
    [ObservableProperty]
    public partial string ConclusionNote { get; private set; } = string.Empty;

    /// <summary>是否存在有效结果。</summary>
    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    /// <summary>结果是否为警告（部分成功语义）。</summary>
    [ObservableProperty]
    public partial bool IsWarning { get; private set; }

    /// <summary>是否有可复制结果。</summary>
    public bool CanCopyResult => !string.IsNullOrEmpty(Output);

    /// <summary>是否应显示空状态。</summary>
    public bool IsEmpty => !HasResult && !IsRunning;

    /// <summary>是否可执行。</summary>
    public bool CanExecute => !IsRunning;

    /// <summary>执行当前工具。</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var tool = SelectedTool;
        InputError = null;

        if (tool.RequiresInput && string.IsNullOrEmpty(Input))
        {
            InputError = "请输入内容。";
            return;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(Input) > DeveloperTransforms.MaxInputBytes)
        {
            InputError = $"输入超过 {DeveloperTransforms.MaxInputBytes / 1024} KiB 上限，请截取后再处理。";
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runCts = linked;
        var startedAt = DateTimeOffset.UtcNow;
        var ticker = TickAsync(startedAt, linked.Token);

        try
        {
            await PostAsync(() =>
            {
                IsRunning = true;
                State = TaskRunState.Running;
                CurrentStage = $"正在执行 {tool.Name}…";
                ElapsedText = "00:00.0";
                Conclusion = string.Empty;
                ConclusionNote = string.Empty;
                Output = string.Empty;
                HasResult = false;
                IsWarning = false;
                NotifyCommands();
            }).ConfigureAwait(false);

            DeveloperToolResult result;

            try
            {
                result = await _executor
                    .ExecuteAsync(tool.Kind, Input, linked.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                linked.Cancel();
                await ticker.ConfigureAwait(false);
            }

            var elapsed = DateTimeOffset.UtcNow - startedAt;

            await PostAsync(() =>
            {
                _lastResult = result;
                Output = result.Output;
                Conclusion = result.Succeeded
                    ? tool.Name + "完成：" + result.Message
                    : tool.Name + "失败：" + result.Message;
                ConclusionNote = result.Succeeded
                    ? result.IsWarning
                        ? "结果包含警告，请查看输出与限制说明。"
                        : "结果已成功生成；本页不写入日志、历史或报告。"
                    : "请修正输入后重新执行。";
                State = result.Succeeded
                    ? result.IsWarning
                        ? TaskRunState.PartiallySucceeded
                        : TaskRunState.Completed
                    : TaskRunState.Failed;
                IsWarning = result.IsWarning;
                HasResult = true;
                CurrentStage = result.Succeeded ? "已完成" : "执行失败";
                ElapsedText = DashboardViewModel.FormatElapsed(elapsed);
                NotifyCommands();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            linked.Cancel();
            await ticker.ConfigureAwait(false);
            await PostAsync(() =>
            {
                State = TaskRunState.Cancelled;
                CurrentStage = "已取消";
                Conclusion = tool.Name + "已取消，未产生结果。";
                ConclusionNote = "取消不是失败；如需结果请重新执行。";
                HasResult = true;
                IsWarning = false;
                NotifyCommands();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            linked.Cancel();
            await ticker.ConfigureAwait(false);
            await PostAsync(() =>
            {
                State = TaskRunState.Failed;
                CurrentStage = "执行失败";
                Conclusion = $"{tool.Name}未能完成：{ex.GetType().Name}";
                ConclusionNote = "转换异常已被边界捕获，未写入日志或历史。";
                HasResult = true;
                IsWarning = false;
                NotifyCommands();
            }).ConfigureAwait(false);
        }
        finally
        {
            _runCts = null;
            await PostAsync(() =>
            {
                IsRunning = false;
                NotifyCommands();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>取消当前转换。</summary>
    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>复制当前结果；这是用户明确动作，不产生额外日志。</summary>
    [RelayCommand(CanExecute = nameof(CanCopyResult))]
    private void CopyResult()
    {
        try
        {
            _clipboard.Copy(Output);
            ConclusionNote = "已复制到剪贴板；请避免把敏感结果粘贴到共享日志。";
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            ConclusionNote = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>清空输入和结果，回到真实空状态。</summary>
    [RelayCommand]
    private void Clear()
    {
        Input = string.Empty;
        Output = string.Empty;
        InputError = null;
        _lastResult = null;
        HasResult = false;
        IsWarning = false;
        State = TaskRunState.Pending;
        CurrentStage = "尚未执行";
        ElapsedText = "00:00.0";
        Conclusion = string.Empty;
        ConclusionNote = string.Empty;
        NotifyCommands();
    }

    partial void OnInputChanged(string value)
    {
        InputError = null;
    }

    partial void OnSelectedToolChanged(DeveloperToolDescriptor value)
    {
        InputError = null;
    }

    partial void OnOutputChanged(string value) => CopyResultCommand.NotifyCanExecuteChanged();

    partial void OnHasResultChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanCopyResult));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanExecute));
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private async Task TickAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var elapsed = DateTimeOffset.UtcNow - startedAt;
            await PostAsync(() => ElapsedText =
                DashboardViewModel.FormatElapsed(elapsed)).ConfigureAwait(false);
        }
    }

    private void NotifyCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        CopyResultCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanCopyResult));
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

    /// <summary>最近一次结果，仅用于测试与页面状态检查。</summary>
    internal DeveloperToolResult? LastResultForTests => _lastResult;
}
