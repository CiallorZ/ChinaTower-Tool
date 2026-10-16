using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LinkForge.App.Navigation;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LinkForge.App.Views;

/// <summary>
/// 应用壳层窗口（UI 规范 §3.1）。
/// </summary>
/// <remarks>
/// 依据 DEV_STANDARDS §1.2，本类只做三件"必须由 View 完成"的事：
/// <list type="number">
///   <item>把控件事件转成 ViewModel 调用（导航选择、面板高度变化）。</item>
///   <item>根据折叠状态设置导航栏宽度（宽度是布局细节，ViewModel 不该关心像素）。</item>
///   <item>把系统主题变化事件转给 ViewModel（事件源在 WPF 层）。</item>
/// </list>
/// 其余一律通过绑定完成。
/// </remarks>
public partial class ShellWindow : FluentWindow
{
    private readonly ShellViewModel _viewModel;

    /// <summary>
    /// 系统主题监听是否已挂上。
    /// </summary>
    /// <remarks>
    /// 必须显式记录：<c>SystemThemeWatcher.UnWatch</c> 在窗口句柄已被销毁时
    /// 会抛 <see cref="InvalidOperationException"/>（"Could not get window handle."），
    /// 而 <c>Closed</c> 事件正好发生在 HWND 销毁之后。
    /// 因此解绑必须提前到 <c>Closing</c>，且只在本窗口确实挂过钩子时执行。
    /// </remarks>
    private bool _isThemeWatcherAttached;

    /// <summary>
    /// 创建壳层窗口。
    /// </summary>
    /// <param name="viewModel">壳层视图模型。</param>
    public ShellWindow(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;

        // 分组节点（如"接入调试"）只负责展开二级项，不切换页面。
        // ViewModel 已经会忽略分组节点，但 TreeView 自身仍会把分组行画成"已选中"，
        // 于是出现"高亮在 A、页面内容是 B"的分裂状态。
        // 用 AddHandler 挂冒泡事件（而不是 EventSetter）：样式定义在独立资源字典里，
        // 那里没有 x:Class 可供绑定事件处理函数。
        NavigationTree.AddHandler(
            System.Windows.Controls.TreeViewItem.SelectedEvent,
            new RoutedEventHandler(OnNavigationTreeItemSelected));

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 折叠/展开日志面板必须同时改"行高"和"可见性"：
        // 只改可见性会留下一条与 LogPanelHeight 等高的空白，
        // 只改行高则 Border 仍占位。
        ApplyLogPanelHeight();

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 监听系统主题变化：仅在"跟随系统"偏好下由 ViewModel 决定是否应用。
        //
        // 注意副作用：Watch 在注册第一个窗口时会调用 ApplicationThemeManager.ApplySystemTheme，
        // 即强制把 WPF-UI 主题切到系统主题，不管用户选的是不是"跟随系统"。
        // 这不是我们能控制的（控件库行为），因此必须由 ThemeManager.Changed 兜回来，
        // 见 OnApplicationThemeChanged。
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: false);
        _isThemeWatcherAttached = true;

        ApplicationThemeManager.Changed += OnApplicationThemeChanged;

