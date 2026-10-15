using System.Windows;
using System.Windows.Controls;

namespace LinkForge.App.Controls;

/// <summary>
/// 加载/准备状态：说明"正在做什么"（UI 规范 §5.6）。
/// </summary>
/// <remarks>
/// 规范明确禁止"无限旋转且没有文字"，因此 <see cref="Message"/> 默认非空，
/// 且模板始终显示文字；不确定进度时使用不确定进度条而不是空转图标。
/// </remarks>
public sealed class LoadingState : Control
{
    static LoadingState()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(LoadingState),
            new FrameworkPropertyMetadata(typeof(LoadingState)));
    }

    /// <summary>正在做什么，例如"正在解析目标地址…"。</summary>
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(
            nameof(Message),
            typeof(string),
            typeof(LoadingState),
            new PropertyMetadata("正在准备…"));

    /// <summary>补充说明（可选），例如预计耗时或已完成的准备步骤。</summary>
    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(
            nameof(Detail),
            typeof(string),
            typeof(LoadingState),
            new PropertyMetadata(null));

    /// <summary>正在做什么。</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>补充说明。</summary>
    public string? Detail
    {
        get => (string?)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }
}
