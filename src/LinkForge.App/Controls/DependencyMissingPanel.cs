using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LinkForge.App.Controls;

/// <summary>
/// 依赖缺失 / 当前环境不可用状态（UI 规范 §5.6）。
/// </summary>
/// <remarks>
/// 规范明确要求"不能伪装成普通失败"：因此本控件使用独立的
/// <c>LfBrushDependencyMissing*</c> 令牌，文案聚焦"缺什么、如何配置"，
/// 而不是"失败"。
/// </remarks>
public sealed class DependencyMissingPanel : Control
{
    static DependencyMissingPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DependencyMissingPanel),
            new FrameworkPropertyMetadata(typeof(DependencyMissingPanel)));
    }

    /// <summary>缺失的组件名称，例如 "Npcap"、"海康 HCNetSDK"。</summary>
    public static readonly DependencyProperty DependencyNameProperty =
        DependencyProperty.Register(
            nameof(DependencyName),
            typeof(string),
            typeof(DependencyMissingPanel),
            new PropertyMetadata("所需组件"));

    /// <summary>影响说明：缺了它会导致什么功能不可用。</summary>
    public static readonly DependencyProperty ImpactProperty =
        DependencyProperty.Register(
            nameof(Impact),
            typeof(string),
            typeof(DependencyMissingPanel),
            new PropertyMetadata(string.Empty));

    /// <summary>如何配置/获取依赖的说明。</summary>
    public static readonly DependencyProperty GuidanceProperty =
        DependencyProperty.Register(
            nameof(Guidance),
            typeof(string),
            typeof(DependencyMissingPanel),
            new PropertyMetadata(string.Empty));

    /// <summary>打开配置/帮助的命令（可选）。</summary>
    public static readonly DependencyProperty ConfigureCommandProperty =
        DependencyProperty.Register(
            nameof(ConfigureCommand),
            typeof(ICommand),
            typeof(DependencyMissingPanel),
            new PropertyMetadata(null));

    /// <summary>缺失的组件名称。</summary>
    public string DependencyName
    {
        get => (string)GetValue(DependencyNameProperty);
        set => SetValue(DependencyNameProperty, value);
    }

    /// <summary>影响说明。</summary>
    public string Impact
    {
        get => (string)GetValue(ImpactProperty);
        set => SetValue(ImpactProperty, value);
    }

    /// <summary>配置指引。</summary>
    public string Guidance
    {
        get => (string)GetValue(GuidanceProperty);
        set => SetValue(GuidanceProperty, value);
    }

    /// <summary>打开配置/帮助命令。</summary>
    public ICommand? ConfigureCommand
    {
        get => (ICommand?)GetValue(ConfigureCommandProperty);
        set => SetValue(ConfigureCommandProperty, value);
    }
}
