using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkForge.App.Navigation;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.Tests.ViewModels;
using LinkForge.App.ViewModels;
using LinkForge.App.Views;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 应用壳层的真实渲染与交互测试（UI 规范 §3.1 / §3.2）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="RenderSmokeTests"/> 的分工：冒烟测试只断言“不抛异常”，
/// 这里进一步断言“内容真的绑上了、导航真的能切、窗口真的能关”。
/// 全部元素都空白或导航点了没反应的窗口同样能通过冒烟测试，但产品是坏的。
/// </para>
/// <para>
/// 阶段 03 实施期间由本类捕获的真实缺陷：
/// </para>
/// <list type="number">
///   <item>
///     <b>关闭窗口必崩。</b><c>SystemThemeWatcher.UnWatch</c> 在 <c>Closed</c> 阶段调用，
///     而那时 HWND 已销毁，控件库抛 <c>InvalidOperationException("Could not get window handle.")</c>。
///     单次关闭表现为退出码异常，反复开关则直接让测试宿主进程崩溃。
///   </item>
///   <item>
///     <b>工作台一片空白。</b><c>DashboardPage</c> 的 <c>DataContext</c> 继承了窗口的
///     <see cref="ShellViewModel"/>，页面上所有绑定静默落空（无异常、无日志）。
///   </item>
///   <item>
///     <b>“重新检测依赖”按钮点了没反应。</b>XAML 绑定 <c>RefreshDependenciesCommand</c>，
///     但 ViewModel 里的方法没有 <c>[RelayCommand]</c> 特性，命令根本不存在。
///   </item>
/// </list>
/// </remarks>
public class ShellWindowTests
{
    // 与规范 §3.1 的参考分辨率一致。
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Shell_window_should_render_at_the_reference_resolution()
    {
        var report = StaRunner.Run(() => RenderShell(ReferenceWidth, ReferenceHeight));

        report.VisualCount.ShouldBeGreaterThan(0, "壳层视觉树为空。");
        report.OpaquePixelRatio.ShouldBeGreaterThan(
            0.5,
            "窗口几乎全透明，说明内容没有被真正绘制。");
    }

    [Fact]
    public void Shell_window_should_render_at_the_minimum_supported_size()
    {
        // 规范 §3.1 要求 1366x768 可完成核心操作、关键按钮不被裁切。
        // 这里用窗口最小尺寸再验一次，确保布局在更小窗口下同样能画出来。
        var report = StaRunner.Run(() => RenderShell(1024, 640));

        report.VisualCount.ShouldBeGreaterThan(0);
        report.OpaquePixelRatio.ShouldBeGreaterThan(0.5);
    }

    [Fact]
    public void Closing_the_window_should_not_throw()
    {
        // 真实缺陷回归守卫：关闭路径曾因 SystemThemeWatcher.UnWatch 抛异常，
        // 单次关闭表现为非零退出码，反复开关会直接让进程崩溃。
        //
        // 关闭动作放在 Loaded 之后、Dispatcher 队列跑完之后，而不是在 Loaded 处理函数里
        // 同步调用 Close()：后者是在窗口"尚未完成创建"的过程中销毁窗口，
        // WPF 自身会因控件库的窗口铬（WindowChrome）初始化未完成而抛 NullReferenceException，
        // 与本次要验证的 UnWatch 缺陷无关，属于测试写法造成的假阳性。
        // 真实用户的关闭动作总是发生在窗口稳定显示之后。
        var failure = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var window = new ShellWindow(new ShellViewModel());
            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            try
            {
                window.Close();
                PumpDispatcher();
                return default(Exception);
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        failure.ShouldBeNull($"关闭壳层窗口时不得抛异常，实际为：{failure?.Message}");
    }

    [Fact]
    public void Opening_and_closing_repeatedly_should_not_crash()
    {
        // 单次关闭可能“看起来没事”（异常被 WPF 吞进 Dispatcher），
        // 反复开关才会暴露句柄与事件钩子的泄漏。真实缺陷就是这样被发现的。
        var failures = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var collected = new List<string>();

            for (var i = 0; i < 3; i++)
            {
                try
                {
                    var window = new ShellWindow(new ShellViewModel());
                    window.Show();
                    PumpDispatcher();
                    window.Close();
                    PumpDispatcher();
                }
                catch (Exception ex)
                {
                    collected.Add($"第 {i + 1} 次：{ex.GetType().Name}: {ex.Message}");
                }
            }

            return collected;
        });

