using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LinkForge.App.Presentation;

namespace LinkForge.App.Controls;

/// <summary>
/// 日志查看器：虚拟化 + 级别过滤 + 文本搜索 + 自动滚动（UI 规范 §10、§5.3）。
/// </summary>
/// <remarks>
/// 三条硬性约束：
/// <list type="number">
///   <item><b>虚拟化</b>：诊断一次可能产出上万行日志，不虚拟化会让 UI 线程
///         在创建 Visual 时被卡死（规范 §10 明确要求日志列表虚拟化）。</item>
///   <item><b>过滤在视图层完成</b>：用 <see cref="CollectionViewSource"/> 过滤而不是
///         每次输入都重建集合，避免反复分配大列表。</item>
///   <item><b>脱敏前置</b>：<see cref="LogEntry"/> 在构造时即已脱敏，
///         本控件不接触明文，也不提供任何"取出原始消息"的入口。</item>
/// </list>
/// </remarks>
public sealed class LogViewer : Control
{
    static LogViewer()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(LogViewer),
            new FrameworkPropertyMetadata(typeof(LogViewer)));
    }

    /// <summary>日志源集合。</summary>
    public static readonly DependencyProperty EntriesProperty =
        DependencyProperty.Register(
            nameof(Entries),
            typeof(IEnumerable<LogEntry>),
            typeof(LogViewer),
            new PropertyMetadata(null, OnEntriesChanged));

    /// <summary>最低显示级别；为 null 表示不过滤级别。</summary>
    public static readonly DependencyProperty MinimumLevelProperty =
        DependencyProperty.Register(
            nameof(MinimumLevel),
            typeof(LfLogLevel?),
            typeof(LogViewer),
            new PropertyMetadata(null, OnFilterChanged));

    /// <summary>搜索关键字（对已脱敏的消息做包含匹配）。</summary>
    public static readonly DependencyProperty SearchTextProperty =
        DependencyProperty.Register(
            nameof(SearchText),
            typeof(string),
            typeof(LogViewer),
            new PropertyMetadata(null, OnFilterChanged));

    /// <summary>是否自动滚动到最新一行。</summary>
    public static readonly DependencyProperty AutoScrollProperty =
        DependencyProperty.Register(
            nameof(AutoScroll),
            typeof(bool),
            typeof(LogViewer),
            new PropertyMetadata(true));

    /// <summary>复制当前筛选结果。</summary>
    public static readonly DependencyProperty CopyCommandProperty =
        DependencyProperty.Register(
            nameof(CopyCommand),
            typeof(ICommand),
            typeof(LogViewer),
            new PropertyMetadata(null));

    /// <summary>导出当前筛选结果。</summary>
    public static readonly DependencyProperty ExportCommandProperty =
        DependencyProperty.Register(
            nameof(ExportCommand),
            typeof(ICommand),
            typeof(LogViewer),
            new PropertyMetadata(null));

    /// <summary>复制/导出后的反馈文本。</summary>
    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(
            nameof(StatusText),
            typeof(string),
            typeof(LogViewer),
            new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey VisibleCountKey =
        DependencyProperty.RegisterReadOnly(
            nameof(VisibleCount),
            typeof(int),
            typeof(LogViewer),
            new PropertyMetadata(0));

    /// <summary>经过滤后当前可见的行数。</summary>
    public static readonly DependencyProperty VisibleCountProperty = VisibleCountKey.DependencyProperty;

    private static readonly DependencyPropertyKey TotalCountKey =
        DependencyProperty.RegisterReadOnly(
            nameof(TotalCount),
            typeof(int),
            typeof(LogViewer),
            new PropertyMetadata(0));

    /// <summary>过滤前的总行数。</summary>
    public static readonly DependencyProperty TotalCountProperty = TotalCountKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsFilteredKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsFiltered),
            typeof(bool),
            typeof(LogViewer),
            new PropertyMetadata(false));

    /// <summary>当前是否处于过滤/搜索状态。</summary>
    public static readonly DependencyProperty IsFilteredProperty = IsFilteredKey.DependencyProperty;

    private static readonly DependencyPropertyKey HasVisibleEntriesKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasVisibleEntries),
            typeof(bool),
            typeof(LogViewer),
            new PropertyMetadata(false));

    /// <summary>当前筛选结果是否至少有一行。</summary>
    public static readonly DependencyProperty HasVisibleEntriesProperty =
        HasVisibleEntriesKey.DependencyProperty;

    private static readonly DependencyPropertyKey EmptyMessageKey =
        DependencyProperty.RegisterReadOnly(
            nameof(EmptyMessage),
            typeof(string),
            typeof(LogViewer),
            new PropertyMetadata(string.Empty));

    /// <summary>无日志或无匹配结果时的说明。</summary>
    public static readonly DependencyProperty EmptyMessageProperty =
        EmptyMessageKey.DependencyProperty;

    private CollectionViewSource? _viewSource;

    /// <summary>日志源集合。</summary>
    public IEnumerable<LogEntry>? Entries
    {
        get => (IEnumerable<LogEntry>?)GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    /// <summary>最低显示级别。</summary>
    public LfLogLevel? MinimumLevel
    {
        get => (LfLogLevel?)GetValue(MinimumLevelProperty);
        set => SetValue(MinimumLevelProperty, value);
    }

    /// <summary>搜索关键字。</summary>
    public string? SearchText
    {
        get => (string?)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    /// <summary>是否自动滚动到最新一行。</summary>
    public bool AutoScroll
    {
        get => (bool)GetValue(AutoScrollProperty);
        set => SetValue(AutoScrollProperty, value);
    }

    /// <summary>复制当前筛选结果。</summary>
    public ICommand? CopyCommand
    {
        get => (ICommand?)GetValue(CopyCommandProperty);
        set => SetValue(CopyCommandProperty, value);
    }

    /// <summary>导出当前筛选结果。</summary>
    public ICommand? ExportCommand
    {
        get => (ICommand?)GetValue(ExportCommandProperty);
        set => SetValue(ExportCommandProperty, value);
    }

    /// <summary>复制/导出后的反馈文本。</summary>
    public string? StatusText
    {
        get => (string?)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    /// <summary>经过滤后当前可见的行数。</summary>
    public int VisibleCount => (int)GetValue(VisibleCountProperty);

    /// <summary>过滤前的总行数。</summary>
    public int TotalCount => (int)GetValue(TotalCountProperty);

    /// <summary>当前是否处于过滤/搜索状态。</summary>
    public bool IsFiltered => (bool)GetValue(IsFilteredProperty);

    /// <summary>当前筛选结果是否至少有一行。</summary>
    public bool HasVisibleEntries => (bool)GetValue(HasVisibleEntriesProperty);

    /// <summary>无日志或无匹配结果时的说明。</summary>
    public string EmptyMessage => (string)GetValue(EmptyMessageProperty);

    /// <summary>供模板内 ItemsControl 绑定的视图（已应用过滤）。</summary>
    internal static readonly DependencyPropertyKey ViewKey =
        DependencyProperty.RegisterReadOnly(
            nameof(View),
            typeof(System.ComponentModel.ICollectionView),
            typeof(LogViewer),
            new PropertyMetadata(null));

    /// <summary>供模板内 ItemsControl 绑定的视图（已应用过滤）。</summary>
    internal static readonly DependencyProperty ViewProperty = ViewKey.DependencyProperty;

    /// <summary>
    /// 供模板内 ItemsControl 绑定的视图（已应用过滤）。
    /// </summary>
    /// <remarks>
    /// 必须是依赖属性而不是普通 CLR 属性。
    /// 模板里 <c>ItemsSource="{Binding View, RelativeSource=TemplatedParent}"</c>
    /// 只在数据源发出通知时才重新拉取。
    /// 若 View 是普通属性，替换 Entries 后视图对象变了、但绑定毫不知情，
    /// 结果 Items 仍然为空 —— 表现就是"换了日志源，界面一行都不显示"。
    /// 这个缺陷在构造时赋值看不出来（那时 OnApplyTemplate 还没跑），
    /// 只有在模板已应用后再换数据源才会暴露。
    /// </remarks>
    internal System.ComponentModel.ICollectionView? View
    {
        get => (System.ComponentModel.ICollectionView?)GetValue(ViewProperty);
        private set => SetValue(ViewKey, value);
    }

    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        RebuildView();
    }

    private static void OnEntriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (LogViewer)d;

        if (e.OldValue is INotifyCollectionChanged oldNotify)
        {
            oldNotify.CollectionChanged -= viewer.OnSourceCollectionChanged;
        }

        viewer.RebuildView();
    }

    private static void OnFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (LogViewer)d;
        viewer.RefreshView();
        viewer.RefreshCounts();
    }

    /// <summary>
    /// 重建视图源。
    /// </summary>
    /// <remarks>
    /// 筛选处理器**只挂一次**（方法组），筛选条件从当前依赖属性实时读取。
    /// 若每次刷新都 <c>Filter += ...</c>，处理器会累积，
    /// 同一行被重复判定，且旧处理器持有旧条件，导致筛选结果不可预测。
    /// </remarks>
    private void RebuildView()
    {
        if (Entries is null)
        {
            _viewSource = null;
            View = null;
            SetValue(VisibleCountKey, 0);
            SetValue(TotalCountKey, 0);
            return;
        }

        _viewSource = new CollectionViewSource { Source = Entries };
        _viewSource.Filter += FilterEntry;

        // 通过依赖属性赋值，让模板里的 ItemsSource 绑定收到变更通知。
        View = _viewSource.View;

        if (Entries is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += OnSourceCollectionChanged;
        }

        RefreshCounts();
    }

    private void FilterEntry(object sender, FilterEventArgs e)
    {
        if (e.Item is not LogEntry entry)
        {
            e.Accepted = false;
            return;
        }

        if (MinimumLevel is { } minimum && entry.Level < minimum)
        {
            e.Accepted = false;
            return;
        }

        e.Accepted = MatchesSearch(entry, SearchText);
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 日志通常由后台任务产生，回调不在 UI 线程，必须转回再操作依赖属性。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnEntriesAppended);
            return;
        }

        OnEntriesAppended();
    }

    private void OnEntriesAppended()
    {
        RefreshCounts();

        // 新行已通过视图过滤后追加，此时才滚动，避免滚到被过滤掉的行上。
        if (AutoScroll)
        {
            ScrollToEnd();
        }
    }

    private void RefreshView()
    {
        // 筛选条件由 FilterEntry 实时读取依赖属性，因此这里只需触发一次刷新。
        // DeferRefresh 把"改条件 + 刷新"合并为一次视图重建，
        // 避免搜索框每输入一个字符都全量重建视图。
        using (_viewSource?.View?.DeferRefresh())
        {
            SetValue(
                IsFilteredKey,
                MinimumLevel is not null || !string.IsNullOrWhiteSpace(SearchText));
        }
    }

    /// <summary>
    /// 重新统计总行数与可见行数。
    /// </summary>
    /// <remarks>
    /// <see cref="System.ComponentModel.ICollectionView"/> 不提供经过滤后的计数，
    /// 只能枚举。这里刻意直接枚举**源集合**（而不是枚举视图）：
    /// 枚举视图会为每行创建容器，代价等同于放弃虚拟化；
    /// 枚举源集合只是遍历内存对象，不产生任何视觉元素。
    /// 因此 O(n) 成本是可接受的，且不违背虚拟化要求。
    /// </remarks>
    private void RefreshCounts()
    {
        var entries = Entries;

        if (entries is null)
        {
            SetValue(VisibleCountKey, 0);
            SetValue(TotalCountKey, 0);
            SetValue(HasVisibleEntriesKey, false);
            SetValue(EmptyMessageKey, "暂无日志。运行诊断或执行页面操作后，真实日志会显示在这里。");
            return;
        }

        var minimum = MinimumLevel;
        var search = SearchText;
        var total = 0;
        var visible = 0;

        foreach (var entry in entries)
        {
            total++;

            if (minimum is { } level && entry.Level < level)
            {
                continue;
            }

            if (!MatchesSearch(entry, search))
            {
                continue;
            }

            visible++;
        }

        SetValue(TotalCountKey, total);
        SetValue(VisibleCountKey, visible);
        SetValue(HasVisibleEntriesKey, visible > 0);
        SetValue(
            EmptyMessageKey,
            total == 0
                ? "暂无日志。运行诊断或执行页面操作后，真实日志会显示在这里。"
                : visible == 0
                    ? "没有匹配当前筛选条件的日志。"
                    : string.Empty);
    }

    private static bool MatchesSearch(LogEntry entry, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.Module.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.RunId.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.TargetText.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.EventId.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>滚动到最新一行。</summary>
    private void ScrollToEnd()
    {
        if (GetTemplateChild("PART_List") is not ListBox list)
        {
            return;
        }

        if (list.Items.Count == 0)
        {
            return;
        }

        list.ScrollIntoView(list.Items[^1]);
    }

    /// <summary>
    /// 供模板在卸载时解绑，避免源集合长期持有控件引用。
    /// </summary>
    internal void DetachSource()
    {
        if (Entries is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged -= OnSourceCollectionChanged;
        }
    }
}
