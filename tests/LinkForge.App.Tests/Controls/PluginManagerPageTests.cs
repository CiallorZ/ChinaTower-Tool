using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkForge.App.Controls;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.Tests.ViewModels;
using LinkForge.App.Views.Pages;
using LinkForge.Domain.Diagnostics;
using LinkForge.Plugin.Abstractions;
using LinkForge.Plugin.Hosting;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 插件管理页的真实渲染与截图测试（阶段 05 / LF-012）。
/// </summary>
/// <remarks>
/// 这一页是"执行第三方可执行文件"的入口，界面上的空态/阻断/未验证都必须真的渲染出来：
/// 只看视图模型的布尔值不能证明 <c>EmptyState</c>、<c>ErrorPanel</c>、
/// 信任确认与阻断原因在屏幕上可见。
/// </remarks>
public class PluginManagerPageTests
{
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Plugin_page_should_survive_a_full_layout_pass()
    {
        var failure = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new PluginManagerHarness();
            var page = new PluginManagerPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            try
            {
                window.Show();
                Pump();
                Layout(window);

                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
            finally
            {
                window.Close();
                Pump();
            }
        });

        if (failure is not null)
        {
            throw new InvalidOperationException("插件管理页在加载/渲染阶段抛出异常。", failure);
        }
    }

    [Fact]
    public void Initial_state_should_show_a_real_empty_state_not_a_blank_panel()
    {
        var title = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new PluginManagerHarness();
            var page = new PluginManagerPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            var visible = FindDescendants<EmptyState>(page)
                .Where(static state => state.Visibility == Visibility.Visible)
                .ToArray();

            visible.ShouldNotBeEmpty("初始状态没有任何可见空状态，页面看起来像坏了。");

            var result = visible[0].Title;

            window.Close();
            Pump();
            return result;
        });

        title.ShouldContain("尚未扫描");
    }

    [Fact]
    public void Blocked_plugin_should_be_visible_with_its_reason_after_scan()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new PluginManagerHarness();
            harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
                id: "acme.good", displayName: "可用插件"));
            harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
                id: "acme.bad",
                displayName: "哈希不符插件",
                status: PluginValidationStatus.HashMismatch,
                detail: "入口哈希不一致。"));

            // 扫描是异步的，但必须在 STA 线程上驱动，否则集合更新不会进入可视化树。
            Drive(harness.ViewModel.ScanCommand.ExecuteAsync(null));

            var page = new PluginManagerPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var list = FindDescendants<ListBox>(page).Single();
            list.Items.Count.ShouldBe(2, "被阻断的插件必须留在列表里，否则用户查不出原因。");

            var statuses = FindDescendants<StatusBadge>(page)
                .Select(static badge => badge.ResolvedText)
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            statuses.ShouldContain(static text => text.Contains("已阻断"), "阻断状态必须在界面上可见。");

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Trust_confirmation_should_be_visible_before_running()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new PluginManagerHarness();
            harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());
            Drive(harness.ViewModel.ScanCommand.ExecuteAsync(null));

            var page = new PluginManagerPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var viewModel = harness.ViewModel;
            viewModel.RequiresTrust.ShouldBeTrue();

            var runButton = FindDescendants<Button>(page)
                .Single(static button => (string?)button.GetValue(AutomationProperties.NameProperty) == "运行插件");

            runButton.IsEnabled.ShouldBeFalse("未确认来源时运行按钮必须不可用。");

            var checkBox = FindDescendants<CheckBox>(page).Single();
            checkBox.IsChecked = true;
            Pump();

            runButton.IsEnabled.ShouldBeTrue();

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Result_and_progress_components_should_render_with_real_data()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new PluginManagerHarness();
            harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());
            Drive(harness.ViewModel.ScanCommand.ExecuteAsync(null));

            var viewModel = harness.ViewModel;
            viewModel.IsSourceConfirmed = true;
            Drive(viewModel.RunPluginCommand.ExecuteAsync(null));

            var page = new PluginManagerPage { DataContext = viewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            FindDescendants<ResultSummary>(page)
                .ShouldContain(static summary => summary.Visibility == Visibility.Visible);

            var badges = FindDescendants<StatusBadge>(page)
                .Select(static badge => badge.ResolvedText)
                .ToArray();

            badges.ShouldContain(static text => text.Contains("通过"));

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Running_state_should_render_the_progress_panel_before_the_run_completes()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new PluginManagerHarness();
            harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor());
            Drive(harness.ViewModel.ScanCommand.ExecuteAsync(null));

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            harness.Executor.ProgressHook = progress =>
                progress?.Report(new DiagnosticProgress(
                    "run-1",
                    "probe",
                    DiagnosticStepPhase.Running,
                    "已启动隔离进程，正在执行插件步骤“能力探针”…",
                    DateTimeOffset.Now,
                    TimeSpan.FromMilliseconds(120)));

            harness.Executor.Behavior = async cancellationToken =>
            {
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return FakePluginRunExecutor.PassedResult();
            };

            var viewModel = harness.ViewModel;
            viewModel.IsSourceConfirmed = true;

            var run = viewModel.RunPluginCommand.ExecuteAsync(null);

            // 在 STA 线程上泵消息，让 Progress 与 IsRunning 的 UI 投递真正执行；
            // 不能在 Dispatcher 线程上同步等待，否则这页会像产品里一样被卡死。
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!started.Task.IsCompleted || viewModel.State != TaskRunState.Running)
                && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(10);
            }

            started.Task.IsCompleted.ShouldBeTrue("测试执行器未进入运行状态。");
            viewModel.IsRunning.ShouldBeTrue();
            viewModel.State.ShouldBe(TaskRunState.Running);

            var page = new PluginManagerPage { DataContext = viewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            FindDescendants<TaskProgressPanel>(page)
                .ShouldContain(
                    static item => item.Visibility == Visibility.Visible
                        && item.TaskState == TaskRunState.Running,
                    "运行中必须显示带真实 Running 状态的进度面板。");

            release.SetResult();

            deadline = DateTime.UtcNow.AddSeconds(5);
            while (!run.IsCompleted && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(10);
            }

            run.IsCompleted.ShouldBeTrue();

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Unreadable_directory_should_render_an_error_panel_not_an_empty_state()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new PluginManagerHarness();
            harness.Catalog.Behavior = static _ =>
                Task.FromException<IReadOnlyList<PluginDescriptor>>(
                    new UnauthorizedAccessException("插件目录被系统拒绝访问。"));

            Drive(harness.ViewModel.ScanCommand.ExecuteAsync(null));

            var page = new PluginManagerPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            FindDescendants<ErrorPanel>(page)
                .ShouldContain(static panel => panel.Visibility == Visibility.Visible,
                    "目录读不到必须显示错误面板。");

            // "读不到"绝不能同时显示"目录为空"：那会把权限问题误导成安装问题。
            FindDescendants<EmptyState>(page)
                .Where(static state => state.Visibility == Visibility.Visible)
                .ShouldAllBe(static state => !state.Title.Contains("插件目录为空"));

            window.Close();
            Pump();
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Plugin_page_should_render_a_non_blank_bitmap(string paletteName)
    {
        var path = StaRunner.Run(() => Render(paletteName));

        var info = new FileInfo(path);
        info.Exists.ShouldBeTrue($"截图未生成：{path}");
        info.Length.ShouldBeGreaterThan(0, $"截图为空文件：{path}");

        var opaque = StaRunner.Run(() => CountOpaquePixels(path));

        opaque.ShouldBeGreaterThan(
            ReferenceWidth * ReferenceHeight / 2,
            $"{paletteName} 主题下插件管理截图几乎全透明。");
    }

    [Fact]
    public void Light_and_dark_screenshots_should_differ()
    {
        var lightPath = StaRunner.Run(() => Render("Light"));
        var darkPath = StaRunner.Run(() => Render("Dark"));
        var (light, dark) = StaRunner.Run(() => (ReadPixels(lightPath), ReadPixels(darkPath)));

        light.Length.ShouldBe(dark.Length);

        var differing = 0;

        for (var i = 0; i < light.Length; i++)
        {
            if (light[i] != dark[i])
            {
                differing++;
            }
        }

        ((double)differing / light.Length).ShouldBeGreaterThan(
            0.05,
            "插件管理页浅色与深色截图几乎完全相同。");
    }

    private static Window CreateWindow(PluginManagerPage page) =>
        new()
        {
            Content = page,
            Width = ReferenceWidth,
            Height = ReferenceHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0
        };

    /// <summary>
    /// 在 STA 线程内等待一个异步命令完成。
    /// </summary>
    /// <remarks>
    /// 不能直接 <c>await task</c> 之后继续用 UI 元素：命令内部通过
    /// <see cref="SynchronizationContext"/> 把集合更新投递回 UI 线程，
    /// 因此必须在同一线程上泵消息直到完成，否则测试会读到"任务完成但界面未更新"的中间态。
    /// </remarks>
    private static void Drive(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }

        task.IsCompleted.ShouldBeTrue("异步命令未在 10 秒内完成。");

        if (task.Exception is { } exception)
        {
            throw exception;
        }

        Pump();
    }

    private static string Render(string paletteName)
    {
        ThemedHost.EnsureResources();

        var themeManager = new ThemeManager(System.Windows.Application.Current.Resources);
        themeManager.Apply(paletteName == "Dark" ? LfThemePreference.Dark : LfThemePreference.Light);

        var harness = new PluginManagerHarness();
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            id: "acme.vendor.sdk", displayName: "厂商 SDK 插件"));
        harness.Catalog.Descriptors.Add(FakePluginCatalogService.Descriptor(
            id: "acme.blocked",
            displayName: "API 不兼容插件",
            status: PluginValidationStatus.IncompatibleApi,
            detail: "插件要求 API 1.1，宿主仅支持 1.0。"));
        Drive(harness.ViewModel.ScanCommand.ExecuteAsync(null));

        var page = new PluginManagerPage { DataContext = harness.ViewModel };
        var window = CreateWindow(page);

        window.Show();
        Pump();
        Layout(window);

        var bitmap = new RenderTargetBitmap(
            ReferenceWidth,
            ReferenceHeight,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(window);
        bitmap.Freeze();

        window.Close();
        Pump();

        var directory = Path.Combine(FindRepositoryRoot(), "screenshots");
        Directory.CreateDirectory(directory);

        var file = Path.Combine(
            directory,
            $"stage05-plugins-{paletteName.ToLowerInvariant()}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Open(file, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);

        return file;
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(ReferenceWidth, ReferenceHeight));
        window.Arrange(new Rect(0, 0, ReferenceWidth, ReferenceHeight));
        window.UpdateLayout();
    }

    private static void Pump()
    {
        for (var i = 0; i < 60; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
        }
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

    private static int CountOpaquePixels(string path)
    {
        var frame = Decode(path);
        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);

        var opaque = 0;

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 0)
            {
                opaque++;
            }
        }

        return opaque;
    }

    private static byte[] ReadPixels(string path)
    {
        var frame = Decode(path);
        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);

        return pixels;
    }

    private static BitmapFrame Decode(string path) =>
        BitmapDecoder.Create(
            new Uri(path, UriKind.Absolute),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];

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
