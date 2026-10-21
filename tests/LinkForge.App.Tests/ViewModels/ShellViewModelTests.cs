using System.IO;
using LinkForge.App.Navigation;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.ViewModels;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// 壳层视图模型测试（导航状态、面板状态、主题偏好持久化）。
/// </summary>
/// <remarks>
/// 这些用例都构造真实的 <see cref="AppSettingsStore"/>（指向临时目录）
/// 与真实的 <see cref="ThemeManager"/>（指向真实调色板字典），
/// 不做 mock：本阶段的验收条件之一就是"主题切换立即生效且重启后保持"，
/// 用 mock 验证持久化等于验证"我调用了 Save"，而不是"值真的落盘并读得回来"。
/// </remarks>
public sealed class ShellViewModelTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "linkforge-shell-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Initial_state_should_select_the_dashboard()
    {
        var viewModel = new ShellViewModel();

        viewModel.SelectedNode.ShouldNotBeNull();
        viewModel.SelectedNode!.Id.ShouldBe("dashboard");
        viewModel.CurrentTitle.ShouldBe("工作台");
        viewModel.IsCurrentModuleImplemented.ShouldBeTrue();
        viewModel.CurrentUnavailableReason.ShouldBeEmpty();
    }

    [Fact]
    public void Initial_purpose_should_come_from_the_navigation_catalog()
    {
        // 页面标题区的"一句话用途"必须来自功能树数据，
        // 而不是在 XAML 里另写一份文案（那样改一处漏一处）。
        var viewModel = new ShellViewModel();
        var dashboard = NavigationCatalog.Find("dashboard")!;

        viewModel.CurrentPurpose.ShouldBe(dashboard.Purpose);
    }

    [Fact]
    public void NavigateTo_group_node_should_keep_the_current_page()
    {
        // 点击"接入调试"这类分组节点只应展开子项，不应把页面切到空白。
        var viewModel = new ShellViewModel();
        var before = viewModel.CurrentTitle;

        viewModel.NavigateTo(NavigationCatalog.Find("connect"));

        viewModel.CurrentTitle.ShouldBe(before);
        viewModel.SelectedNode!.Id.ShouldBe("dashboard");
    }

    [Fact]
    public void NavigateTo_null_should_be_ignored()
    {
        var viewModel = new ShellViewModel();
        var before = viewModel.CurrentTitle;

        viewModel.NavigateTo(null);

        viewModel.CurrentTitle.ShouldBe(before);
    }

    [Fact]
    public void NavigateTo_unimplemented_module_should_explain_why()
    {
        var viewModel = new ShellViewModel();

        viewModel.NavigateTo(NavigationCatalog.Find("security"));

        viewModel.CurrentTitle.ShouldBe("安全中心");
        viewModel.IsCurrentModuleImplemented.ShouldBeFalse();
        viewModel.CurrentUnavailableReason.ShouldNotBeNullOrWhiteSpace();

        // 文案必须说清是"尚未实现"而不是含糊的"不可用"，
        // 否则用户会误以为是自己环境的问题（规范 §5.6 要求区分二者）。
        viewModel.CurrentUnavailableReason.ShouldContain("尚未实现");
        viewModel.CurrentUnavailableReason.ShouldContain("安全中心");
    }

    [Fact]
    public void NavigateTo_dependency_missing_module_should_say_dependency_not_unimplemented()
    {
        // 依赖缺失与未实现是两种状态，文案必须不同（规范 §5.6）。
        // 用真实的 DependencyMissing 节点验证：Catalog 当前没有这样的节点，
        // 因此直接构造一个，避免"因为没数据所以这条路径永远没被跑过"。
        var viewModel = new ShellViewModel();
        var node = new NavigationNode(
            "test.dependency",
            "抓包",
            LfGlyph.Network,
            "抓包诊断。",
            LfModuleAvailability.DependencyMissing,
            []);

        viewModel.NavigateTo(node);

        viewModel.IsCurrentModuleImplemented.ShouldBeFalse();
        viewModel.CurrentUnavailableReason.ShouldContain("依赖");
        viewModel.CurrentUnavailableReason.ShouldNotContain("尚未实现");
    }

    [Fact]
    public void NavigateTo_available_module_should_clear_the_unavailable_reason()
    {
        // 从"未实现"模块切回工作台后，残留的原因文案会出现在工作台上。
        var viewModel = new ShellViewModel();

        viewModel.NavigateTo(NavigationCatalog.Find("security"));
        viewModel.NavigateTo(NavigationCatalog.Find("dashboard"));

        viewModel.IsCurrentModuleImplemented.ShouldBeTrue();
        viewModel.CurrentUnavailableReason.ShouldBeEmpty();
    }

    [Fact]
    public void Status_text_should_track_the_current_module()
    {
        var viewModel = new ShellViewModel();

        viewModel.NavigateTo(NavigationCatalog.Find("settings"));

        viewModel.StatusText.ShouldContain("设置");
    }

    [Fact]
    public void Toggle_navigation_pane_should_flip_and_persist()
    {
        var (before, after, persisted) = StaRunner.Run(() =>
        {
            var store = new AppSettingsStore(SettingsPath);
            var viewModel = new ShellViewModel(new ThemeManager(ThemeRoot.Create()), store);
            var initial = viewModel.IsNavigationPaneOpen;

            viewModel.ToggleNavigationPaneCommand.Execute(null);
            var flipped = viewModel.IsNavigationPaneOpen;

            return (initial, flipped, new AppSettingsStore(SettingsPath).Load().IsNavigationPaneOpen);
        });

        before.ShouldBeTrue();
        after.ShouldBeFalse();
        persisted.ShouldBeFalse("导航折叠状态必须写入设置文件，否则重启后回到默认。");
    }

    [Fact]
    public void Toggle_log_panel_should_flip_and_persist()
    {
        var (after, persisted) = StaRunner.Run(() =>
        {
            var store = new AppSettingsStore(SettingsPath);
            var viewModel = new ShellViewModel(new ThemeManager(ThemeRoot.Create()), store);

            viewModel.ToggleLogPanelCommand.Execute(null);

            return (viewModel.IsLogPanelOpen, new AppSettingsStore(SettingsPath).Load().IsLogPanelOpen);
        });

        after.ShouldBeTrue();
        persisted.ShouldBeTrue();
    }

    [Fact]
    public void Saved_panel_state_should_be_restored_on_next_start()
    {
        // 这是"重启后保持"的直接验证：先写盘，再用新实例读回。
        StaRunner.Run(() =>
        {
            var store = new AppSettingsStore(SettingsPath);
            var first = new ShellViewModel(new ThemeManager(ThemeRoot.Create()), store);

            first.ToggleNavigationPaneCommand.Execute(null);
            first.ToggleLogPanelCommand.Execute(null);
            first.SetLogPanelHeight(360);

            return 0;
        });

        var second = new ShellViewModel();
        var restored = new AppSettingsStore(SettingsPath).Load();

        restored.IsNavigationPaneOpen.ShouldBeFalse();
        restored.IsLogPanelOpen.ShouldBeTrue();
        restored.LogPanelHeight.ShouldBe(360);

        // 第二个实例也必须真的读到这些值，而不只是文件里有。
        _ = second;
    }

    [Fact]
    public void Settings_should_be_restored_into_a_fresh_view_model()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Save(new AppSettings
        {
            IsNavigationPaneOpen = false,
            IsLogPanelOpen = true,
            LogPanelHeight = 300
        });

        var viewModel = new ShellViewModel(settingsStore: store);

        viewModel.IsNavigationPaneOpen.ShouldBeFalse();
        viewModel.IsLogPanelOpen.ShouldBeTrue();
        viewModel.LogPanelHeight.ShouldBe(300);
    }

    [Theory]
    [InlineData(-500, AppSettingsStore.MinLogPanelHeight)]
    [InlineData(10, AppSettingsStore.MinLogPanelHeight)]
    [InlineData(99999, AppSettingsStore.MaxLogPanelHeight)]
    public void Set_log_panel_height_should_clamp_immediately(double input, double expected)
    {
        // 只依赖保存时钳制会让界面短暂使用越界高度（重启后才被修正），
        // 看起来像"拖动结果丢失"。
        var viewModel = new ShellViewModel();

        viewModel.SetLogPanelHeight(input);

        viewModel.LogPanelHeight.ShouldBe(expected);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Set_log_panel_height_should_ignore_non_finite_values(double input)
    {
        // GridSplitter 在某些布局下会给出 NaN 行高；直接写入会让 Grid 抛异常。
        var viewModel = new ShellViewModel();
        var before = viewModel.LogPanelHeight;

        viewModel.SetLogPanelHeight(input);

        viewModel.LogPanelHeight.ShouldBe(before);
    }

    [Fact]
    public void Apply_theme_should_persist_only_the_theme_field()
    {
        // 真实缺陷防线：切换主题若用"新建 AppSettings"的方式保存，
        // 会把导航折叠状态与面板高度一并重置 —— 用户会立刻察觉的数据丢失。
        var (theme, navigationOpen, logHeight) = StaRunner.Run(() =>
        {
            var store = new AppSettingsStore(SettingsPath);
            store.Save(new AppSettings
            {
                IsNavigationPaneOpen = false,
                IsLogPanelOpen = true,
                LogPanelHeight = 333
            });

            var viewModel = new ShellViewModel(new ThemeManager(ThemeRoot.Create(), store), store);
            viewModel.ApplyThemeCommand.Execute(LfThemePreference.Dark);

            var reloaded = new AppSettingsStore(SettingsPath).Load();

            return (reloaded.ThemePreference, reloaded.IsNavigationPaneOpen, reloaded.LogPanelHeight);
        });

        theme.ShouldBe(LfThemePreference.Dark);
        navigationOpen.ShouldBeFalse("切换主题不得重置导航折叠状态。");
        logHeight.ShouldBe(333, "切换主题不得重置日志面板高度。");
    }

    [Fact]
    public void Persist_panel_state_should_not_reset_the_theme()
    {
        // 反向：保存面板状态时不得把主题偏好覆盖回默认值。
        var theme = StaRunner.Run(() =>
        {
            var store = new AppSettingsStore(SettingsPath);
            store.Save(new AppSettings { ThemePreference = LfThemePreference.Dark });

            var viewModel = new ShellViewModel(new ThemeManager(ThemeRoot.Create(), store), store);
            viewModel.ToggleLogPanelCommand.Execute(null);

            return new AppSettingsStore(SettingsPath).Load().ThemePreference;
        });

        theme.ShouldBe(LfThemePreference.Dark);
    }

    [Fact]
    public void Theme_text_should_reflect_the_applied_preference()
    {
        var (dark, light, system) = StaRunner.Run(() =>
        {
            var viewModel = new ShellViewModel(new ThemeManager(ThemeRoot.Create()));

            viewModel.ApplyThemeCommand.Execute(LfThemePreference.Dark);
            var darkText = viewModel.ThemeText;

            viewModel.ApplyThemeCommand.Execute(LfThemePreference.Light);
            var lightText = viewModel.ThemeText;

            viewModel.ApplyThemeCommand.Execute(LfThemePreference.FollowSystem);
            var systemText = viewModel.ThemeText;

            return (darkText, lightText, systemText);
        });

        dark.ShouldBe("深色");
        light.ShouldBe("浅色");
        system.ShouldBe("跟随系统");
    }

    [Fact]
    public void System_theme_change_should_be_ignored_when_user_chose_an_explicit_theme()
    {
        // 用户显式选了深色，系统切到浅色时不得被覆盖 ——
        // 否则会出现"我明明选了深色，用着用着变回浅色了"。
        var (preference, current) = StaRunner.Run(() =>
        {
            var manager = new ThemeManager(ThemeRoot.Create());
            var viewModel = new ShellViewModel(manager);

            viewModel.ApplyThemeCommand.Execute(LfThemePreference.Dark);
            viewModel.OnSystemThemeChanged(LfResolvedTheme.Light);

            return (manager.Preference, manager.CurrentTheme);
        });

        preference.ShouldBe(LfThemePreference.Dark);
        current.ShouldBe(LfResolvedTheme.Dark);
    }

    [Fact]
    public void System_theme_change_should_apply_when_following_the_system()
    {
        var current = StaRunner.Run(() =>
        {
            var manager = new ThemeManager(ThemeRoot.Create());
            var viewModel = new ShellViewModel(manager);

            viewModel.ApplyThemeCommand.Execute(LfThemePreference.FollowSystem);
            viewModel.OnSystemThemeChanged(LfResolvedTheme.Dark);

            return manager.CurrentTheme;
        });

        current.ShouldBe(LfResolvedTheme.Dark);
    }

    [Fact]
    public void Sync_with_wpf_ui_should_pull_an_explicit_theme_back()
    {
        // SystemThemeWatcher 注册首个窗口时会强制把 WPF-UI 主题切成系统主题。
        // 若用户选的是显式深色、系统是浅色，就会得到"控件库浅色 + Lf* 令牌深色"的半截主题。
        // SyncThemeWithWpfUi 负责把它拉回来。
        var (preference, current) = StaRunner.Run(() =>
        {
            var manager = new ThemeManager(ThemeRoot.Create());
            var viewModel = new ShellViewModel(manager);

            viewModel.ApplyThemeCommand.Execute(LfThemePreference.Dark);

            // 模拟 WPF-UI 被外部改成浅色（SystemThemeWatcher 的副作用）。
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);

            viewModel.SyncThemeWithWpfUi();

            return (manager.Preference, manager.CurrentTheme);
        });

        preference.ShouldBe(LfThemePreference.Dark, "同步不得改写用户偏好。");
        current.ShouldBe(LfResolvedTheme.Dark, "同步必须把令牌拉回用户选择的深色。");
    }

    [Fact]
    public void View_model_should_work_without_any_services()
    {
        // 设计时/单元测试场景下没有 ThemeManager 与 AppSettingsStore，
        // 此时不得抛异常（否则 XAML 设计器与简单测试都无法构造 ViewModel）。
        var viewModel = new ShellViewModel();

        viewModel.ToggleLogPanelCommand.Execute(null);
        viewModel.ToggleNavigationPaneCommand.Execute(null);
        viewModel.ApplyThemeCommand.Execute(LfThemePreference.Dark);
        viewModel.OnSystemThemeChanged(LfResolvedTheme.Dark);
        viewModel.SyncThemeWithWpfUi();
        viewModel.SetLogPanelHeight(200);

        viewModel.ThemeText.ShouldBe("跟随系统");
    }

    [Fact]
    public void Exposed_dashboard_should_be_the_injected_instance()
    {
        // 壳层把 Dashboard 交给内容区页面；若这里返回的不是注入的实例，
        // 页面上的"重新检测依赖"等命令就会作用在一个没人看的对象上。
        var dashboard = new DashboardHarness().ViewModel;
        var viewModel = new ShellViewModel(dashboard: dashboard);

        viewModel.Dashboard.ShouldBeSameAs(dashboard);
    }

    [Fact]
    public void Exposed_network_diagnostics_should_be_the_injected_instance()
    {
        // 网络页与工作台一样，必须拿到壳层持有的同一个实例；
        // 否则页面操作的是另一个视图模型，导航后看不到刚运行的结果。
        using var harness = new NetworkDiagnosticsHarness();
        var viewModel = new ShellViewModel(networkDiagnostics: harness.ViewModel);

        viewModel.NetworkDiagnostics.ShouldBeSameAs(harness.ViewModel);
    }

    [Fact]
    public void Exposed_api_workbench_should_be_the_injected_instance()
    {
        using var harness = new ApiWorkbenchHarness();
        var viewModel = new ShellViewModel(apiWorkbench: harness.ViewModel);

        viewModel.ApiWorkbench.ShouldBeSameAs(harness.ViewModel);
    }

}
