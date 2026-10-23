using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkForge.App.Controls;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.Tests.ViewModels;
using LinkForge.App.ViewModels;
using LinkForge.App.Views.Pages;
using LinkForge.DeveloperTools;
using LinkForge.App.Presentation;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// LF-011 开发者工具页渲染测试。
/// </summary>
/// <remarks>
/// 断言的是"真实组件被实例化并可见"，而不是只截一张图：
/// 空状态、运行面板、结果摘要与只读输出框都必须来自真实绑定数据。
/// 页面初始输出必须为空 —— 任何"预填示例结果"都会在这里被抓住。
/// </remarks>
public class DeveloperToolsPageTests
{
    private static readonly object RenderLock = new();
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Page_should_render_the_empty_state_with_an_empty_output()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new DeveloperToolsHarness();
            var page = new DeveloperToolsPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            FindDescendants<EmptyState>(page)
                .ShouldContain(empty => empty.Visibility == Visibility.Visible);

            // 只读输出框必须存在且为空：预填示例值等于用假数据伪装功能。
            var outputBoxes = FindDescendants<TextBox>(page)
                .Where(box => box.IsReadOnly)
                .ToList();

            outputBoxes.ShouldNotBeEmpty("结果区未实例化，页面无法展示转换结果。");
            outputBoxes.ShouldAllBe(box => string.IsNullOrEmpty(box.Text));

            FindDescendants<ResultSummary>(page)
                .ShouldAllBe(summary => summary.Visibility != Visibility.Visible);

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Running_and_result_components_should_be_visible()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new DeveloperToolsHarness();
            // 必须让续延异步派发：假执行器在 STA 线程上被同步调用，
            // 若 SetResult 同步跑续延，测试自己的等待会与它互相阻塞。
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            harness.Executor.Behavior = async (_, _, token) =>
            {
                await release.Task.WaitAsync(token);
                return DeveloperToolResult.Ok("ok", "已处理。");
            };

            var viewModel = harness.ViewModel;
            viewModel.Input = "{}";

