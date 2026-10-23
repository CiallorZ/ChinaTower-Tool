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

public class ReportPageTests
{
    private static readonly object RenderLock = new();
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public async Task Report_page_should_show_empty_state_without_history()
    {
        await Task.CompletedTask;

        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            using var harness = new ReportPageHarness();
            var page = new ReportPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            // 初始 IsLoading=true，先让它完成读取再断言空状态。
            var load = harness.ViewModel.LoadAsync();

            while (!load.IsCompleted)
            {
                Pump();
                Thread.Sleep(10);
            }

            window.UpdateLayout();
            Pump();

            // 空状态必须是真正可见的：一个 Collapsed 的占位等于页面什么都没有。
            FindDescendants<EmptyState>(page)
                .ShouldContain(e => e.Visibility == Visibility.Visible);

            window.Close();
            Pump();
        });
    }

    [Fact]
    public async Task Report_page_should_render_steps_after_preview()
    {
        await Task.CompletedTask;

        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            using var harness = new ReportPageHarness();
            harness.History.Entries.Add(ReportPageHarness.SampleRun());

            var page = new ReportPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var load = harness.ViewModel.LoadAsync();
            WaitFor(ref load);

            var preview = harness.ViewModel.PreviewCommand.ExecuteAsync(null);

            while (!preview.IsCompleted)
            {
                Pump();
                Thread.Sleep(10);
            }

            window.UpdateLayout();
            Pump();

            FindDescendants<StatusBadge>(page).ShouldNotBeEmpty();
            FindDescendants<System.Windows.Controls.ItemsControl>(page)
                .ShouldContain(c => c.Items.Count > 0);

            window.Close();
            Pump();
        });
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Selected_run_item_should_keep_text_readable(string paletteName)
    {
        // 真实缺陷回归守卫：默认 ListBoxItem 选中态用系统强调色（深蓝）做背景，
        // 但条目内各文本使用自己的令牌前景色，结果渲染成"深蓝底 + 深灰字"。
        // 这里直接断言选中项背景不是强调实色，且文字保持主色令牌。
        var (background, textBrush) = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            ThemedHost.UsePalette(paletteName);

            using var harness = new ReportPageHarness();
            harness.History.Entries.Add(ReportPageHarness.SampleRun());

            var page = new ReportPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var load = harness.ViewModel.LoadAsync();
            WaitFor(ref load);
            window.UpdateLayout();
            Pump();

            var listBox = FindDescendants<System.Windows.Controls.ListBox>(page).First();
            var container = (System.Windows.Controls.ListBoxItem)listBox.ItemContainerGenerator
                .ContainerFromIndex(0)!;

            container.IsSelected = true;
            Pump();
            window.UpdateLayout();

            // 取标题文本块：状态标签有自己的语义色（例如"部分成功"是警告色），
            // 用第一个 TextBlock 会误取到它。
            var textBlock = FindDescendants<System.Windows.Controls.TextBlock>(container)
                .First(t => t.Text == "网络基础诊断");

            var result = (
                (container.Background as SolidColorBrush)?.Color,
                (textBlock.Foreground as SolidColorBrush)?.Color);

            window.Close();
            Pump();
            return result;
        });

        var accent = StaRunner.Run(() =>
            (System.Windows.Application.Current.TryFindResource("LfBrushAccent") as SolidColorBrush)?.Color);
        var primary = StaRunner.Run(() =>
            (System.Windows.Application.Current.TryFindResource("LfBrushTextPrimary") as SolidColorBrush)?.Color);

        // 选中项背景不得是强调实色（那会与深色文字形成低对比）。
        background.ShouldNotBe(accent);
        textBrush.ShouldBe(primary);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Report_page_should_render_non_blank_screenshot(string paletteName)
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
    public void Report_light_and_dark_screenshots_should_differ()
    {
        var lightPath = StaRunner.Run(() => Render("Light"));
        var darkPath = StaRunner.Run(() => Render("Dark"));

        try
        {
            var light = StaRunner.Run(() => ReadPixels(lightPath));
            var dark = StaRunner.Run(() => ReadPixels(darkPath));
            light.Length.ShouldBe(dark.Length);

            var differing = light.Where((pixel, i) => pixel != dark[i]).Count();

            ((double)differing / light.Length).ShouldBeGreaterThan(0.05);
        }
        finally
        {
            File.Delete(lightPath);
            File.Delete(darkPath);
        }
    }

    private static void WaitFor(ref Task task)
    {
        while (!task.IsCompleted)
        {
            Pump();
            Thread.Sleep(10);
        }
    }

    private static Window CreateWindow(ReportPage page) =>
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

            using var harness = new ReportPageHarness();
            harness.History.Entries.Add(ReportPageHarness.SampleRun());

            var page = new ReportPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var load = harness.ViewModel.LoadAsync();
            WaitFor(ref load);
            var preview = harness.ViewModel.PreviewCommand.ExecuteAsync(null);
            WaitFor(ref preview);
            window.UpdateLayout();
            Pump();

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
            System.IO.Directory.CreateDirectory(directory);
            var finalFile = Path.Combine(directory, $"stage05-report-{paletteName.ToLowerInvariant()}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = File.Open(finalFile, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            // 读取唯一临时副本，避免并行渲染覆盖固定截图时产生文件锁竞争。
            var readFile = Path.Combine(
                Path.GetTempPath(),
                $"linkforge-report-{paletteName.ToLowerInvariant()}-{Guid.NewGuid():N}.png");

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
