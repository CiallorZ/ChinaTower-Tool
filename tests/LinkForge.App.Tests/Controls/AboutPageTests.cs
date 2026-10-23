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

namespace LinkForge.App.Tests.Controls;

/// <summary>Rendering and accessibility checks for the V1.0 About page.</summary>
public sealed class AboutPageTests
{
    private static readonly object RenderLock = new();
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Initial_state_should_show_a_real_empty_state()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            var harness = new AboutHarness();
            var page = new AboutPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);

            window.Show();
            Pump();
            Layout(window);

            FindDescendants<EmptyState>(page)
                .ShouldContain(static state => state.Visibility == Visibility.Visible);

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Loaded_state_should_render_facts_and_accessible_actions()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            var harness = new AboutHarness();
            var page = new AboutPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var load = harness.ViewModel.LoadCommand.ExecuteAsync(null);
            WaitFor(load);
            window.UpdateLayout();
            Pump();

            FindDescendants<ItemsControl>(page)
                .ShouldContain(static control => control.Items.Count == 11);
            FindDescendants<TextBlock>(page)
                .ShouldContain(static text => text.Text == "LinkForge 视联网调试工具箱");

            var names = FindDescendants<Button>(page)
                .Select(static button => AutomationProperties.GetName(button))
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .ToArray();

            names.ShouldContain("重新读取版本与安装信息");
            names.ShouldContain("复制版本与诊断信息");

            var actionBar = (FrameworkElement)page.FindName("AboutActionBar")!;
            actionBar.Visibility.ShouldBe(Visibility.Visible);
            var actionBounds = actionBar
                .TransformToAncestor(window)
                .TransformBounds(new Rect(actionBar.RenderSize));
            actionBounds.Bottom.ShouldBeLessThanOrEqualTo(
                window.ActualHeight + 0.5,
                "关于页操作栏不得被滚动区域或窗口边缘裁切。");

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Loading_and_error_components_should_render_in_their_real_states()
    {
        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            var harness = new AboutHarness();
            var pending = new TaskCompletionSource<AboutSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            harness.Info.Behavior = _ => pending.Task;

            var page = new AboutPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var load = harness.ViewModel.LoadCommand.ExecuteAsync(null);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!harness.ViewModel.IsLoading && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(10);
            }

            FindDescendants<LoadingState>(page)
                .ShouldContain(static state => state.Visibility == Visibility.Visible);

            pending.SetException(new InvalidOperationException("版本读取失败"));
            WaitFor(load);
            window.UpdateLayout();
            Pump();

            FindDescendants<ErrorPanel>(page)
                .ShouldContain(static panel => panel.Visibility == Visibility.Visible);

            window.Close();
            Pump();
        });
    }

    [Fact]
    public void Shell_must_use_the_windows_per_monitor_v2_manifest()
    {
        var root = FindRepositoryRoot();
        var manifest = File.ReadAllText(
            Path.Combine(root, "src", "LinkForge.App", "app.manifest"));

        manifest.ShouldContain("PerMonitorV2, PerMonitor");
        manifest.ShouldContain("level=\"asInvoker\"");
        manifest.ShouldContain("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void About_page_should_render_a_non_blank_screenshot(string paletteName)
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
    public void Light_and_dark_about_screenshots_should_differ()
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

    private static Window CreateWindow(AboutPage page) =>
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

            var harness = new AboutHarness();
            var page = new AboutPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page);
            window.Show();
            Pump();
            Layout(window);

            var load = harness.ViewModel.LoadCommand.ExecuteAsync(null);
            WaitFor(load);
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
            Directory.CreateDirectory(directory);
            var finalFile = Path.Combine(
                directory,
                $"stage06-about-{paletteName.ToLowerInvariant()}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = File.Open(finalFile, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            var readFile = Path.Combine(
                Path.GetTempPath(),
                $"linkforge-about-{paletteName.ToLowerInvariant()}-{Guid.NewGuid():N}.png");
            using (var stream = File.Open(readFile, FileMode.Create, FileAccess.Write))
            {
                var readEncoder = new PngBitmapEncoder();
                readEncoder.Frames.Add(BitmapFrame.Create(bitmap));
                readEncoder.Save(stream);
            }

            return readFile;
        }
    }

    private static void WaitFor(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }

        task.IsCompleted.ShouldBeTrue("异步操作未在 5 秒内完成。");
        if (task.IsFaulted)
        {
            throw task.Exception!;
        }
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(ReferenceWidth, ReferenceHeight));
        window.Arrange(new Rect(0, 0, ReferenceWidth, ReferenceHeight));
        window.UpdateLayout();
    }

    private static void Pump()
    {
        for (var i = 0; i < 40; i++)
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

        throw new InvalidOperationException("未能找到仓库根目录。");
    }
}
