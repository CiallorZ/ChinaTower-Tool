using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.Tests.ViewModels;
using LinkForge.App.ViewModels;
using LinkForge.App.Views.Pages;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 工作台页面的真实渲染与截图（阶段 04 验收证据）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="DashboardViewModelTests"/> 的分工：那里验证"数据是真的"，
/// 这里验证"页面真的画出来了、两个主题都画得出来"。
/// 两者缺一不可 —— 数据对但渲染崩，或渲染正常但绑定全落空，都算没交付。
/// </para>
/// <para>
/// 遵循 ADR-023：截图必须显式设定被测状态，并固定窗口位置。
/// </para>
/// </remarks>
public class DashboardPageTests
{
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Fact]
    public void Dashboard_page_should_survive_a_full_layout_pass()
    {
        var failure = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var harness = new DashboardHarness();
            var page = new DashboardPage { DataContext = harness.ViewModel };
            var window = new Window { Content = page, Width = ReferenceWidth, Height = ReferenceHeight };

            try
            {
                window.Show();
                Pump();
                window.Measure(new Size(ReferenceWidth, ReferenceHeight));
                window.Arrange(new Rect(0, 0, ReferenceWidth, ReferenceHeight));
                window.UpdateLayout();

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
                harness.Dispose();
            }
        });

        // Shouldly 的断言消息参数数量因重载而异，这里显式抛出以带上原始异常。
        if (failure is not null)
        {
            throw new InvalidOperationException("工作台页面在加载/渲染阶段抛出异常。", failure);
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Dashboard_page_should_render_a_non_blank_bitmap(string paletteName)
    {
        var path = StaRunner.Run(() => Render(paletteName));

        var info = new FileInfo(path);
        info.Exists.ShouldBeTrue($"截图未生成：{path}");
        info.Length.ShouldBeGreaterThan(0, $"截图为空文件：{path}");

        var opaque = StaRunner.Run(() => CountOpaquePixels(path));

        opaque.ShouldBeGreaterThan(
            ReferenceWidth * ReferenceHeight / 2,
            $"{paletteName} 主题下工作台截图几乎全透明，页面可能没有真正绘制内容。");
    }

    [Fact]
    public void Light_and_dark_screenshots_should_differ()
    {
        // 只断言"文件非空"不够：主题切换失效时两张图会一模一样，
        // 而两个用例仍然全绿。
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
            "工作台浅色与深色截图几乎完全相同，说明主题切换没有真正生效。");
    }

    private static string Render(string paletteName)
    {
        ThemedHost.EnsureResources();

        // 显式设定主题（ADR-023）：不得依赖上一个测试残留的调色板。
        var themeManager = new ThemeManager(System.Windows.Application.Current.Resources);
        themeManager.Apply(paletteName == "Dark" ? LfThemePreference.Dark : LfThemePreference.Light);

        using var harness = new DashboardHarness();

        var page = new DashboardPage { DataContext = harness.ViewModel };

        var window = new Window
        {
            Content = page,
            Width = ReferenceWidth,
            Height = ReferenceHeight,

            // 固定位置：ClearType 子像素渲染的结果取决于窗口落在屏幕的哪个像素相位上，
            // 不固定会让同一主题的两张截图产生无意义的差异。
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0
        };

        window.Show();
        Pump();

        window.Measure(new Size(ReferenceWidth, ReferenceHeight));
        window.Arrange(new Rect(0, 0, ReferenceWidth, ReferenceHeight));
        window.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            ReferenceWidth, ReferenceHeight, 96, 96, PixelFormats.Pbgra32);

        // 必须在窗口仍可见时渲染：Close() 之后视觉树断开，只会输出全透明像素。
        bitmap.Render(window);
        bitmap.Freeze();

        window.Close();
        Pump();

        var directory = Path.Combine(FindRepositoryRoot(), "screenshots");
        Directory.CreateDirectory(directory);

        var file = Path.Combine(
            directory,
            $"stage04-dashboard-{paletteName.ToLowerInvariant()}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Open(file, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);

        return file;
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
