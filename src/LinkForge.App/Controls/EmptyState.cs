using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LinkForge.App.Controls;

/// <summary>
/// 初始空状态：说明"这里是什么、如何开始"，并提供主操作（UI 规范 §5.6）。
/// </summary>
/// <remarks>
/// 空状态不是错误状态：不使用危险色，也不显示"失败"字样。
/// 未提供主操作时，控件会隐藏操作区而不是显示一个不可用的按钮。
/// </remarks>
public sealed class EmptyState : Control
{
    static EmptyState()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(EmptyState),
            new FrameworkPropertyMetadata(typeof(EmptyState)));
    }

    /// <summary>标题，例如"尚未选择项目"。</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(EmptyState),
            new PropertyMetadata("暂无内容"));

    /// <summary>说明文字：解释此处是什么、如何开始。</summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(EmptyState),
            new PropertyMetadata(string.Empty));

    /// <summary>主操作文案；留空则不显示操作按钮。</summary>
    public static readonly DependencyProperty ActionTextProperty =
        DependencyProperty.Register(
            nameof(ActionText),
            typeof(string),
            typeof(EmptyState),
            new PropertyMetadata(null, OnActionAvailabilityChanged));

    /// <summary>主操作命令。</summary>
    public static readonly DependencyProperty ActionCommandProperty =
        DependencyProperty.Register(
            nameof(ActionCommand),
            typeof(ICommand),
            typeof(EmptyState),
            new PropertyMetadata(null, OnActionAvailabilityChanged));

    /// <summary>主操作命令参数。</summary>
    public static readonly DependencyProperty ActionCommandParameterProperty =
        DependencyProperty.Register(
            nameof(ActionCommandParameter),
            typeof(object),
            typeof(EmptyState),
            new PropertyMetadata(null));

    private static readonly DependencyPropertyKey HasActionKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasAction),
            typeof(bool),
            typeof(EmptyState),
            new PropertyMetadata(false));

    /// <summary>是否存在可用主操作（供模板控制显示）。</summary>
    public static readonly DependencyProperty HasActionProperty = HasActionKey.DependencyProperty;

    /// <summary>标题。</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>说明文字。</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>主操作文案。</summary>
    public string? ActionText
    {
        get => (string?)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    /// <summary>主操作命令。</summary>
    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    /// <summary>主操作命令参数。</summary>
    public object? ActionCommandParameter
    {
        get => GetValue(ActionCommandParameterProperty);
        set => SetValue(ActionCommandParameterProperty, value);
    }

    /// <summary>是否存在可用主操作。</summary>
    public bool HasAction => (bool)GetValue(HasActionProperty);

    private static void OnActionAvailabilityChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e) =>
        ((EmptyState)d).RefreshActionAvailability();

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        RefreshActionAvailability();
    }

    private void RefreshActionAvailability() =>
        SetValue(
            HasActionKey,
            !string.IsNullOrWhiteSpace(ActionText) && ActionCommand is not null);
}
