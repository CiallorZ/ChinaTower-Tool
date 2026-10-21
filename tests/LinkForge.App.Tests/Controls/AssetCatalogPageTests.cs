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
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 设备与资产页的真实渲染与截图测试（阶段 05 / LF-004）。
/// </summary>
/// <remarks>
/// 这一页的全部内容来自本地数据库：没有“示例项目”、没有占位资产。
/// 因此这里必须验证空状态、加载状态、真实资产列表与导入结论
/// 都真的渲染出来 —— 只看视图模型的布尔值不能证明界面正确。
/// </remarks>
public class AssetCatalogPageTests
{
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Asset_page_should_survive_a_full_layout_pass()
    {
        var failure = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();

            // 用与验收截图一致的"有内容"状态：
            // 空库时左栏很短，根本触发不了溢出，也测不出裁切问题。
            harness.Projects.Items.Add(new Project
            {
                Id = "prj-1",
                Name = "东门视频项目",
                Description = "一号楼东门与园区周界",
                CreatedAt = DateTimeOffset.Now
            });

            harness.Projects.Items.Add(new Project
            {
                Id = "prj-2",
                Name = "西门视频项目",
                CreatedAt = DateTimeOffset.Now.AddDays(-1)
            });

            harness.Assets.Items.Add(new Asset
            {
                Id = "asset-1",
                ProjectId = "prj-1",
                Name = "东门球机",
                Host = "10.0.0.8",
                Port = 554,
                Kind = AssetKind.Camera,
                Vendor = "海康",
                Tags = ["园区"],
                CredentialReference = "asset.cam-01.password"
            });

            harness.Assets.Items.Add(new Asset
            {
                Id = "asset-2",
                ProjectId = "prj-1",
                Name = "园区网关",
                Host = "10.0.0.10",
                Kind = AssetKind.Gateway,
                Vendor = "通用",
                Tags = ["园区"]
            });

            harness.Assets.Items.Add(new Asset
            {
                Id = "asset-3",
                ProjectId = "prj-1",
                Name = "MQTT Broker",
                Host = "10.0.0.11",
                Port = 1883,
                Kind = AssetKind.Broker,
                Tags = ["机房"]
            });

            Drive(harness.ViewModel.LoadAsync());

            var page = new AssetCatalogPage { DataContext = harness.ViewModel };

            // 用比验收分辨率更矮的窗口：只有内容真的装不下时，
            // "能不能滚动到"这个问题才有意义。
            // 1366×768 下内容可能刚好放得下，那样裁切缺陷根本不会暴露。
            var window = new Window
            {
                Content = page,
                Width = ReferenceWidth,
                Height = 560,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0
            };

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
            throw new InvalidOperationException("设备与资产页在加载/渲染阶段抛出异常。", failure);
        }
    }

    [Fact]
    public void Content_should_be_reachable_by_scrolling_instead_of_being_clipped()
    {
        // 回归：三栏内容比 1366×768 高，左栏的"创建环境"按钮曾被裁到视口之外，
        // 用户既看不到也点不到。现在页面用 ScrollViewer 承载，超出部分必须能滚动到。
        var (scrollable, reachable, detail) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();

            // 用与验收截图一致的"有内容"状态：
            // 空库时左栏很短，根本触发不了溢出，也测不出裁切问题。
            harness.Projects.Items.Add(new Project
            {
                Id = "prj-1",
                Name = "东门视频项目",
                Description = "一号楼东门与园区周界",
                CreatedAt = DateTimeOffset.Now
            });

            harness.Projects.Items.Add(new Project
            {
                Id = "prj-2",
                Name = "西门视频项目",
                CreatedAt = DateTimeOffset.Now.AddDays(-1)
            });

            harness.Assets.Items.Add(new Asset
            {
                Id = "asset-1",
                ProjectId = "prj-1",
                Name = "东门球机",
                Host = "10.0.0.8",
                Port = 554,
                Kind = AssetKind.Camera,
                Vendor = "海康",
                Tags = ["园区"],
                CredentialReference = "asset.cam-01.password"
            });

            harness.Assets.Items.Add(new Asset
            {
                Id = "asset-2",
                ProjectId = "prj-1",
                Name = "园区网关",
                Host = "10.0.0.10",
                Kind = AssetKind.Gateway,
                Vendor = "通用",
                Tags = ["园区"]
            });

            harness.Assets.Items.Add(new Asset
            {
                Id = "asset-3",
                ProjectId = "prj-1",
                Name = "MQTT Broker",
                Host = "10.0.0.11",
                Port = 1883,
                Kind = AssetKind.Broker,
                Tags = ["机房"]
            });

            Drive(harness.ViewModel.LoadAsync());

            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window, ReferenceWidth, 560);

            // 取页面自己那个：ListBox 等控件自带内层 ScrollViewer，
            // 取到内层的会得到"内容不溢出"的假结论，
            // 而且内层 ScrollViewer 也不是按钮的上级，坐标换算会直接抛异常。
            // 判据：它承载的是页面里那层带 5 列的主工作区 Grid。
            // （ScrollViewer 与内容之间隔着 ScrollContentPresenter，
            //   所以要看 Content 而不是直接父级。）
            var scrollViewer = FindDescendants<ScrollViewer>(page)
                .FirstOrDefault(viewer =>
                    viewer.Content is Grid owner && owner.ColumnDefinitions.Count == 5);

            scrollViewer.ShouldNotBeNull(
                "页面必须提供页面级滚动容器，否则超出一屏的内容会被直接裁掉。");

            // 布局是异步的：ScrollableHeight 只有在测量完成后才有意义。
            window.UpdateLayout();
            Pump();
            window.UpdateLayout();
            var createEnvironment = FindDescendants<Button>(page)
                .FirstOrDefault(button =>
                    button.Content as string == "创建环境" && button.Visibility == Visibility.Visible);

            createEnvironment.ShouldNotBeNull("左栏应当有“创建环境”按钮。");

            // 滚到底之前：按钮应当还在视口之外（内容确实装不下）。
            // 坐标必须相对 ScrollViewer 算：相对 page 算会把工具条的高度也算进去，
            // 得到"按钮其实在视口内"的假结论。
            var bounds = createEnvironment!.TransformToAncestor(scrollViewer).TransformBounds(
                new Rect(new Point(0, 0), createEnvironment.RenderSize));

            var topBefore = bounds.Top;
            var viewportHeight = scrollViewer.ViewportHeight;

            scrollViewer!.ScrollToEnd();
            Pump();
            window.UpdateLayout();

            bounds = createEnvironment.TransformToAncestor(scrollViewer).TransformBounds(
                new Rect(new Point(0, 0), createEnvironment.RenderSize));

            var topAfter = bounds.Top;

            window.Close();
            Pump();

            return (
                topBefore > viewportHeight,
                bounds.Top >= -1 && bounds.Bottom <= viewportHeight + 1,
                $"scrollableHeight={scrollViewer.ScrollableHeight}, viewport={viewportHeight}, " +
                $"topBefore={topBefore}, topAfter={topAfter}, " +
                $"contentHeight={((FrameworkElement)scrollViewer.Content).ActualHeight}, " +
                $"contentDesired={((FrameworkElement)scrollViewer.Content).DesiredSize.Height}, " +
                $"all=[{string.Join(" | ", FindDescendants<ScrollViewer>(page).Select(v => $"vh={v.ViewportHeight},sh={v.ScrollableHeight}"))}]");
        });

        scrollable.ShouldBeTrue(
            "内容应当超出视口（否则这条用例根本测不到裁切）。" + detail);
        reachable.ShouldBeTrue(
            "滚动到底之后，“创建环境”按钮仍应落在页面可视区域内。");
    }

    [Fact]
    public void Initial_state_should_show_a_real_empty_state_not_a_blank_panel()
    {
        var titles = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();
            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            var visible = FindDescendants<EmptyState>(page)
                .Where(static state => state.Visibility == Visibility.Visible)
                .Select(static state => state.Title)
                .ToArray();

            window.Close();
            Pump();
            return visible;
        });

        titles.ShouldNotBeEmpty("初始状态必须有空状态，而不是一个空白面板。");
    }

    [Fact]
    public void Real_assets_should_be_listed_with_their_target_and_credential_state()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();
            harness.Projects.Items.Add(new Project
            {
                Id = "prj-1",
                Name = "东门视频项目",
                CreatedAt = DateTimeOffset.Now
            });

            harness.Assets.Items.Add(new Asset
            {
                Id = "asset-1",
                ProjectId = "prj-1",
                Name = "东门球机",
                Host = "10.0.0.8",
                Port = 554,
                Kind = AssetKind.Camera,
                Vendor = "海康",
                Tags = ["园区"],
                CredentialReference = "asset.cam-01.password"
            });

            Drive(harness.ViewModel.LoadAsync());

            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var list = FindDescendants<ListBox>(page).Single();
            list.Items.Count.ShouldBe(1, "真实资产必须出现在列表里。");

            var text = CollectText(list);

            text.ShouldContain("东门球机");
            text.ShouldContain("10.0.0.8:554");
            text.ShouldContain("已配置凭据引用");

            // 凭据只显示“是否已配置”，绝不显示引用键本身或任何值。
            text.ShouldNotContain("asset.cam-01.password");

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Group_header_rows_should_render_and_cannot_be_selected()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();
            harness.Projects.Items.Add(new Project
            {
                Id = "prj-1",
                Name = "东门视频项目",
                CreatedAt = DateTimeOffset.Now
            });

            harness.Assets.Items.Add(new Asset
            {
                Id = "asset-1",
                ProjectId = "prj-1",
                Name = "东门球机",
                Host = "10.0.0.8",
                Kind = AssetKind.Camera,
                Tags = ["园区"]
            });

            Drive(harness.ViewModel.LoadAsync());
            harness.ViewModel.IsGroupByTag = true;

            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var list = FindDescendants<ListBox>(page).Single();
            list.Items.Count.ShouldBe(2, "一个分组头 + 一行资产。");

            var containers = FindDescendants<ListBoxItem>(list).ToArray();

            // 分组头行必须不可选中：否则“选中项一定是一台真实资产”就不成立。
            var header = containers.FirstOrDefault(item => item.IsEnabled == false);
            header.ShouldNotBeNull("分组头行必须存在且不可选。");

            var text = CollectText(list);
            text.ShouldContain("园区");

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Import_conclusion_should_render_the_partial_success_state()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();
            harness.Projects.Items.Add(new Project
            {
                Id = "prj-1",
                Name = "东门视频项目",
                CreatedAt = DateTimeOffset.Now
            });

            Drive(harness.ViewModel.LoadAsync());

            harness.Files.ImportContent = System.Text.Encoding.UTF8.GetBytes(
                "name,host,port\r\n东门球机,10.0.0.8,554\r\n,10.0.0.9,554\r\n");

            Drive(harness.ViewModel.ImportCsvCommand.ExecuteAsync(null));

            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var badges = FindDescendants<StatusBadge>(page)
                .Select(static badge => badge.ResolvedText)
                .ToArray();

            badges.ShouldContain(static text => text.Contains("部分成功"),
                "部分成功必须以独立状态显示，不能与成功或失败混同。");

            var text = CollectText(page);
            text.ShouldContain("第 3 行");

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Storage_failure_should_render_an_error_panel_not_an_empty_state()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();
            harness.Storage.Failure = new LinkForge.Application.Storage.StorageException(
                "数据库文件被另一个 LinkForge 实例占用。");

            Drive(harness.ViewModel.LoadAsync());

            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            FindDescendants<ErrorPanel>(page)
                .ShouldContain(static panel => panel.Visibility == Visibility.Visible,
                    "数据库读不到必须显示错误面板。");

            // “读不到”绝不能同时显示“没有资产”：那会把权限问题误导成数据丢了。
            FindDescendants<EmptyState>(page)
                .Where(static state => state.Visibility == Visibility.Visible)
                .ShouldAllBe(static state => !state.Title.Contains("当前项目还没有资产"));

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Buttons_should_expose_accessible_names()
    {
        var problems = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new AssetCatalogHarness();
            var page = new AssetCatalogPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var problems = new List<string>();

            foreach (var button in FindDescendants<Button>(page))
            {
                // 折叠的按钮不在无障碍树里：EmptyState 无主操作时会把按钮整体收起，
                // 那时 AutomationProperties.Name 本来就没有值，不该断言。
                if (button.Visibility != Visibility.Visible)
                {
                    continue;
                }

                // 只校验页面自己声明的按钮：
                // ComboBox / CheckBox / TextBox 模板内部的按钮由控件库负责，
                // 本项目不能替第三方模板补可访问名称。
                if (!IsPageOwnedButton(button, page))
                {
                    continue;
                }

                var name = AutomationProperties.GetName(button);

                if (string.IsNullOrWhiteSpace(name))
                {
                    problems.Add(
                        $"{button.GetType().FullName}:{Describe(button, page)} 缺少 AutomationProperties.Name");
                }
            }

            window.Close();
            Pump();

            return problems;
        });

        problems.ShouldBeEmpty("按钮缺少可访问名称：" + string.Join("; ", problems));
    }

    /// <summary>
    /// 判断按钮是否由本页面直接声明（而不是控件库模板内部的零件）。
    /// </summary>
    /// <param name="button">按钮。</param>
    /// <param name="page">页面根。</param>
    /// <returns>从页面根到按钮的视觉树路径，便于定位问题控件。</returns>
    private static string Describe(DependencyObject button, DependencyObject page)
    {
        var names = new List<string>();
        for (var current = button; current is not null && !ReferenceEquals(current, page);
             current = VisualTreeHelper.GetParent(current))
        {
            names.Add(current.GetType().Name);
        }

        names.Add(page.GetType().Name);
        names.Reverse();

        return string.Join(" > ", names);
    }

    /// <summary>
    /// 判断按钮是否由本页面直接声明（而不是控件库模板内部的零件）。
    /// </summary>
    /// <param name="button">按钮。</param>
    /// <param name="page">页面根。</param>
    /// <returns>是否为页面自有按钮。</returns>
    private static bool IsPageOwnedButton(Button button, DependencyObject page)
    {
        for (var current = VisualTreeHelper.GetParent(button); current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            // 控件库控件（ComboBox / CheckBox / TextBox / PasswordBox）的模板零件
            // 不归本项目管：它们的按钮由控件库自己的模板定义。
            if (current is ComboBox
                or CheckBox
                or TextBox
                or PasswordBox
                or Wpf.Ui.Controls.TextBox
                or Wpf.Ui.Controls.PasswordBox)
            {
                return false;
            }

            if (ReferenceEquals(current, page))
            {
                return true;
            }
        }

        return false;
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Asset_page_should_render_a_non_blank_bitmap(string paletteName)
    {
        var path = StaRunner.Run(() => Render(paletteName));

        var info = new FileInfo(path);
        info.Exists.ShouldBeTrue($"截图未生成：{path}");
        info.Length.ShouldBeGreaterThan(0, $"截图为空文件：{path}");

        var opaque = StaRunner.Run(() => CountOpaquePixels(path));

        opaque.ShouldBeGreaterThan(
            ReferenceWidth * ReferenceHeight / 2,
            $"{paletteName} 主题下设备与资产截图几乎全透明。");
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
            "设备与资产页浅色与深色截图几乎完全相同。");
    }

    private static Window CreateWindow(AssetCatalogPage page) =>
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
    /// 在 STA 线程内等待一个异步操作完成。
    /// </summary>
    /// <remarks>
    /// 不能直接 await 之后继续用 UI 元素：视图模型内部通过
    /// <see cref="SynchronizationContext"/> 把集合更新投递回 UI 线程，
    /// 因此必须在同一线程上泵消息直到完成，否则测试会读到“任务完成但界面未更新”的中间态。
    /// </remarks>
    private static void Drive(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }

        task.IsCompleted.ShouldBeTrue("异步操作未在 10 秒内完成。");

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

        var harness = new AssetCatalogHarness();
        harness.Projects.Items.Add(new Project
        {
            Id = "prj-1",
            Name = "东门视频项目",
            Description = "一号楼东门与园区周界",
            CreatedAt = DateTimeOffset.Now
        });

        harness.Projects.Items.Add(new Project
        {
            Id = "prj-2",
            Name = "西门视频项目",
            CreatedAt = DateTimeOffset.Now.AddDays(-1)
        });

        harness.Assets.Items.Add(new Asset
        {
            Id = "asset-1",
            ProjectId = "prj-1",
            Name = "东门球机",
            Host = "10.0.0.8",
            Port = 554,
            Kind = AssetKind.Camera,
            Vendor = "海康",
            Tags = ["园区", "东门"],
            CredentialReference = "asset.cam-01.password"
        });

        harness.Assets.Items.Add(new Asset
        {
            Id = "asset-2",
            ProjectId = "prj-1",
            Name = "园区网关",
            Host = "10.0.0.10",
            Kind = AssetKind.Gateway,
            Vendor = "通用",
            Tags = ["园区"]
        });

        harness.Assets.Items.Add(new Asset
        {
            Id = "asset-3",
            ProjectId = "prj-1",
            Name = "MQTT Broker",
            Host = "10.0.0.11",
            Port = 1883,
            Kind = AssetKind.Broker,
            Tags = ["机房"]
        });

        Drive(harness.ViewModel.LoadAsync());

        var page = new AssetCatalogPage { DataContext = harness.ViewModel };
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
            $"stage05-assets-{paletteName.ToLowerInvariant()}.png");

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

    /// <summary>
    /// 以指定尺寸完成一次布局。
    /// </summary>
    /// <param name="window">窗口。</param>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    private static void Layout(Window window, double width, double height)
    {
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
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

    /// <summary>
    /// 收集可视树中的全部文本，用于断言“某个信息真的显示在界面上”。
    /// </summary>
    /// <param name="root">根元素。</param>
    /// <returns>拼接后的文本。</returns>
    private static string CollectText(DependencyObject root)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var textBlock in FindDescendants<TextBlock>(root))
        {
            builder.Append(textBlock.Text);
            builder.Append('\n');

            // 逐段文本（Run）不在 TextBlock.Text 里：
            // 资产行用多个 Run 拼接"目标 · 类型 · 凭据状态"，
            // 只读 Text 会得到空字符串，从而漏检真实显示内容。
            foreach (var run in textBlock.Inlines.OfType<System.Windows.Documents.Run>())
            {
                builder.Append(run.Text);
                builder.Append('\n');
            }
        }

        return builder.ToString();
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