            var page = new DeveloperToolsPage { DataContext = viewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            var run = viewModel.RunCommand.ExecuteAsync(null);

            // 不能用 Task.Wait 等运行态：那会冻结本线程的 Dispatcher，
            // 而视图模型正是通过该 Dispatcher 回投 IsRunning。
            WaitForState(viewModel, static state => state.IsRunning);
            Pump();
            window.UpdateLayout();

            FindDescendants<TaskProgressPanel>(page)
                .ShouldContain(static panel => panel.Visibility == Visibility.Visible);

            release.SetResult();
            WaitFor(run, viewModel);

            viewModel.State.ShouldBe(TaskRunState.Completed);
            FindDescendants<ResultSummary>(page)
                .ShouldContain(summary => summary.Visibility == Visibility.Visible);
            FindDescendants<TextBox>(page)
                .Where(box => box.IsReadOnly)
                .ShouldContain(box => box.Text == "ok");

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Cancelled_run_should_not_render_a_result_value()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new DeveloperToolsHarness();
            harness.Executor.Behavior = (_, _, token) =>
                Task.FromException<DeveloperToolResult>(new OperationCanceledException(token));

            var viewModel = harness.ViewModel;
            viewModel.Input = "{}";

            var page = new DeveloperToolsPage { DataContext = viewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            WaitFor(viewModel.RunCommand.ExecuteAsync(null), viewModel);

            viewModel.State.ShouldBe(TaskRunState.Cancelled);
            FindDescendants<TextBox>(page)
                .Where(box => box.IsReadOnly)
                .ShouldAllBe(box => string.IsNullOrEmpty(box.Text));

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Jwt_decode_should_render_the_unverified_notice_in_the_output()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new DeveloperToolsHarness();
            var viewModel = harness.ViewModel;

            viewModel.SelectedTool = harness.Tool(DeveloperToolKind.JwtDecode);
            viewModel.Input = BuildJwt("""{"alg":"HS256","typ":"JWT"}""", """{"sub":"camera-01"}""");

            var page = new DeveloperToolsPage { DataContext = viewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            WaitFor(viewModel.RunCommand.ExecuteAsync(null), viewModel);

            viewModel.IsWarning.ShouldBeTrue();
            FindDescendants<TextBox>(page)
                .Where(box => box.IsReadOnly)
                .ShouldContain(box => box.Text.Contains("未验证"));

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Every_tool_should_be_reachable_in_the_list_without_clipping()
    {
        // 真实缺陷回归守卫：列表最初用固定 Height=260，
        // 第 8 项之后被裁掉，且被裁的行与下方说明文字重叠。
        // 这里断言最后一个工具项确实被实例化并落在列表可视范围内。
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new DeveloperToolsHarness();
            var page = new DeveloperToolsPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            var list = FindDescendants<ListBox>(page).Single();
            list.UpdateLayout();

            var last = DeveloperToolDescriptor.All[^1];
            var container = list.ItemContainerGenerator
                .ContainerFromItem(last) as ListBoxItem;

            container.ShouldNotBeNull($"最后一个工具 {last.Name} 未生成容器。");
            container!.IsVisible.ShouldBeTrue();

            var listBounds = new Rect(list.TranslatePoint(new Point(0, 0), window),
                new Size(list.ActualWidth, list.ActualHeight));
            var itemBounds = new Rect(container.TranslatePoint(new Point(0, 0), window),
                new Size(container.ActualWidth, container.ActualHeight));

            itemBounds.Bottom.ShouldBeLessThanOrEqualTo(
                listBounds.Bottom + 0.5,
                $"{last.Name} 被列表裁切：列表高度不足以容纳全部工具。");

            window.Close();
            Pump();
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Page_should_render_a_non_blank_bitmap(string paletteName)
    {
        var path = StaRunner.Run(() => Render(paletteName));

        try
        {
            new FileInfo(path).Length.ShouldBeGreaterThan(0);
            StaRunner.Run(() => CountOpaquePixels(path))
                .ShouldBeGreaterThan(ReferenceWidth * ReferenceHeight / 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Light_and_dark_screenshots_should_differ()
    {
        var lightPath = StaRunner.Run(() => Render("Light"));
        var darkPath = StaRunner.Run(() => Render("Dark"));

        try
        {
            var light = StaRunner.Run(() => ReadPixels(lightPath));
            var dark = StaRunner.Run(() => ReadPixels(darkPath));
            light.Length.ShouldBe(dark.Length);

            var differing = light.Where((pixel, index) => pixel != dark[index]).Count();

            ((double)differing / light.Length).ShouldBeGreaterThan(0.05);
        }
        finally
        {
            File.Delete(lightPath);
            File.Delete(darkPath);
        }
    }

    private static Window CreateWindow(DeveloperToolsPage page) =>
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
    /// 渲染一张带真实结果的页面截图。
    /// </summary>
    /// <remarks>
    /// 截图前先执行一次真实转换：只截空状态无法证明结果区与操作按钮
    /// 在两个主题下都可读。固定路径写入在静态锁内完成，
    /// 测试读取唯一临时副本，避免并行覆盖。
    /// </remarks>
    private static string Render(string paletteName)
    {
        lock (RenderLock)
        {
            ThemedHost.EnsureResources();

            var manager = new ThemeManager(System.Windows.Application.Current.Resources);
            manager.Apply(paletteName == "Dark" ? LfThemePreference.Dark : LfThemePreference.Light);

            var harness = new DeveloperToolsHarness();
            var viewModel = harness.ViewModel;
            viewModel.SelectedTool = DeveloperToolDescriptor.All.First(static tool =>
                tool.Kind == DeveloperToolKind.JsonFormat);
            viewModel.Input = """{"设备":"前门摄像机","通道":[{"id":1,"状态":"在线"},{"id":2,"状态":"离线"}]}""";

            var page = new DeveloperToolsPage { DataContext = viewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            WaitFor(viewModel.RunCommand.ExecuteAsync(null), viewModel);
            Pump();
            window.UpdateLayout();

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

            var directory = ScreenshotArtifacts.Directory;
            Directory.CreateDirectory(directory);
            var finalFile = Path.Combine(directory, $"stage05-developer-{paletteName.ToLowerInvariant()}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = File.Open(finalFile, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            var readFile = Path.Combine(
                Path.GetTempPath(),
                $"linkforge-developer-{paletteName.ToLowerInvariant()}-{Guid.NewGuid():N}.png");
            using (var stream = File.Open(readFile, FileMode.Create, FileAccess.Write))
            {
                var readEncoder = new PngBitmapEncoder();
                readEncoder.Frames.Add(BitmapFrame.Create(bitmap));
                readEncoder.Save(stream);
            }

            return readFile;
        }
    }

    private static void WaitForState(DeveloperToolsViewModel viewModel, Func<DeveloperToolsViewModel, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!condition(viewModel) && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }

        condition(viewModel).ShouldBeTrue("页面状态未在 5 秒内到达预期。");
    }

    private static void WaitFor(Task run, DeveloperToolsViewModel viewModel)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }

        run.IsCompleted.ShouldBeTrue("开发者工具未能在 5 秒内结束。");
        Pump();
    }

    private static void Layout(Window window)
    {
        window.UpdateLayout();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);

        while (window.ActualWidth <= 0 && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
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

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static int CountOpaquePixels(string path)
    {
        var bitmap = LoadBitmap(path);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        var count = 0;

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static byte[] ReadPixels(string path)
    {
        var bitmap = LoadBitmap(path);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bitmap = new BitmapImage();
        using var stream = File.OpenRead(path);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string BuildJwt(string header, string payload) =>
        $"{Base64Url(header)}.{Base64Url(payload)}.ZGVtby1zaWduYXR1cmU";

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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

        throw new InvalidOperationException("未能找到仓库根目录。");
    }
}
