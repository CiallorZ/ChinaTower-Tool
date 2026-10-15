using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkForge.App.Presentation;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Controls;

/// <summary>
/// 统一状态标签：图标 + 文字 + 颜色三重表达（UI 规范 §5.4）。
/// </summary>
/// <remarks>
/// 推荐通过 <see cref="DiagnosticStatus"/> / <see cref="TaskRunState"/> 绑定，
/// 而不是手工设置颜色，以保证全应用状态口径一致：
/// <code>
/// &lt;controls:StatusBadge DiagnosticStatus="{Binding Status}" /&gt;
/// &lt;controls:StatusBadge TaskState="{Binding RunState}" /&gt;
/// &lt;controls:StatusBadge DependencyName="Npcap" /&gt;
/// </code>
/// 控件不承载业务逻辑，只消费 <see cref="StatusPresentation"/> 的映射结果。
/// </remarks>
public sealed class StatusBadge : Control
{
    static StatusBadge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(StatusBadge),
            new FrameworkPropertyMetadata(typeof(StatusBadge)));
    }

    /// <summary>步骤层状态；设置后自动推导视觉语义、图标与文案。</summary>
    public static readonly DependencyProperty DiagnosticStatusProperty =
        DependencyProperty.Register(
            nameof(DiagnosticStatus),
            typeof(DiagnosticStatus?),
            typeof(StatusBadge),
            new PropertyMetadata(null, OnStatusSourceChanged));

    /// <summary>任务层状态；设置后自动推导视觉语义、图标与文案。</summary>
    public static readonly DependencyProperty TaskStateProperty =
        DependencyProperty.Register(
            nameof(TaskState),
            typeof(TaskRunState?),
            typeof(StatusBadge),
            new PropertyMetadata(null, OnStatusSourceChanged));

    /// <summary>依赖缺失项名称；设置后显示"依赖缺失"而非"失败"。</summary>
    public static readonly DependencyProperty DependencyNameProperty =
        DependencyProperty.Register(
            nameof(DependencyName),
            typeof(string),
            typeof(StatusBadge),
            new PropertyMetadata(null, OnStatusSourceChanged));

    /// <summary>覆盖显示文案（留空则使用映射的默认文案）。</summary>
    public static readonly DependencyProperty TextOverrideProperty =
        DependencyProperty.Register(
            nameof(TextOverride),
            typeof(string),
            typeof(StatusBadge),
            new PropertyMetadata(null, OnStatusSourceChanged));

    private static readonly DependencyPropertyKey ResolvedGlyphKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ResolvedGlyph),
            typeof(string),
            typeof(StatusBadge),
            new PropertyMetadata(string.Empty));

    /// <summary>当前图标字形。</summary>
    public static readonly DependencyProperty ResolvedGlyphProperty = ResolvedGlyphKey.DependencyProperty;

    private static readonly DependencyPropertyKey ResolvedTextKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ResolvedText),
            typeof(string),
            typeof(StatusBadge),
            new PropertyMetadata(string.Empty));

    /// <summary>当前显示文案。</summary>
    public static readonly DependencyProperty ResolvedTextProperty = ResolvedTextKey.DependencyProperty;

    private static readonly DependencyPropertyKey ResolvedAccessibleTextKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ResolvedAccessibleText),
            typeof(string),
            typeof(StatusBadge),
            new PropertyMetadata(string.Empty));

    /// <summary>当前可访问描述（读屏与 ToolTip）。</summary>
    public static readonly DependencyProperty ResolvedAccessibleTextProperty =
        ResolvedAccessibleTextKey.DependencyProperty;

    private static readonly DependencyPropertyKey ResolvedVisualKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ResolvedVisual),
            typeof(LfStatusVisual),
            typeof(StatusBadge),
            new PropertyMetadata(LfStatusVisual.Neutral));

    /// <summary>当前视觉语义。</summary>
    public static readonly DependencyProperty ResolvedVisualProperty = ResolvedVisualKey.DependencyProperty;

    private static readonly DependencyPropertyKey ResolvedForegroundKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ResolvedForeground),
            typeof(Brush),
            typeof(StatusBadge),
            new PropertyMetadata(null));

    /// <summary>当前前景画刷（由主题令牌解析得到）。</summary>
    public static readonly DependencyProperty ResolvedForegroundProperty =
        ResolvedForegroundKey.DependencyProperty;

    private static readonly DependencyPropertyKey ResolvedBackgroundKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ResolvedBackground),
            typeof(Brush),
            typeof(StatusBadge),
            new PropertyMetadata(null));

    /// <summary>当前背景画刷（由主题令牌解析得到）。</summary>
    public static readonly DependencyProperty ResolvedBackgroundProperty =
        ResolvedBackgroundKey.DependencyProperty;

    /// <summary>步骤层状态。</summary>
    public DiagnosticStatus? DiagnosticStatus
    {
        get => (DiagnosticStatus?)GetValue(DiagnosticStatusProperty);
        set => SetValue(DiagnosticStatusProperty, value);
    }

    /// <summary>任务层状态。</summary>
    public TaskRunState? TaskState
    {
        get => (TaskRunState?)GetValue(TaskStateProperty);
        set => SetValue(TaskStateProperty, value);
    }

    /// <summary>依赖缺失项名称。</summary>
    public string? DependencyName
    {
        get => (string?)GetValue(DependencyNameProperty);
        set => SetValue(DependencyNameProperty, value);
    }

    /// <summary>覆盖显示文案。</summary>
    public string? TextOverride
    {
        get => (string?)GetValue(TextOverrideProperty);
        set => SetValue(TextOverrideProperty, value);
    }

    /// <summary>解析后的图标字形。</summary>
    public string ResolvedGlyph => (string)GetValue(ResolvedGlyphProperty);

    /// <summary>解析后的显示文案。</summary>
    public string ResolvedText => (string)GetValue(ResolvedTextProperty);

    /// <summary>解析后的可访问描述。</summary>
    public string ResolvedAccessibleText => (string)GetValue(ResolvedAccessibleTextProperty);

    /// <summary>解析后的视觉语义。</summary>
    public LfStatusVisual ResolvedVisual => (LfStatusVisual)GetValue(ResolvedVisualProperty);

    /// <summary>解析后的前景画刷。</summary>
    public Brush? ResolvedForeground => (Brush?)GetValue(ResolvedForegroundProperty);

    /// <summary>解析后的背景画刷。</summary>
    public Brush? ResolvedBackground => (Brush?)GetValue(ResolvedBackgroundProperty);

    private static void OnStatusSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatusBadge)d).Refresh();

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Refresh();
    }

    /// <summary>
    /// 重新计算视觉语义与文案，并解析当前主题下的令牌画刷。
    /// </summary>
    private void Refresh()
    {
        var map = Resolve();

        SetValue(ResolvedGlyphKey, map.IconGlyph);
        SetValue(ResolvedVisualKey, map.Visual);
        SetValue(ResolvedTextKey, string.IsNullOrWhiteSpace(TextOverride) ? map.DisplayText : TextOverride);
        SetValue(ResolvedAccessibleTextKey, map.AccessibleText);

        var suffix = StatusPresentation.TokenSuffix(map.Visual);
        SetValue(ResolvedForegroundKey, FindBrush($"LfBrush{suffix}"));
        SetValue(ResolvedBackgroundKey, FindBrush($"LfBrush{suffix}Subtle"));
    }

    private StatusVisualMap Resolve()
    {
        // 依赖缺失优先：必须区别于普通失败，因此即使同时给了状态也以依赖缺失呈现。
        if (!string.IsNullOrWhiteSpace(DependencyName))
        {
            return StatusPresentation.ForMissingDependency(DependencyName);
        }

        if (DiagnosticStatus is { } status)
        {
            return StatusPresentation.ForDiagnosticStatus(status);
        }

        if (TaskState is { } state)
        {
            return StatusPresentation.ForTaskRunState(state);
        }

        return StatusPresentation.ForTaskRunState(TaskRunState.Pending);
    }

    /// <summary>
    /// 从当前主题解析令牌画刷。
    /// </summary>
    /// <remarks>
    /// 每次重新查找而不是缓存画刷实例：主题切换时调色板字典会被整体替换，
    /// 缓存的旧画刷不会更新（见 ADR-017）。
    /// </remarks>
    private Brush? FindBrush(string tokenKey) => TryFindResource(tokenKey) as Brush;
}
