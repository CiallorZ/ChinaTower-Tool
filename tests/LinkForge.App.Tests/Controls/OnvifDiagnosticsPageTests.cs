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

public class OnvifDiagnosticsPageTests
{
    private static readonly object RenderLock = new();
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Onvif_page_should_survive_full_layout_and_show_empty_state()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            using var harness = new OnvifDiagnosticsHarness();
            var page = new OnvifDiagnosticsPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            FindDescendants<EmptyState>(page)
                .ShouldContain(state => state.Visibility == Visibility.Visible);
            FindDescendants<SecretTextBox>(page).ShouldHaveSingleItem();

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
            using var harness = new OnvifDiagnosticsHarness();
            harness.Probe.DeviceBehavior = async token =>
            {
                await Task.Delay(150, token);
                return harness.Probe.Device;
            };
            harness.ViewModel.ServiceUrl = "http://127.0.0.1/onvif/device_service";
            var page = new OnvifDiagnosticsPage { DataContext = harness.ViewModel };
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
                .ShouldContain(panel => panel.Visibility == Visibility.Visible);

            while (!run.IsCompleted && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(10);
            }

            run.IsCompleted.ShouldBeTrue();
            Pump();
            window.UpdateLayout();

            FindDescendants<ResultSummary>(page)
                .ShouldContain(summary => summary.Visibility == Visibility.Visible);

            window.Close();
            Pump();
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Onvif_page_should_render_non_blank_screenshot(string paletteName)
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
    public void Onvif_light_and_dark_screenshots_should_differ()
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

    private static Window CreateWindow(OnvifDiagnosticsPage page) =>
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

            using var harness = new OnvifDiagnosticsHarness();
            var page = new OnvifDiagnosticsPage { DataContext = harness.ViewModel };
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
            var finalFile = Path.Combine(directory, $"stage05-onvif-{paletteName.ToLowerInvariant()}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = File.Open(finalFile, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            var readFile = Path.Combine(
                Path.GetTempPath(),
                $"linkforge-onvif-{paletteName.ToLowerInvariant()}-{Guid.NewGuid():N}.png");
            using (var stream = File.Open(readFile, FileMode.Create, FileAccess.Write))
            {
                var readEncoder = new PngBitmapEncoder();
                readEncoder.Frames.Add(BitmapFrame.Create(bitmap));
                readEncoder.Save(stream);
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
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
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
