using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Controls;

/// <summary>
/// 任务进度面板：阶段、已运行时间、取消入口（UI 规范 §5.6、§6）。
/// </summary>
/// <remarks>
/// 依据 UI 规范"运行状态：显示阶段、耗时、停止入口"。
/// 取消入口的可取消性由 <see cref="CanCancel"/> 与命令状态共同决定：
/// 不可取消时按钮置灰而不是隐藏，避免用户以为功能缺失。
/// </remarks>
public sealed class TaskProgressPanel : Control
{
    static TaskProgressPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(TaskProgressPanel),
            new FrameworkPropertyMetadata(typeof(TaskProgressPanel)));
    }

    /// <summary>当前阶段描述，例如"正在建立 MQTT 连接…"。</summary>
    public static readonly DependencyProperty StageProperty =
        DependencyProperty.Register(
            nameof(Stage),
            typeof(string),
            typeof(TaskProgressPanel),
            new PropertyMetadata("准备中…"));

    /// <summary>任务状态；用于选择视觉语义与决定是否显示进度。</summary>
    public static readonly DependencyProperty TaskStateProperty =
        DependencyProperty.Register(
            nameof(TaskState),
            typeof(TaskRunState),
            typeof(TaskProgressPanel),
            new PropertyMetadata(TaskRunState.Pending, OnStateChanged));

    /// <summary>已运行时间文本，例如 "00:00:12.4"。</summary>
    public static readonly DependencyProperty ElapsedTextProperty =
        DependencyProperty.Register(
            nameof(ElapsedText),
            typeof(string),
            typeof(TaskProgressPanel),
            new PropertyMetadata("00:00:00"));

    /// <summary>取消命令。</summary>
    public static readonly DependencyProperty CancelCommandProperty =
        DependencyProperty.Register(
            nameof(CancelCommand),
            typeof(ICommand),
            typeof(TaskProgressPanel),
            new PropertyMetadata(null));

    /// <summary>
    /// 是否允许取消。
    /// </summary>
    /// <remarks>
    /// 默认值为 true：本控件用于"正在跑的任务"，默认就应可取消。
    /// 置灰由调用方在真正不可取消时显式设置——默认不可取消会让
    /// "长按等待"变成唯一出路，违背规范 §6 的长任务要求。
    ///
    /// 注意：仅当提供了 <see cref="CancelCommand"/> 时才有意义。
    /// 无命令时按钮由 WPF 自动禁用（Command 为 null 不触发 CanExecute）。
    /// </remarks>
    public static readonly DependencyProperty CanCancelProperty =
        DependencyProperty.Register(
            nameof(CanCancel),
            typeof(bool),
            typeof(TaskProgressPanel),
            new PropertyMetadata(true));

    /// <summary>
    /// 任务是否处于执行期（准备中或执行中）。
    /// </summary>
    /// <remarks>
    /// 命名刻意不叫 <c>IsIndeterminate</c>：那是进度条"无百分比数据"的技术属性，
    /// 而这里表达的是"任务是否仍在跑"这一业务语义。两者混用会让人误以为
    /// 存在"确定进度的运行态"，而实际上诊断任务的阶段比例无法可靠估算。
    /// </remarks>
    private static readonly DependencyPropertyKey IsActiveKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsActive),
            typeof(bool),
            typeof(TaskProgressPanel),
            new PropertyMetadata(true));

    /// <summary>任务是否处于执行期。</summary>
    public static readonly DependencyProperty IsActiveProperty = IsActiveKey.DependencyProperty;

    /// <summary>当前阶段描述。</summary>
    public string Stage
    {
        get => (string)GetValue(StageProperty);
        set => SetValue(StageProperty, value);
    }

    /// <summary>任务状态。</summary>
    public TaskRunState TaskState
    {
        get => (TaskRunState)GetValue(TaskStateProperty);
        set => SetValue(TaskStateProperty, value);
    }

    /// <summary>已运行时间文本。</summary>
    public string ElapsedText
    {
        get => (string)GetValue(ElapsedTextProperty);
        set => SetValue(ElapsedTextProperty, value);
    }

    /// <summary>取消命令。</summary>
    public ICommand? CancelCommand
    {
        get => (ICommand?)GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    /// <summary>是否允许取消。</summary>
    public bool CanCancel
    {
        get => (bool)GetValue(CanCancelProperty);
        set => SetValue(CanCancelProperty, value);
    }

    /// <summary>任务是否处于执行期。</summary>
    public bool IsActive => (bool)GetValue(IsActiveProperty);

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (TaskProgressPanel)d;
        var state = (TaskRunState)e.NewValue;

        // 只有执行期才显示进度条；到达终态后进度条会误导用户以为仍在运行。
        panel.SetValue(IsActiveKey, state is TaskRunState.Preparing or TaskRunState.Running);

        // 到达终态后没有可取消的对象了：此时"停止"按钮必须置灰。
        // 反过来，只要回到执行期就恢复可取消，避免状态来回切换后按钮一直灰着。
        panel.CanCancel = state is TaskRunState.Preparing or TaskRunState.Running;
    }
}
