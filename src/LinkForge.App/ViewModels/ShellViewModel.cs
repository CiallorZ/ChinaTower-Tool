using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.App.Navigation;
using LinkForge.App.Presentation;
using LinkForge.App.Services;

namespace LinkForge.App.ViewModels;

/// <summary>
/// 应用壳层的视图模型：导航状态、主题偏好、面板开合。
/// </summary>
/// <remarks>
/// 依据 DEV_STANDARDS §1.2：业务逻辑不得写在 View 的 code-behind。
/// 因此窗口的每一项状态（当前页、导航折叠、日志面板、主题）都由本类持有，
/// View 只负责绑定与转发输入。
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppSettingsStore? _settingsStore;
    private readonly ThemeManager? _themeManager;
    private readonly IStructuredLogService? _structuredLogs;
    private readonly ILogPanelExporter? _logExporter;
    private readonly ObservableCollection<LogEntry> _localLogs = [];

    /// <summary>
    /// 创建壳层视图模型。
    /// </summary>
    /// <param name="themeManager">主题服务；为 null 时主题相关操作变为无操作（设计时/测试用）。</param>
    /// <param name="settingsStore">设置持久化；为 null 时不读写磁盘。</param>
    /// <param name="dashboard">工作台视图模型；为 null 时内容区不显示工作台数据。</param>
    /// <param name="networkDiagnostics">网络诊断视图模型。</param>
    /// <param name="apiWorkbench">API 工作台视图模型。</param>
    /// <param name="mqttWorkbench">MQTT 工作台视图模型。</param>
    /// <param name="rtspDiagnostics">RTSP 诊断视图模型。</param>
    /// <param name="reportPage">项目与报告视图模型。</param>
    /// <param name="developerTools">开发者工具视图模型。</param>
    /// <param name="pluginManager">插件管理视图模型。</param>
    /// <param name="assetCatalog">设备与资产视图模型。</param>
    /// <param name="structuredLogs">应用级结构化日志；null 时仅保留进程内局部日志。</param>
    /// <param name="logExporter">日志复制/导出边界；null 时命令给出明确不可用反馈。</param>
    public ShellViewModel(
        ThemeManager? themeManager = null,
        AppSettingsStore? settingsStore = null,
        DashboardViewModel? dashboard = null,
        NetworkDiagnosticsViewModel? networkDiagnostics = null,
        ApiWorkbenchViewModel? apiWorkbench = null,
        MqttWorkbenchViewModel? mqttWorkbench = null,
        RtspDiagnosticsViewModel? rtspDiagnostics = null,
        ReportPageViewModel? reportPage = null,
        DeveloperToolsViewModel? developerTools = null,
        PluginManagerViewModel? pluginManager = null,
        AssetCatalogViewModel? assetCatalog = null,
        IStructuredLogService? structuredLogs = null,
        ILogPanelExporter? logExporter = null)
    {
        _themeManager = themeManager;
        _settingsStore = settingsStore;
        _structuredLogs = structuredLogs;
        _logExporter = logExporter;
        Dashboard = dashboard;
        NetworkDiagnostics = networkDiagnostics;
        ApiWorkbench = apiWorkbench;
        MqttWorkbench = mqttWorkbench;
        RtspDiagnostics = rtspDiagnostics;
        ReportPage = reportPage;
        DeveloperTools = developerTools;
        PluginManager = pluginManager;
        AssetCatalog = assetCatalog;

        NavigationItems = new ObservableCollection<NavigationNode>(NavigationCatalog.TopLevel);
        LogEntries = structuredLogs?.Entries ?? _localLogs;

        AttachLogSource(Dashboard?.SelfCheckLogs);
        AttachLogSource(NetworkDiagnostics?.Logs);
        AttachLogSource(ApiWorkbench?.Logs);
        AttachLogSource(MqttWorkbench?.Logs);
        AttachLogSource(RtspDiagnostics?.Logs);
        AttachLogSource(PluginManager?.Logs);

        LogEntries.CollectionChanged += OnLogEntriesChanged;

        if (_structuredLogs is not null)
        {
            _structuredLogs.PropertyChanged += OnStructuredLogsPropertyChanged;
        }

        var settings = settingsStore?.Load();

        IsNavigationPaneOpen = settings?.IsNavigationPaneOpen ?? true;
        IsLogPanelOpen = settings?.IsLogPanelOpen ?? false;
        LogPanelHeight = settings?.LogPanelHeight ?? 200;

        SelectedNode = NavigationItems.FirstOrDefault(static n => n.IsNavigable);
        NavigateTo(SelectedNode);
    }

    /// <summary>一级导航项。</summary>
    public ObservableCollection<NavigationNode> NavigationItems { get; }

    /// <summary>
    /// 工作台视图模型。
    /// </summary>
    /// <remarks>
    /// 由壳层持有并交给内容区的 <c>DashboardPage</c> 使用（见 <c>ShellWindow.xaml</c>）：
    /// 页面的 <c>DataContext</c> 继承自窗口，若不在窗口这一层做转换，
    /// 工作台的所有绑定都会解析到 <see cref="ShellViewModel"/> 上而全部落空 ——
    /// 表现是"环境信息与依赖状态一片空白"，且不会有任何报错。
    /// </remarks>
    public DashboardViewModel? Dashboard { get; }

    /// <summary>网络诊断页面视图模型；为 null 时页面显示明确不可用状态。</summary>
    public NetworkDiagnosticsViewModel? NetworkDiagnostics { get; }

    /// <summary>API 工作台视图模型。</summary>
    public ApiWorkbenchViewModel? ApiWorkbench { get; }

    /// <summary>MQTT 工作台视图模型。</summary>
    public MqttWorkbenchViewModel? MqttWorkbench { get; }

    /// <summary>RTSP 诊断视图模型。</summary>
    public RtspDiagnosticsViewModel? RtspDiagnostics { get; }

    /// <summary>项目与报告视图模型。</summary>
    public ReportPageViewModel? ReportPage { get; }

    /// <summary>开发者工具视图模型。</summary>
    public DeveloperToolsViewModel? DeveloperTools { get; }

    /// <summary>插件管理视图模型。</summary>
    public PluginManagerViewModel? PluginManager { get; }

    /// <summary>设备与资产视图模型。</summary>
    public AssetCatalogViewModel? AssetCatalog { get; }

    /// <summary>底部日志面板显示的应用级日志。</summary>
    public ObservableCollection<LogEntry> LogEntries { get; }

    /// <summary>日志搜索关键字。</summary>
    [ObservableProperty]
    public partial string LogSearchText { get; set; } = string.Empty;

    /// <summary>日志最低显示级别；null 表示全部。</summary>
    [ObservableProperty]
    public partial LfLogLevel? LogMinimumLevel { get; set; }

    /// <summary>最近的日志复制/导出反馈。</summary>
    [ObservableProperty]
    public partial string LogActionFeedback { get; set; } = string.Empty;

    /// <summary>最近一次日志操作是否失败。</summary>
    [ObservableProperty]
    public partial bool HasLogActionError { get; set; }

    /// <summary>日志文件错误；正常时为空。</summary>
    public string? LogStorageError => _structuredLogs?.StorageError;

    /// <summary>日志面板底部状态：优先显示操作反馈，同时保留文件错误。</summary>
    public string LogPanelStatus
    {
        get
        {
            var parts = new List<string>(2);

            if (!string.IsNullOrWhiteSpace(LogActionFeedback))
            {
                parts.Add(LogActionFeedback);
            }

            if (!string.IsNullOrWhiteSpace(LogStorageError))
            {
                parts.Add(LogStorageError);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>当前筛选条件下是否有可操作日志。</summary>
    public bool HasVisibleLogs => EnumerateVisibleLogs().Any();

    /// <summary>当前选中的导航节点。</summary>
    [ObservableProperty]
    public partial NavigationNode? SelectedNode { get; set; }

    /// <summary>当前页面标题。</summary>
    [ObservableProperty]
    public partial string CurrentTitle { get; set; } = string.Empty;

    /// <summary>当前页面用途说明（规范 §4.2 第 1 项：标题 + 一句话用途）。</summary>
    [ObservableProperty]
    public partial string CurrentPurpose { get; set; } = string.Empty;

    /// <summary>当前页面标识。</summary>
    [ObservableProperty]
    public partial string CurrentPageId { get; set; } = string.Empty;

    /// <summary>是否显示工作台。</summary>
    public bool IsDashboardSelected => CurrentPageId == "dashboard";

    /// <summary>是否显示网络诊断。</summary>
    public bool IsNetworkDiagnosticsSelected => CurrentPageId == "network";

    /// <summary>是否显示 API 工作台。</summary>
    public bool IsApiWorkbenchSelected => CurrentPageId == "connect.api";

    /// <summary>是否显示 IoT 与协议工作台（当前实现 MQTT）。</summary>
    public bool IsMqttWorkbenchSelected => CurrentPageId == "connect.iot";

    /// <summary>是否显示 RTSP 诊断页。</summary>
    public bool IsRtspDiagnosticsSelected => CurrentPageId == "connect.video";

    /// <summary>是否显示项目与报告页。</summary>
    public bool IsReportPageSelected => CurrentPageId == "reports";

    /// <summary>是否显示开发者工具页。</summary>
    public bool IsDeveloperToolsSelected => CurrentPageId == "developer";

    /// <summary>是否显示插件管理页。</summary>
    public bool IsPluginManagerSelected => CurrentPageId == "settings.plugins";

    /// <summary>是否显示设备与资产页。</summary>
    public bool IsAssetCatalogSelected => CurrentPageId == "assets";

    /// <summary>当前页面是否已实现（false 时内容区显示明确的未实现状态）。</summary>
    [ObservableProperty]
    public partial bool IsCurrentModuleImplemented { get; set; }

    /// <summary>当前模块不可用的原因说明；已实现时为空。</summary>
    [ObservableProperty]
    public partial string CurrentUnavailableReason { get; set; } = string.Empty;

    /// <summary>导航栏是否展开（false 为图标模式）。</summary>
    [ObservableProperty]
    public partial bool IsNavigationPaneOpen { get; set; }

    /// <summary>底部日志面板是否展开。</summary>
    [ObservableProperty]
    public partial bool IsLogPanelOpen { get; set; }

    /// <summary>底部日志面板高度。</summary>
    [ObservableProperty]
    public partial double LogPanelHeight { get; set; }

    /// <summary>状态栏文本。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>当前主题的显示文案。</summary>
    public string ThemeText => _themeManager is null
        ? "跟随系统"
        : _themeManager.Preference switch
        {
            LfThemePreference.Light => "浅色",
            LfThemePreference.Dark => "深色",
            _ => "跟随系统"
        };

    /// <summary>
    /// 导航到指定节点。
    /// </summary>
    /// <param name="node">目标节点；为 null 时忽略。</param>
    /// <remarks>
    /// 分组节点（如"接入调试"）不可直接导航：它只负责展开二级项。
    /// 这一点必须显式判断，否则点击分组会得到一个空页面。
    /// </remarks>
    public void NavigateTo(NavigationNode? node)
    {
        if (node is null || node.IsGroup)
        {
            return;
        }

        SelectedNode = node;
        CurrentPageId = node.Id;
        CurrentTitle = node.Title;
        CurrentPurpose = node.Purpose;
        IsCurrentModuleImplemented = node.Availability == LfModuleAvailability.Available;
        CurrentUnavailableReason = node.Availability switch
        {
            LfModuleAvailability.NotImplemented =>
                $"“{node.Title}”尚未实现。此入口已按功能树保留，功能将在后续阶段交付。",
            LfModuleAvailability.DependencyMissing =>
                $"“{node.Title}”依赖的外部组件当前不可用，请先在设置中检查依赖。",
            _ => string.Empty
        };

        UpdateStatusText();
        OnPropertyChanged(nameof(IsDashboardSelected));
        OnPropertyChanged(nameof(IsNetworkDiagnosticsSelected));
        OnPropertyChanged(nameof(IsApiWorkbenchSelected));
        OnPropertyChanged(nameof(IsMqttWorkbenchSelected));
        OnPropertyChanged(nameof(IsRtspDiagnosticsSelected));
        OnPropertyChanged(nameof(IsReportPageSelected));
        OnPropertyChanged(nameof(IsDeveloperToolsSelected));
        OnPropertyChanged(nameof(IsPluginManagerSelected));
        OnPropertyChanged(nameof(IsAssetCatalogSelected));

        // 每次进入报告页都重新读取历史：用户刚跑完一次诊断再切过来，
        // 看到的必须是包含这次结果的最新列表，而不是启动时的快照。
        if (node.Id == "reports")
        {
            _ = ReportPage?.LoadAsync();
        }

        // 资产页每次进入都重新读取数据库：用户刚导入 CSV 再切回来，
        // 必须看到包含这次导入的最新列表，而不是启动时的快照。
        if (node.Id == "assets")
        {
            _ = AssetCatalog?.LoadAsync();
        }
    }

    /// <summary>切换导航栏展开/折叠。</summary>
    [RelayCommand]
    private void ToggleNavigationPane()
    {
        IsNavigationPaneOpen = !IsNavigationPaneOpen;
        PersistSettings();
    }

    /// <summary>切换底部日志面板。</summary>
    [RelayCommand]
    private void ToggleLogPanel()
    {
        IsLogPanelOpen = !IsLogPanelOpen;
        PersistSettings();
    }

    /// <summary>
    /// 记录用户拖动分隔条后的日志面板高度。
    /// </summary>
    /// <param name="height">新的高度（像素）。</param>
    /// <remarks>
    /// 越界值由 <see cref="AppSettingsStore"/> 在保存时钳制；
    /// 这里只负责把值收下来并持久化，不重复实现钳制逻辑 ——
    /// 两处各写一份阈值，改一处漏一处。
    /// </remarks>
    public void SetLogPanelHeight(double height)
    {
        if (double.IsNaN(height) || double.IsInfinity(height))
        {
            return;
        }

        // 与 AppSettingsStore 用同一组阈值：这里钳制是为了让内存中的值也立刻合法，
        // 否则界面会短暂使用一个越界高度（重启后才被修正），看起来像"拖动结果丢失"。
        LogPanelHeight = Math.Clamp(
            height,
            AppSettingsStore.MinLogPanelHeight,
            AppSettingsStore.MaxLogPanelHeight);

        PersistSettings();
    }

    /// <summary>
    /// 应用主题偏好并持久化。
    /// </summary>
    /// <param name="preference">目标偏好。</param>
    [RelayCommand]
    private void ApplyTheme(LfThemePreference preference)
    {
        _themeManager?.Apply(preference);
        _themeManager?.PersistPreference();

        OnPropertyChanged(nameof(ThemeText));
        UpdateStatusText();
        PublishShellLog(
            LfLogLevel.Information,
            "shell.theme.changed",
            $"主题偏好已切换为“{ThemeText}”。");
    }

    /// <summary>
    /// 系统主题发生变化时同步（仅在"跟随系统"偏好下生效）。
    /// </summary>
    /// <param name="systemTheme">系统当前主题。</param>
    public void OnSystemThemeChanged(LfResolvedTheme systemTheme)
    {
        // 用户显式选择了浅色/深色时不得被系统变化覆盖：
        // 那会让"我明明选了深色，重启后却变回浅色"。
        if (_themeManager is null || _themeManager.Preference != LfThemePreference.FollowSystem)
        {
            return;
        }

        _themeManager.ApplyResolved(systemTheme);
        OnPropertyChanged(nameof(ThemeText));
        UpdateStatusText();
    }

    /// <summary>
    /// WPF-UI 主题被外部改动后，把设计令牌同步回用户偏好。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="OnSystemThemeChanged"/> 的分工：
    /// 后者是"系统主题变了，且用户选择跟随，于是接受新主题"；
    /// 本方法不判断系统主题，只负责消除"控件库主题与 Lf* 令牌不一致"这一状态，
    /// 因此用户显式选了浅色/深色时也会生效（把控件库拉回用户的选择）。
    /// </para>
    /// <para>
    /// 两条路径都可能被同一次系统主题变化触发，先执行的一方完成同步后，
    /// 另一方再执行只是重复应用同一主题，不产生额外副作用。
    /// </para>
    /// </remarks>
    public void SyncThemeWithWpfUi()
    {
        _themeManager?.SyncWithWpfUiTheme();

        OnPropertyChanged(nameof(ThemeText));
        UpdateStatusText();
    }

    /// <summary>保存面板与导航状态。</summary>
    private void PersistSettings()
    {
        if (_settingsStore is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        settings.IsNavigationPaneOpen = IsNavigationPaneOpen;
        settings.IsLogPanelOpen = IsLogPanelOpen;
        settings.LogPanelHeight = LogPanelHeight;
        _settingsStore.Save(settings);
    }

    /// <summary>复制当前筛选后的脱敏日志。</summary>
    [RelayCommand(CanExecute = nameof(HasVisibleLogs))]
    private void CopyLogs()
    {
        if (_logExporter is null)
        {
            SetLogActionFeedback("当前环境未提供剪贴板服务。", isError: true);
            return;
        }

        var entries = EnumerateVisibleLogs();
        var text = BuildLogText(entries);

        try
        {
            _logExporter.Copy(text);
            SetLogActionFeedback($"已复制 {entries.Count} 行脱敏日志。", isError: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.ExternalException)
        {
            SetLogActionFeedback($"复制日志失败：{ex.GetType().Name} — {ex.Message}", isError: true);
        }
    }

    /// <summary>导出当前筛选后的脱敏日志。</summary>
    [RelayCommand(CanExecute = nameof(HasVisibleLogs), IncludeCancelCommand = true)]
    private async Task ExportLogsAsync(CancellationToken cancellationToken)
    {
        if (_logExporter is null)
        {
            SetLogActionFeedback("当前环境未提供日志导出服务。", isError: true);
            return;
        }

        var entries = EnumerateVisibleLogs();
        var text = BuildLogText(entries);
        var fileName = $"linkforge-log-{DateTime.Now:yyyyMMdd-HHmmss}.log";

        try
        {
            var path = await _logExporter
                .ExportAsync(fileName, text, cancellationToken)
                .ConfigureAwait(true);

            if (path is null)
            {
                SetLogActionFeedback("已取消导出日志。", isError: false);
                return;
            }

            SetLogActionFeedback($"已导出 {entries.Count} 行脱敏日志：{path}", isError: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetLogActionFeedback("已取消导出日志。", isError: false);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException)
        {
            SetLogActionFeedback($"导出日志失败：{ex.GetType().Name} — {ex.Message}", isError: true);
        }
    }

    partial void OnLogSearchTextChanged(string value) => RefreshLogFilterState();

    partial void OnLogMinimumLevelChanged(LfLogLevel? value) => RefreshLogFilterState();

    partial void OnLogActionFeedbackChanged(string value) =>
        OnPropertyChanged(nameof(LogPanelStatus));

    partial void OnHasLogActionErrorChanged(bool value) =>
        OnPropertyChanged(nameof(LogPanelStatus));

    private void AttachLogSource(ObservableCollection<LogEntry>? source)
    {
        if (source is null)
        {
            return;
        }

        if (_structuredLogs is not null)
        {
            _structuredLogs.Attach(source);
            return;
        }

        foreach (var entry in source)
        {
            _localLogs.Add(entry);
        }

        source.CollectionChanged += OnLocalLogSourceChanged;
    }

    private void OnLocalLogSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (var entry in e.NewItems.OfType<LogEntry>())
        {
            _localLogs.Add(entry);
        }
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshLogFilterState();

    private void OnStructuredLogsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IStructuredLogService.StorageError))
        {
            OnPropertyChanged(nameof(LogStorageError));
            OnPropertyChanged(nameof(LogPanelStatus));
        }
    }

    private void RefreshLogFilterState()
    {
        OnPropertyChanged(nameof(HasVisibleLogs));
        CopyLogsCommand.NotifyCanExecuteChanged();
        ExportLogsCommand.NotifyCanExecuteChanged();
    }

    private IReadOnlyList<LogEntry> EnumerateVisibleLogs()
    {
        IEnumerable<LogEntry> query = LogEntries;
        var minimum = LogMinimumLevel;
        var search = LogSearchText.Trim();

        if (minimum is { } level)
        {
            query = query.Where(entry => entry.Level >= level);
        }

        if (search.Length > 0)
        {
            query = query.Where(entry =>
                entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Module.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.RunId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.TargetText.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.EventId.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return query.ToList();
    }

    private static string BuildLogText(IReadOnlyList<LogEntry> entries) =>
        entries.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, entries.Select(static entry => entry.ToPlainText()))
                + Environment.NewLine;

    private void SetLogActionFeedback(string message, bool isError)
    {
        LogActionFeedback = message;
        HasLogActionError = isError;
    }

    private void PublishShellLog(LfLogLevel level, string eventId, string message)
    {
        var entry = new LogEntry(
            DateTimeOffset.Now,
            level,
            "Shell",
            string.Empty,
            null,
            message,
            eventId);

        if (_structuredLogs is not null)
        {
            _structuredLogs.Publish(entry);
            return;
        }

        _localLogs.Add(entry);
    }

    /// <summary>刷新状态栏文本。</summary>
    private void UpdateStatusText()
    {
        StatusText = $"当前模块：{CurrentTitle} · 主题：{ThemeText}";
    }
}
