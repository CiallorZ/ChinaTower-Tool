using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace LinkForge.App.Controls;

/// <summary>
/// 带可见标签、必填标记、单位/格式说明与就近错误提示的输入控件（UI 规范 §5.2）。
/// </summary>
/// <remarks>
/// 规范原文："错误提示贴近字段，并提供修正方向；不要只在页面顶部弹一句'参数错误'。"
/// 因此本控件把标签、错误提示与输入框放在同一个模板内，
/// 而不是让页面自行拼装 TextBlock + TextBlock + TextBox（那样容易漏掉某个部分）。
///
/// 刻意不继承 <see cref="Wpf.Ui.Controls.TextBox"/>：继承会把控件库的
/// Placeholder/ClearButton 等属性一并暴露，而它们与本控件的标签职责重叠，
/// 容易让页面同时出现"标签 + 占位符"两处字段名称。这里改为包装，
/// 只暴露本控件真正需要的属性。
/// </remarks>
public sealed class LabeledTextBox : Control
{
    static LabeledTextBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(LabeledTextBox),
            new FrameworkPropertyMetadata(typeof(LabeledTextBox)));
    }

    /// <summary>字段标签（必须可见，不得只靠占位符）。</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(LabeledTextBox),
            new PropertyMetadata(string.Empty));

    /// <summary>是否必填；为真时在标签后显示必填标记。</summary>
    public static readonly DependencyProperty IsRequiredProperty =
        DependencyProperty.Register(
            nameof(IsRequired),
            typeof(bool),
            typeof(LabeledTextBox),
            new PropertyMetadata(false));

    /// <summary>输入值。</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(LabeledTextBox),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>
    /// 占位符：仅用于示例，不用于说明完整规则（规范 §5.2）。
    /// </summary>
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(
            nameof(Placeholder),
            typeof(string),
            typeof(LabeledTextBox),
            new PropertyMetadata(string.Empty));

    /// <summary>单位或默认值说明，例如 "ms"、"默认 554"。</summary>
    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(
            nameof(Hint),
            typeof(string),
            typeof(LabeledTextBox),
            new PropertyMetadata(string.Empty, OnOptionalTextChanged));

    /// <summary>字段级错误提示；为空表示无错误。</summary>
    public static readonly DependencyProperty ErrorTextProperty =
        DependencyProperty.Register(
            nameof(ErrorText),
            typeof(string),
            typeof(LabeledTextBox),
            new PropertyMetadata(null, OnOptionalTextChanged));

    private static readonly DependencyPropertyKey HasHintKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasHint),
            typeof(bool),
            typeof(LabeledTextBox),
            new PropertyMetadata(false));

    /// <summary>是否存在单位/默认值说明。</summary>
    public static readonly DependencyProperty HasHintProperty = HasHintKey.DependencyProperty;

    private static readonly DependencyPropertyKey HasErrorKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasError),
            typeof(bool),
            typeof(LabeledTextBox),
            new PropertyMetadata(false));

    /// <summary>当前是否处于错误态（决定边框颜色与提示可见性）。</summary>
    public static readonly DependencyProperty HasErrorProperty = HasErrorKey.DependencyProperty;

    /// <summary>字段标签。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>是否必填。</summary>
    public bool IsRequired
    {
        get => (bool)GetValue(IsRequiredProperty);
        set => SetValue(IsRequiredProperty, value);
    }

    /// <summary>输入值。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>占位符示例。</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>单位或默认值说明。</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>字段级错误提示。</summary>
    public string? ErrorText
    {
        get => (string?)GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    /// <summary>是否存在单位/默认值说明。</summary>
    public bool HasHint => (bool)GetValue(HasHintProperty);

    /// <summary>当前是否处于错误态。</summary>
    public bool HasError => (bool)GetValue(HasErrorProperty);

    private static void OnOptionalTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (LabeledTextBox)d;

        control.SetValue(HasHintKey, !string.IsNullOrWhiteSpace(control.Hint));
        control.SetValue(HasErrorKey, !string.IsNullOrWhiteSpace(control.ErrorText));
    }
}
