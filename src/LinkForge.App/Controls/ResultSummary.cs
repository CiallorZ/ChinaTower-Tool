using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Controls;

/// <summary>
/// 结果摘要中的一行事实。
/// </summary>
public sealed class ResultSummaryItem
{
    /// <summary>项目名称。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>项目取值（已脱敏的文本）。</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// 该项结论；为 null 表示这是纯事实项（如"目标地址"），没有结论可言。
    /// </summary>
    /// <remarks>
    /// 刻意用可空类型而不是给个默认值：纯事实项不应显示状态标签，
    /// 否则会渲染成"待运行/通过"等与事实无关的结论，属于编造状态。
    /// </remarks>
    public DiagnosticStatus? Status { get; init; }

    /// <summary>该项是否带有结论（决定模板是否渲染状态标签）。</summary>
    public bool HasStatus => Status.HasValue;
}

/// <summary>
/// 结论摘要：先给结论，再给细节（UI 规范 §4.2 第 6 项"结论先于原始日志"）。
/// </summary>
public sealed class ResultSummary : Control
{
    static ResultSummary()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ResultSummary),
            new FrameworkPropertyMetadata(typeof(ResultSummary)));
    }

    /// <summary>任务层结论。</summary>
    public static readonly DependencyProperty TaskStateProperty =
        DependencyProperty.Register(
            nameof(TaskState),
            typeof(TaskRunState),
            typeof(ResultSummary),
            new PropertyMetadata(TaskRunState.Pending));

    /// <summary>一句话结论（面向用户，不含内部术语）。</summary>
    public static readonly DependencyProperty ConclusionProperty =
        DependencyProperty.Register(
            nameof(Conclusion),
            typeof(string),
            typeof(ResultSummary),
            new PropertyMetadata(string.Empty));

    /// <summary>补充说明，例如"3 项未执行，原因是缺少依赖"。</summary>
    public static readonly DependencyProperty NoteProperty =
        DependencyProperty.Register(
            nameof(Note),
            typeof(string),
            typeof(ResultSummary),
            new PropertyMetadata(null));

    /// <summary>结构化事实列表。</summary>
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(ObservableCollection<ResultSummaryItem>),
            typeof(ResultSummary),
            new PropertyMetadata(null));

    /// <summary>任务层结论。</summary>
    public TaskRunState TaskState
    {
        get => (TaskRunState)GetValue(TaskStateProperty);
        set => SetValue(TaskStateProperty, value);
    }

    /// <summary>一句话结论。</summary>
    public string Conclusion
    {
        get => (string)GetValue(ConclusionProperty);
        set => SetValue(ConclusionProperty, value);
    }

    /// <summary>补充说明。</summary>
    public string? Note
    {
        get => (string?)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    /// <summary>结构化事实列表。</summary>
    public ObservableCollection<ResultSummaryItem>? Items
    {
        get => (ObservableCollection<ResultSummaryItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }
}
