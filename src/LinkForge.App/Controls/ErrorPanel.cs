using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LinkForge.App.Controls;

/// <summary>
/// 错误状态面板：解释原因、可恢复性，并提供重试或查看详情入口（UI 规范 §5.6）。
/// </summary>
/// <remarks>
/// 与 <see cref="DependencyMissingPanel"/> 的区别：
/// 本控件表达"执行失败"，依赖缺失必须用另一个控件，不能伪装成普通失败。
/// </remarks>
public sealed class ErrorPanel : Control
{
    static ErrorPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ErrorPanel),
            new FrameworkPropertyMetadata(typeof(ErrorPanel)));
    }

    /// <summary>错误摘要（面向用户的一句话原因）。</summary>
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(
            nameof(Message),
            typeof(string),
            typeof(ErrorPanel),
            new PropertyMetadata("操作失败"));

    /// <summary>技术详情（可折叠展示，例如异常类型与关键堆栈行）。</summary>
    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(
            nameof(Detail),
            typeof(string),
            typeof(ErrorPanel),
            new PropertyMetadata(null, OnDetailChanged));

    /// <summary>是否为可恢复错误（决定是否提示"可重试"）。</summary>
    public static readonly DependencyProperty IsRecoverableProperty =
        DependencyProperty.Register(
            nameof(IsRecoverable),
            typeof(bool),
            typeof(ErrorPanel),
            new PropertyMetadata(true));

    /// <summary>重试命令；留空则不显示重试按钮。</summary>
    public static readonly DependencyProperty RetryCommandProperty =
        DependencyProperty.Register(
            nameof(RetryCommand),
            typeof(ICommand),
            typeof(ErrorPanel),
            new PropertyMetadata(null));

    /// <summary>重试命令参数。</summary>
    public static readonly DependencyProperty RetryCommandParameterProperty =
        DependencyProperty.Register(
            nameof(RetryCommandParameter),
            typeof(object),
            typeof(ErrorPanel),
            new PropertyMetadata(null));

    private static readonly DependencyPropertyKey HasDetailKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasDetail),
            typeof(bool),
            typeof(ErrorPanel),
            new PropertyMetadata(false));

    /// <summary>是否存在技术详情。</summary>
    public static readonly DependencyProperty HasDetailProperty = HasDetailKey.DependencyProperty;

    /// <summary>错误摘要。</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>技术详情。</summary>
    public string? Detail
    {
        get => (string?)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>是否可恢复。</summary>
    public bool IsRecoverable
    {
        get => (bool)GetValue(IsRecoverableProperty);
        set => SetValue(IsRecoverableProperty, value);
    }

    /// <summary>重试命令。</summary>
    public ICommand? RetryCommand
    {
        get => (ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    /// <summary>重试命令参数。</summary>
    public object? RetryCommandParameter
    {
        get => GetValue(RetryCommandParameterProperty);
        set => SetValue(RetryCommandParameterProperty, value);
    }

    /// <summary>是否存在技术详情。</summary>
    public bool HasDetail => (bool)GetValue(HasDetailProperty);

    private static void OnDetailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ErrorPanel)d).SetValue(HasDetailKey, !string.IsNullOrWhiteSpace(e.NewValue as string));
}
