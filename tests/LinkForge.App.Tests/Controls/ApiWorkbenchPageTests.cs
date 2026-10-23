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

/// <summary>API 工作台页面渲染与状态测试。</summary>
public class ApiWorkbenchPageTests
{
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Api_page_should_survive_a_full_layout_pass()
    {
        var failure = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var harness = new ApiWorkbenchHarness();
            var page = new ApiWorkbenchPage { DataContext = harness.ViewModel };
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
            throw new InvalidOperationException("API 工作台页面加载失败。", failure);
        }
    }

    [Fact]
    public void Empty_and_result_states_should_render_real_components()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            using var harness = new ApiWorkbenchHarness();
            var viewModel = harness.ViewModel;
            var page = new ApiWorkbenchPage { DataContext = viewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            FindDescendants<EmptyState>(page)
                .ShouldContain(static empty => empty.Visibility == Visibility.Visible);

            viewModel.Url = "http://127.0.0.1:8080/api";
            var run = viewModel.SendCommand.ExecuteAsync(null);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!run.IsCompleted && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(10);
            }

            run.IsCompleted.ShouldBeTrue("API 请求未在 5 秒内完成。");
            Pump();
            window.UpdateLayout();

            viewModel.HasResponse.ShouldBeTrue();
            FindDescendants<ResultSummary>(page)
                .ShouldContain(static summary => summary.Visibility == Visibility.Visible);

            window.Close();
            Pump();
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Api_page_should_render_a_non_blank_bitmap(string paletteName)
    {
        var path = StaRunner.Run(() => Render(paletteName));
        var info = new FileInfo(path);

        info.Exists.ShouldBeTrue($"截图未生成：{path}");
        info.Length.ShouldBeGreaterThan(0);

        var opaque = StaRunner.Run(() => CountOpaquePixels(path));
        opaque.ShouldBeGreaterThan(ReferenceWidth * ReferenceHeight / 2);
    }

    [Fact]
    public void Light_and_dark_api_screenshots_should_differ()
    {
        var light = StaRunner.Run(() => ReadPixels(Render("Light")));
        var dark = StaRunner.Run(() => ReadPixels(Render("Dark")));

        light.Length.ShouldBe(dark.Length);

        var differing = 0;
        for (var i = 0; i < light.Length; i++)
        {
            if (light[i] != dark[i])
            {
                differing++;
            }
        }

        ((double)differing / light.Length).ShouldBeGreaterThan(0.05);
    }

    private static Window CreateWindow(ApiWorkbenchPage page) =>
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

        var manager = new ThemeManager(System.Windows.Application.Current.Resources);
        manager.Apply(paletteName == "Dark" ? LfThemePreference.Dark : LfThemePreference.Light);

        using var harness = new ApiWorkbenchHarness();
        var page = new ApiWorkbenchPage { DataContext = harness.ViewModel };
        var window = CreateWindow(page);

        window.Show();
        Pump();
        Layout(window);
        WaitForExpanderAnimations();
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
        var file = Path.Combine(directory, $"stage05-api-{paletteName.ToLowerInvariant()}.png");

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

    private static void WaitForExpanderAnimations()
    {
        // WPF-UI 的 Expander 在首次展开时使用时间驱动的动画。
        // 只清空 Dispatcher 队列不会推进动画时钟，截屏会抓到内容仍被裁切的一帧。
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(900);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(16);
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
        var pixels = new byte[frame.PixelWidth * 4 * frame.PixelHeight];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);

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
        var pixels = new byte[frame.PixelWidth * 4 * frame.PixelHeight];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
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

        throw new InvalidOperationException("未找到仓库根目录。");
    }
}
