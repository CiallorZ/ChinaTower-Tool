using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkForge.App.Controls;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.Tests.ViewModels;
using LinkForge.App.Views.Pages;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 网络诊断页面的真实渲染与截图测试（阶段 05 / LF-006）。
/// </summary>
public class NetworkDiagnosticsPageTests
{
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Network_page_should_survive_a_full_layout_pass()
    {
        var failure = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var harness = new NetworkDiagnosticsHarness();
            var page = new NetworkDiagnosticsPage { DataContext = harness.ViewModel };
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
            throw new InvalidOperationException("网络诊断页面在加载/渲染阶段抛出异常。", failure);
        }
    }

    [Fact]
    public void Empty_running_and_result_states_should_render_their_real_components()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var harness = new NetworkDiagnosticsHarness();
            var viewModel = harness.ViewModel;
            var page = new NetworkDiagnosticsPage { DataContext = viewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            var emptyState = FindDescendants<EmptyState>(page)
                .Single(static control => control.Visibility == Visibility.Visible);

            emptyState.Title.ShouldContain("尚未运行");

            viewModel.Target = "127.0.0.1";
            var run = viewModel.RunCommand.ExecuteAsync(null);

            // 命令会先完成输入校验，再异步进入运行态；这里等待状态真正出现，
            // 而不是用固定次数的 Dispatcher 轮询去赌时序。
            var runningDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

            while (!viewModel.IsRunning && !run.IsCompleted && DateTime.UtcNow < runningDeadline)
            {
                Pump();
                Thread.Sleep(10);
            }

            viewModel.IsRunning.ShouldBeTrue();
            FindDescendants<TaskProgressPanel>(page)
                .ShouldContain(static panel => panel.Visibility == Visibility.Visible);

            // 不能在 STA 线程上同步等待：进度通过 Dispatcher 投递，
            // 阻塞线程会让命令与回调互相等待。改为后台观察完成状态，同时继续泵消息。
            var completed = false;

            _ = run.ContinueWith(
                _ => completed = true,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var resultDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

            while (!completed && DateTime.UtcNow < resultDeadline)
            {
                Pump();
                Thread.Sleep(10);
            }

            completed.ShouldBeTrue("网络诊断命令未在 5 秒内完成。");
            Pump();
            window.UpdateLayout();

            viewModel.HasResult.ShouldBeTrue();
            FindDescendants<ResultSummary>(page)
                .ShouldContain(static panel => panel.Visibility == Visibility.Visible);

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Advanced_parameter_fields_should_have_visible_height_and_labels()
    {
        // 截图曾显示“高级参数”展开后出现大块留白：控件模板存在，
        // 但字段内容没有真正占据高度。这里直接量测控件与标签，避免只靠目视漏掉回归。
        var report = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var harness = new NetworkDiagnosticsHarness();
            var viewModel = harness.ViewModel;
            var page = new NetworkDiagnosticsPage { DataContext = viewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            var fields = FindDescendants<LabeledTextBox>(page).ToArray();
            var labels = fields
                .Select(static field => field.Label)
                .Where(static label => !string.IsNullOrWhiteSpace(label))
                .ToArray();
            var heights = fields
                .Where(static field => field.Visibility == Visibility.Visible)
                .Select(static field => field.ActualHeight)
                .ToArray();

            window.Close();
            Pump();

            return (fields.Length, labels, heights);
        });

        report.Length.ShouldBeGreaterThanOrEqualTo(2);
        report.labels.ShouldContain("目标");
        report.labels.ShouldContain("超时（秒）");
        report.heights.ShouldAllBe(static height => height > 0);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Network_page_should_render_a_non_blank_bitmap(string paletteName)
    {
        var path = StaRunner.Run(() => Render(paletteName));

        var info = new FileInfo(path);
        info.Exists.ShouldBeTrue($"截图未生成：{path}");
        info.Length.ShouldBeGreaterThan(0, $"截图为空文件：{path}");

        var opaque = StaRunner.Run(() => CountOpaquePixels(path));

        opaque.ShouldBeGreaterThan(
            ReferenceWidth * ReferenceHeight / 2,
            $"{paletteName} 主题下网络诊断截图几乎全透明。");
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
            "网络诊断页浅色与深色截图几乎完全相同。");
    }

    private static Window CreateWindow(NetworkDiagnosticsPage page) =>
        new()
        {
            Content = page,
            Width = ReferenceWidth,
            Height = ReferenceHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0
        };

    private static string Render(string paletteName)
    {
        ThemedHost.EnsureResources();

        var themeManager = new ThemeManager(System.Windows.Application.Current.Resources);
        themeManager.Apply(paletteName == "Dark" ? LfThemePreference.Dark : LfThemePreference.Light);

        using var harness = new NetworkDiagnosticsHarness();
        var page = new NetworkDiagnosticsPage { DataContext = harness.ViewModel };
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
            $"stage05-network-{paletteName.ToLowerInvariant()}.png");

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