        ApplyLogPanelHeight();
        SyncSelectedNavigationItem();
    }

    /// <summary>
    /// 在窗口句柄销毁前解绑系统主题钩子。
    /// </summary>
    /// <remarks>
    /// 放在 <c>Closing</c> 而不是 <c>Closed</c>：<c>Closed</c> 触发时 HWND 已销毁，
    /// <c>SystemThemeWatcher.UnWatch</c> 会因拿不到句柄而抛异常，
    /// 表现为"每次关闭应用都报错退出"。
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;

        if (_isThemeWatcherAttached && IsLoaded)
        {
            SystemThemeWatcher.UnWatch(this);
        }

        _isThemeWatcherAttached = false;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded -= OnLoaded;
        Closing -= OnClosing;
        Closed -= OnClosed;
    }

    /// <summary>
    /// WPF-UI 主题变化后把设计令牌同步回用户偏好。
    /// </summary>
    /// <remarks>
    /// 触发来源有两类，都不能忽略：
    /// <list type="number">
    ///   <item><c>SystemThemeWatcher.Watch</c> 注册首个窗口时的初始化调用；</item>
    ///   <item>系统主题真实变化（<c>WM_SETTINGCHANGE</c> / <c>WM_THEMECHANGED</c> 等）。</item>
    /// </list>
    /// 两者都只改 WPF-UI 字典，不改 <c>Palette.*.xaml</c>；
    /// 不补这一步就会出现"控件库是深色、Lf* 令牌还是浅色"的半截主题。
    /// </remarks>
    private void OnApplicationThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        _viewModel.SyncThemeWithWpfUi();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 导航栏宽度由 XAML 的 DataTrigger 在两个令牌之间切换，不需要代码后置参与。
        if (e.PropertyName is nameof(ShellViewModel.IsLogPanelOpen)
            or nameof(ShellViewModel.LogPanelHeight))
        {
            ApplyLogPanelHeight();
        }
    }

    /// <summary>
    /// 把日志面板的行高同步为"展开时 = 用户高度，折叠时 = 0"。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="GridLength"/> 像素值而不是 <c>Auto</c>/<c>*</c>：
    /// <c>GridSplitter</c> 的 <c>ResizeBehavior="PreviousAndNext"</c> 只对像素行生效，
    /// 行高是 Auto/Star 时拖动会被忽略（表现为"分隔条拖不动"）。
    /// 折叠时给 0 而不是把行删掉，是为了保持行索引稳定（GridSplitter 的行号不变）。
    /// </remarks>
    private void ApplyLogPanelHeight()
    {
        if (LogPanelRow is null)
        {
            return;
        }

        LogPanelRow.Height = _viewModel.IsLogPanelOpen
            ? new GridLength(
                Math.Max(_viewModel.LogPanelHeight, AppSettingsStore.MinLogPanelHeight),
                GridUnitType.Pixel)
            : new GridLength(0, GridUnitType.Pixel);
    }

    /// <summary>
    /// 用户拖动分隔条结束后，把实际行高回写到 ViewModel 并持久化。
    /// </summary>
    /// <remarks>
    /// 没有这一步，"记住日志面板高度"就只是启动时读一次、
    /// 用户拖动后永远不保存 —— 表现为"每次重启都回到默认高度"。
    /// 拖动过程中不回写（DragDelta 频率很高，会频繁写磁盘）。
    /// </remarks>
    private void OnLogPanelSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (double.IsNaN(LogPanelRow.Height.Value) || LogPanelRow.Height.IsStar)
        {
            return;
        }

        _viewModel.SetLogPanelHeight(LogPanelRow.Height.Value);
    }

    /// <summary>
    /// 导航选择变化 -> 通知 ViewModel。
    /// </summary>
    /// <remarks>
    /// 分组节点（如"接入调试"）被选中时不切换页面，只展开子项 ——
    /// ViewModel 的 NavigateTo 已显式忽略分组节点。
    /// </remarks>
    private void OnNavigationSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NavigationNode node)
        {
            _viewModel.NavigateTo(node);
        }
    }

    /// <summary>
    /// 阻止分组节点被"选中"。
    /// </summary>
    /// <remarks>
    /// TreeView 的选中状态由控件自身维护，ViewModel 无法阻止它；
    /// 因此这里在选中发生后立即撤销，并把选中项回填到当前页面节点上。
    /// 若只撤销而不回填，会出现"没有任何导航项高亮，但内容区仍显示某个页面"。
    /// </remarks>
    private void OnNavigationTreeItemSelected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.TreeViewItem item
            || item.DataContext is not NavigationNode node
            || !node.IsGroup)
        {
            return;
        }

        // 点击分组行的合理反馈是"展开它"，而不是把高亮停在一个没有内容的入口上。
        item.IsExpanded = true;
        item.IsSelected = false;

        SelectNavigationNode(_viewModel.SelectedNode);
    }

    /// <summary>
    /// 让导航树的选中项与 ViewModel 保持一致（初始加载时用）。
    /// </summary>
    private void SyncSelectedNavigationItem()
    {
        // TreeView 不支持直接绑定 SelectedItem，
        // 因此初始选中需要在这里回填一次。仅做一次，避免与用户操作互相打架。
        SelectNavigationNode(_viewModel.SelectedNode);
    }

    /// <summary>把导航树的选中项设为指定节点。</summary>
    private void SelectNavigationNode(NavigationNode? node)
    {
        if (node is null)
        {
            return;
        }

        NavigationTree.UpdateLayout();

        if (FindContainer(NavigationTree, node) is { } container)
        {
            container.IsSelected = true;
        }
    }

    /// <summary>在导航树中查找指定数据项对应的容器。</summary>
    private static System.Windows.Controls.TreeViewItem? FindContainer(ItemsControl parent, object target)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.Controls.TreeViewItem container)
            {
                continue;
            }

            if (ReferenceEquals(item, target))
            {
                return container;
            }

            if (container.Items.Count > 0 && FindContainer(container, target) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