        failures.ShouldBeEmpty("反复打开/关闭壳层窗口不应失败：" + string.Join("; ", failures));
    }

    [Fact]
    public void Dashboard_page_should_receive_its_own_view_model()
    {
        // 真实缺陷回归守卫：DashboardPage 的 DataContext 曾继承窗口的 ShellViewModel，
        // 导致页面上所有绑定静默落空（不报错、只是空白）。
        var (dataContext, visibility) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var dashboard = new DashboardHarness().ViewModel;
            var window = new ShellWindow(new ShellViewModel(dashboard: dashboard));

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            var page = (FrameworkElement)window.FindName("DashboardContent")!;
            var result = (page.DataContext, page.Visibility);

            window.Close();
            PumpDispatcher();

            return result;
        });

        dataContext.ShouldBeOfType<DashboardViewModel>();
        visibility.ShouldBe(Visibility.Visible);
    }

    [Fact]
    public void Dashboard_should_be_hidden_when_an_unimplemented_module_is_selected()
    {
        // 工作台与“未实现”占位必须互斥：同时可见会让用户以为模块已经能用。
        var (dashboard, placeholder) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("security"));
            PumpDispatcher();
            window.UpdateLayout();

            var dashboardPage = (FrameworkElement)window.FindName("DashboardContent")!;
            var emptyState = (FrameworkElement)window.FindName("NotImplementedContent")!;
            var result = (dashboardPage.Visibility, emptyState.Visibility);

            window.Close();
            PumpDispatcher();

            return result;
        });

        dashboard.ShouldBe(Visibility.Collapsed);
        placeholder.ShouldBe(Visibility.Visible);
    }

    [Fact]
    public void Selecting_network_should_show_the_network_page_and_hide_dashboard()
    {
        var (dashboard, network, placeholder, dataContext) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var networkHarness = new NetworkDiagnosticsHarness();
            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel,
                networkDiagnostics: networkHarness.ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("network"));
            PumpDispatcher();
            window.UpdateLayout();

            var dashboardPage = (FrameworkElement)window.FindName("DashboardContent")!;
            var networkPage = (FrameworkElement)window.FindName("NetworkDiagnosticsContent")!;
            var emptyState = (FrameworkElement)window.FindName("NotImplementedContent")!;
            var result = (
                dashboardPage.Visibility,
                networkPage.Visibility,
                emptyState.Visibility,
                networkPage.DataContext);

            window.Close();
            PumpDispatcher();

            return result;
        });

        dashboard.ShouldBe(Visibility.Collapsed);
        network.ShouldBe(Visibility.Visible);
        placeholder.ShouldBe(Visibility.Collapsed);
        dataContext.ShouldBeOfType<NetworkDiagnosticsViewModel>();
    }

    [Fact]
    public void Selecting_api_workbench_should_show_the_api_page_and_hide_other_pages()
    {
        var (dashboard, network, api, placeholder, dataContext) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var networkHarness = new NetworkDiagnosticsHarness();
            using var apiHarness = new ApiWorkbenchHarness();
            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel,
                networkDiagnostics: networkHarness.ViewModel,
                apiWorkbench: apiHarness.ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("connect.api"));
            PumpDispatcher();
            window.UpdateLayout();

            var dashboardPage = (FrameworkElement)window.FindName("DashboardContent")!;
            var networkPage = (FrameworkElement)window.FindName("NetworkDiagnosticsContent")!;
            var apiPage = (FrameworkElement)window.FindName("ApiWorkbenchContent")!;
            var emptyState = (FrameworkElement)window.FindName("NotImplementedContent")!;
            var result = (
                dashboardPage.Visibility,
                networkPage.Visibility,
                apiPage.Visibility,
                emptyState.Visibility,
                apiPage.DataContext);

            window.Close();
            PumpDispatcher();

            return result;
        });

        dashboard.ShouldBe(Visibility.Collapsed);
        network.ShouldBe(Visibility.Collapsed);
        api.ShouldBe(Visibility.Visible);
        placeholder.ShouldBe(Visibility.Collapsed);
        dataContext.ShouldBeOfType<ApiWorkbenchViewModel>();
    }

    [Fact]
    public void Selecting_iot_should_show_the_mqtt_page_and_hide_other_pages()
    {
        var (dashboard, network, api, mqtt, placeholder, dataContext) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var networkHarness = new NetworkDiagnosticsHarness();
            using var apiHarness = new ApiWorkbenchHarness();
            using var mqttHarness = new MqttWorkbenchHarness();
            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel,
                networkDiagnostics: networkHarness.ViewModel,
                apiWorkbench: apiHarness.ViewModel,
                mqttWorkbench: mqttHarness.ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("connect.iot"));
            PumpDispatcher();
            window.UpdateLayout();

            var dashboardPage = (FrameworkElement)window.FindName("DashboardContent")!;
            var networkPage = (FrameworkElement)window.FindName("NetworkDiagnosticsContent")!;
            var apiPage = (FrameworkElement)window.FindName("ApiWorkbenchContent")!;
            var mqttPage = (FrameworkElement)window.FindName("MqttWorkbenchContent")!;
            var emptyState = (FrameworkElement)window.FindName("NotImplementedContent")!;
            var result = (
                dashboardPage.Visibility,
                networkPage.Visibility,
                apiPage.Visibility,
                mqttPage.Visibility,
                emptyState.Visibility,
                mqttPage.DataContext);

            window.Close();
            PumpDispatcher();

            return result;
        });

        dashboard.ShouldBe(Visibility.Collapsed);
        network.ShouldBe(Visibility.Collapsed);
        api.ShouldBe(Visibility.Collapsed);
        mqtt.ShouldBe(Visibility.Visible);
        placeholder.ShouldBe(Visibility.Collapsed);
        dataContext.ShouldBeOfType<MqttWorkbenchViewModel>();
    }

    [Fact]
    public void Selecting_video_should_show_the_rtsp_page_and_hide_other_pages()
    {
        var (dashboard, network, api, mqtt, rtsp, placeholder, dataContext) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var networkHarness = new NetworkDiagnosticsHarness();
            using var apiHarness = new ApiWorkbenchHarness();
            using var mqttHarness = new MqttWorkbenchHarness();
            using var rtspHarness = new RtspDiagnosticsHarness();
            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel,
                networkDiagnostics: networkHarness.ViewModel,
                apiWorkbench: apiHarness.ViewModel,
                mqttWorkbench: mqttHarness.ViewModel,
                rtspDiagnostics: rtspHarness.ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("connect.video"));
            PumpDispatcher();
            window.UpdateLayout();

            var result = (
                ((FrameworkElement)window.FindName("DashboardContent")!).Visibility,
                ((FrameworkElement)window.FindName("NetworkDiagnosticsContent")!).Visibility,
                ((FrameworkElement)window.FindName("ApiWorkbenchContent")!).Visibility,
                ((FrameworkElement)window.FindName("MqttWorkbenchContent")!).Visibility,
                ((FrameworkElement)window.FindName("RtspDiagnosticsContent")!).Visibility,
                ((FrameworkElement)window.FindName("NotImplementedContent")!).Visibility,
                ((FrameworkElement)window.FindName("RtspDiagnosticsContent")!).DataContext);

            window.Close();
            PumpDispatcher();
            return result;
        });

        dashboard.ShouldBe(Visibility.Collapsed);
        network.ShouldBe(Visibility.Collapsed);
        api.ShouldBe(Visibility.Collapsed);
        mqtt.ShouldBe(Visibility.Collapsed);
        rtsp.ShouldBe(Visibility.Visible);
        placeholder.ShouldBe(Visibility.Collapsed);
        dataContext.ShouldBeOfType<RtspDiagnosticsViewModel>();
    }

    [Fact]
    public void Selecting_reports_should_show_the_report_page_and_hide_other_pages()
    {
        var (dashboard, network, rtsp, report, placeholder, dataContext) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var networkHarness = new NetworkDiagnosticsHarness();
            using var rtspHarness = new RtspDiagnosticsHarness();
            using var reportHarness = new ReportPageHarness();
            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel,
                networkDiagnostics: networkHarness.ViewModel,
                rtspDiagnostics: rtspHarness.ViewModel,
                reportPage: reportHarness.ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("reports"));
            PumpDispatcher();
            window.UpdateLayout();

            var result = (
                ((FrameworkElement)window.FindName("DashboardContent")!).Visibility,
                ((FrameworkElement)window.FindName("NetworkDiagnosticsContent")!).Visibility,
                ((FrameworkElement)window.FindName("RtspDiagnosticsContent")!).Visibility,
                ((FrameworkElement)window.FindName("ReportContent")!).Visibility,
                ((FrameworkElement)window.FindName("NotImplementedContent")!).Visibility,
                ((FrameworkElement)window.FindName("ReportContent")!).DataContext);

            window.Close();
            PumpDispatcher();
            return result;
        });

        dashboard.ShouldBe(Visibility.Collapsed);
        network.ShouldBe(Visibility.Collapsed);
        rtsp.ShouldBe(Visibility.Collapsed);
        report.ShouldBe(Visibility.Visible);
        placeholder.ShouldBe(Visibility.Collapsed);
        dataContext.ShouldBeOfType<ReportPageViewModel>();
    }

    [Fact]
    public void Selecting_developer_tools_should_show_the_page_and_hide_other_pages()
    {
        var harness = new DeveloperToolsHarness();
        var (developer, dashboard, report, placeholder, dataContext) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel,
                reportPage: new ReportPageHarness().ViewModel,
                developerTools: harness.ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("developer"));
            PumpDispatcher();
            window.UpdateLayout();

            var result = (
                ((FrameworkElement)window.FindName("DeveloperToolsContent")!).Visibility,
                ((FrameworkElement)window.FindName("DashboardContent")!).Visibility,
                ((FrameworkElement)window.FindName("ReportContent")!).Visibility,
                ((FrameworkElement)window.FindName("NotImplementedContent")!).Visibility,
                ((FrameworkElement)window.FindName("DeveloperToolsContent")!).DataContext);

            window.Close();
            PumpDispatcher();
            return result;
        });

        developer.ShouldBe(Visibility.Visible);
        dashboard.ShouldBe(Visibility.Collapsed);
        report.ShouldBe(Visibility.Collapsed);
        placeholder.ShouldBe(Visibility.Collapsed);
        dataContext.ShouldBeOfType<DeveloperToolsViewModel>();
    }

    [Fact]
    public void Selecting_asset_catalog_should_show_the_page_and_hide_other_pages()
    {
        // 资产目录挂在一级导航下：既要用真实 ViewModel 作为 DataContext，
        // 也必须验证工作台被收起 —— 否则用户会同时看到两套互相矛盾的内容。
        var harness = new AssetCatalogHarness();
        var (assets, dashboard, placeholder, dataContext) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel,
                assetCatalog: harness.ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("assets"));
            PumpDispatcher();
            window.UpdateLayout();

            var result = (
                ((FrameworkElement)window.FindName("AssetCatalogContent")!).Visibility,
                ((FrameworkElement)window.FindName("DashboardContent")!).Visibility,
                ((FrameworkElement)window.FindName("NotImplementedContent")!).Visibility,
                ((FrameworkElement)window.FindName("AssetCatalogContent")!).DataContext);

            window.Close();
            PumpDispatcher();
            return result;
        });

        assets.ShouldBe(Visibility.Visible);
        dashboard.ShouldBe(Visibility.Collapsed);
        placeholder.ShouldBe(Visibility.Collapsed);
        dataContext.ShouldBeOfType<AssetCatalogViewModel>();
    }

    [Fact]
    public void Selecting_plugin_manager_should_show_the_page_and_hide_other_pages()
    {
        // 插件管理挂在"设置"分组下：必须验证二级项导航真的把页面切出来，
        // 而不是只改高亮 —— 历史缺陷是 DataContext 未指向页面视图模型，
        // 结果是一个只有标题的空白页，且没有任何报错。
        var harness = new PluginManagerHarness();
        var (plugins, dashboard, developer, placeholder, dataContext) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel,
                developerTools: new DeveloperToolsHarness().ViewModel,
                pluginManager: harness.ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("settings.plugins"));
            PumpDispatcher();
            window.UpdateLayout();

            var result = (
                ((FrameworkElement)window.FindName("PluginManagerContent")!).Visibility,
                ((FrameworkElement)window.FindName("DashboardContent")!).Visibility,
                ((FrameworkElement)window.FindName("DeveloperToolsContent")!).Visibility,
                ((FrameworkElement)window.FindName("NotImplementedContent")!).Visibility,
                ((FrameworkElement)window.FindName("PluginManagerContent")!).DataContext);

            window.Close();
            PumpDispatcher();
            return result;
        });

        plugins.ShouldBe(Visibility.Visible);
        dashboard.ShouldBe(Visibility.Collapsed);
        developer.ShouldBe(Visibility.Collapsed);
        placeholder.ShouldBe(Visibility.Collapsed);
        dataContext.ShouldBeOfType<PluginManagerViewModel>();
    }

    [Fact]
    public void Settings_group_node_should_not_navigate_to_a_blank_page()
    {
        // "设置"现在带二级项，是分组节点：直接导航它必须被忽略，
        // 否则用户点一下就看到一个没有任何内容的占位页。
        var (pageId, placeholder) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewModel = new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel,
                pluginManager: new PluginManagerHarness().ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            viewModel.NavigateTo(NavigationCatalog.Find("settings"));
            PumpDispatcher();
            window.UpdateLayout();

            var result = (
                viewModel.CurrentPageId,
                ((FrameworkElement)window.FindName("DashboardContent")!).Visibility);

            window.Close();
            PumpDispatcher();
            return result;
        });

        pageId.ShouldBe("dashboard", "分组节点不应改变当前页面。");
        placeholder.ShouldBe(Visibility.Visible, "当前页仍是工作台，占位状态不该出现。");
    }

    [Fact]
    public void Selecting_a_group_node_should_not_leave_it_highlighted()
    {
        // 真实缺陷回归守卫：TreeView 自己维护选中状态，ViewModel 拦不住它，
        // 于是会出现“高亮停在接入调试、内容区显示工作台”的分裂状态。
        var (selectedId, currentTitle, groupSelected) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewModel = new ShellViewModel(dashboard: new DashboardHarness().ViewModel);
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            var tree = (TreeView)window.FindName("NavigationTree")!;
            var groupContainer = FindContainer(tree, NavigationCatalog.Find("connect")!);

            groupContainer.ShouldNotBeNull("未找到“接入调试”的导航容器。");

            groupContainer!.IsSelected = true;
            PumpDispatcher();
            window.UpdateLayout();

            var result = (viewModel.SelectedNode?.Id, viewModel.CurrentTitle, groupContainer.IsSelected);

            window.Close();
            PumpDispatcher();

            return result;
        });

        selectedId.ShouldBe("dashboard");
        currentTitle.ShouldBe("工作台");
        groupSelected.ShouldBeFalse("分组节点不应停留在选中态。");
    }

    [Fact]
    public void Navigation_pane_width_should_come_from_design_tokens()
    {
        // 宽度必须来自设计令牌而不是代码里的魔数：
        // 阶段 03 曾出现“令牌写 48、代码写 52”的两处各自维护。
        var (openWidth, compactWidth, tokenOpen, tokenCompact) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewModel = new ShellViewModel();
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            var pane = FindNavigationPane(window);
            var before = pane.Width;

            viewModel.ToggleNavigationPaneCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();

            var after = pane.Width;

            var result = (
                before,
                after,
                (double)System.Windows.Application.Current.Resources["LfNavWidth"],
                (double)System.Windows.Application.Current.Resources["LfNavWidthCompact"]);

            window.Close();
            PumpDispatcher();

            return result;
        });

        openWidth.ShouldBe(tokenOpen, "展开宽度必须等于 LfNavWidth 令牌。");
        compactWidth.ShouldBe(tokenCompact, "折叠宽度必须等于 LfNavWidthCompact 令牌。");
    }

    [Fact]
    public void Log_panel_should_take_no_vertical_space_while_collapsed()
    {
        // 折叠时若只隐藏 Border 而不把行高归零，会留下一条与面板等高的空白。
        var (collapsed, expanded) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var viewModel = new ShellViewModel();
            var window = new ShellWindow(viewModel);

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            var row = FindLogPanelRow(window);
            var before = row.Height.Value;

            viewModel.ToggleLogPanelCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();

            var after = row.Height.Value;

            window.Close();
            PumpDispatcher();

            return (before, after);
        });

        collapsed.ShouldBe(0, "日志面板折叠时行高必须为 0。");
        expanded.ShouldBeGreaterThanOrEqualTo(
            AppSettingsStore.MinLogPanelHeight,
            "日志面板展开时行高不得低于可读下限。");
    }

    [Fact]
    public void Icon_buttons_should_have_tooltips_and_accessible_names()
    {
        // 规范 §3.1 / §5.1：纯图标按钮必须有 ToolTip 与可访问名称，
        // 否则读屏用户与初次使用者都无法判断它做什么。
        var problems = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var window = new ShellWindow(new ShellViewModel());

            window.Show();
            PumpDispatcher();
            Layout(window, ReferenceWidth, ReferenceHeight);

            var collected = new List<string>();

            foreach (var button in FindDescendants<Button>(window))
            {
                var isIconOnly = button.Content is TextBlock
                    || (button.Content is string text && text.Length == 1);

                if (!isIconOnly)
                {
                    continue;
                }

                if (button.ToolTip is not string tip || string.IsNullOrWhiteSpace(tip))
                {
                    collected.Add($"{button.Name} 缺少 ToolTip");
                }

                if (string.IsNullOrWhiteSpace(
                        System.Windows.Automation.AutomationProperties.GetName(button)))
                {
                    collected.Add($"{button.Name} 缺少 AutomationProperties.Name");
                }
            }

            window.Close();
            PumpDispatcher();

            return collected;
        });

        problems.ShouldBeEmpty("图标按钮缺少可访问信息：" + string.Join("; ", problems));
    }

    [Fact]
    public void Shell_should_not_use_canvas_absolute_positioning()
    {
        // 规范 §3.1：禁止用固定绝对坐标构建整页。
        var xamlPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "LinkForge.App",
            "Views",
            "ShellWindow.xaml");

        File.ReadAllText(xamlPath).ShouldNotContain(
            "<Canvas",
            Case.Sensitive,
            "壳层不得使用 Canvas 绝对定位。");
    }

    /// <summary>
    /// 渲染壳层并返回视觉树规模与非透明像素占比。
    /// </summary>
    private static (int VisualCount, double OpaquePixelRatio) RenderShell(int width, int height)
    {
        ThemedHost.EnsureResources();

        var window = new ShellWindow(new ShellViewModel(
            dashboard: new DashboardHarness().ViewModel))
        {
            Width = width,
            Height = height
        };

        window.Show();
        PumpDispatcher();
        Layout(window, width, height);

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);

        // 必须在窗口仍然可见时渲染：Close() 之后视觉树被断开，只会得到全透明像素。
        bitmap.Render(window);

        var visualCount = CountVisuals(window);
        var opaqueRatio = OpaqueRatio(bitmap);

        window.Close();
        PumpDispatcher();

        return (visualCount, opaqueRatio);
    }

    private static double OpaqueRatio(System.Windows.Media.Imaging.RenderTargetBitmap bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var opaque = 0;

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 0)
            {
                opaque++;
            }
        }

        return (double)opaque / (bitmap.PixelWidth * bitmap.PixelHeight);
    }

    private static void Layout(Window window, int width, int height)
    {
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
    }

    /// <summary>
    /// 清空 Dispatcher 队列。
    /// </summary>
    /// <remarks>
    /// 只做 Measure/Arrange 不够：部分资源引用与绑定要等窗口真正进入
    /// 呈现阶段才求值。阶段 02 已用 Mutation 验证过这一点（见 RenderSmokeTests 注释）。
    /// </remarks>
    private static void PumpDispatcher()
    {
        for (var i = 0; i < 60; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
        }
    }

    private static int CountVisuals(DependencyObject root)
    {
        var count = 1;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            count += CountVisuals(VisualTreeHelper.GetChild(root, i));
        }

        return count;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, object target)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
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

    /// <summary>定位导航栏容器（承载 LfNavWidth 的那个 Border）。</summary>
    private static FrameworkElement FindNavigationPane(ShellWindow window)
    {
        var tree = (TreeView)window.FindName("NavigationTree")!;
        DependencyObject? current = VisualTreeHelper.GetParent(tree);

        while (current is not null)
        {
            if (current is Border border && !double.IsNaN(border.Width))
            {
                return border;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        throw new InvalidOperationException("未找到导航栏容器。");
    }

    /// <summary>定位日志面板所在 Grid 的行定义。</summary>
    private static RowDefinition FindLogPanelRow(ShellWindow window)
    {
        var splitter = (GridSplitter)window.FindName("LogPanelSplitter")!;
        var grid = (Grid)VisualTreeHelper.GetParent(splitter);

        // 日志面板是内容区 Grid 的第 3 行（标题 / 内容 / 分隔条 / 面板）。
        return grid.RowDefinitions[3];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LinkForge.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"未能从 {AppContext.BaseDirectory} 向上找到包含 LinkForge.slnx 的仓库根目录。");
    }
}
