using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkForge.App.Controls;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.Tests.ViewModels;
using LinkForge.App.Views.Pages;

namespace LinkForge.App.Tests.Controls;

/// <summary>安全中心页面的真实渲染、状态组件与主题截图测试（LF-020）。</summary>
public class SecurityCenterPageTests
{
    private static readonly object RenderLock = new();
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Security_page_should_survive_full_layout_and_show_scope()
    {
        var report = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            using var harness = new SecurityCenterHarness();
            var page = new SecurityCenterPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            var empty = FindDescendants<EmptyState>(page)
                .SingleOrDefault(static item => item.Visibility == Visibility.Visible);
            var fields = FindDescendants<LabeledTextBox>(page)
                .Where(static field => field.Visibility == Visibility.Visible)
                .ToArray();
            var result = (
                empty?.Title,
                fields.Select(static field => field.Label).ToArray(),
                fields.All(static field => field.ActualHeight > 0));

            window.Close();
            Pump();
            return result;
        });

        report.Title.ShouldNotBeNullOrWhiteSpace();
        report.Item2.ShouldContain("目标");
        report.Item2.ShouldContain("TCP 端口");
        report.Item2.ShouldContain("TLS 端口");
        report.Item3.ShouldBeTrue("安全中心输入控件没有实际高度。");
    }

    [Fact]
    public async Task Running_and_result_components_should_be_visible()
    {
        await Task.CompletedTask;

        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            using var harness = new SecurityCenterHarness();
            harness.Probe.Behavior = async (request, token) =>
            {
                await Task.Delay(150, token);
                return FakeSecurityProbe.DefaultSuccess(request);
            };
            var page = new SecurityCenterPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            var run = harness.ViewModel.RunCommand.ExecuteAsync(null);
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (!harness.ViewModel.IsRunning && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(10);
            }

            FindDescendants<TaskProgressPanel>(page)
                .ShouldContain(static panel => panel.Visibility == Visibility.Visible);

            while (!run.IsCompleted && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(10);
            }

            run.IsCompleted.ShouldBeTrue();
            Pump();
            window.UpdateLayout();

            FindDescendants<ResultSummary>(page)
                .ShouldContain(static panel => panel.Visibility == Visibility.Visible);
            harness.ViewModel.Steps.ShouldNotBeEmpty();

            window.Close();
            Pump();
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Security_page_should_render_non_blank_screenshot(string paletteName)
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
    public void Security_light_and_dark_screenshots_should_differ()
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

    private static Window CreateWindow(SecurityCenterPage page) =>
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
        lock (RenderLock)
        {
            ThemedHost.EnsureResources();
            var manager = new ThemeManager(System.Windows.Application.Current.Resources);
            manager.Apply(paletteName == "Dark" ? LfThemePreference.Dark : LfThemePreference.Light);

            using var harness = new SecurityCenterHarness();
            var page = new SecurityCenterPage { DataContext = harness.ViewModel };
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

            var directory = ScreenshotArtifacts.Directory;
            Directory.CreateDirectory(directory);
            var finalFile = Path.Combine(
                directory,
                $"stage06-security-{paletteName.ToLowerInvariant()}.png");

            using (var stream = File.Open(finalFile, FileMode.Create, FileAccess.Write))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }

            // 测试读取唯一临时副本，避免并行渲染覆盖固定截图时发生文件锁竞争。
            var readFile = Path.Combine(
                Path.GetTempPath(),
                $"linkforge-security-{paletteName.ToLowerInvariant()}-{Guid.NewGuid():N}.png");

            using (var stream = File.Open(readFile, FileMode.Create, FileAccess.Write))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }

            return readFile;
        }
    }

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        var deadline = DateTime.UtcNow.AddSeconds(2);

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
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

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
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0)
            .CopyPixels(pixels, stride, 0);

        var count = 0;

        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] > 0)
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
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0)
            .CopyPixels(pixels, stride, 0);
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
}
