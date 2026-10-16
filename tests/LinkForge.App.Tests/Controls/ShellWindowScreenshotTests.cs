using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.Tests.ViewModels;
using LinkForge.App.ViewModels;
using LinkForge.App.Views;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 真实渲染 <c>ShellWindow</c> 并把浅/深色截图落到 <c>screenshots/</c>（阶段 03 验收证据）。
/// </summary>
/// <remarks>
/// <para>
/// 用 <see cref="RenderTargetBitmap"/> 渲染视觉树而不是屏幕截图：
/// 屏幕截图依赖窗口焦点、分辨率与前台状态，换台机器就对不上，也无法在 CI 里跑。
/// </para>
/// <para>
/// 与 <see cref="ShellWindowTests"/> 的区别：那里断言"内容绑上了、导航能切"，
/// 这里额外产出可供人工比对设计稿的图片，并断言"两个主题都真的画出了非空画面"。
/// 截图有副作用（写文件）仍保留在测试中，因为生成过程本身就是验证过程。
/// </para>
/// </remarks>
public class ShellWindowScreenshotTests
{
    // 与规范 §3.1 的参考分辨率一致。
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Shell_window_should_render_a_non_blank_bitmap(string paletteName)
    {
        var path = StaRunner.Run(() => Render(paletteName));

        var info = new FileInfo(path);
        info.Exists.ShouldBeTrue($"截图未生成：{path}");
        info.Length.ShouldBeGreaterThan(0, $"截图为空文件：{path}");

        // 位图有内容但全透明，等于什么都没画出来。
        var opaque = StaRunner.Run(() => CountOpaquePixels(path));

        opaque.ShouldBeGreaterThan(
            ReferenceWidth * ReferenceHeight / 2,
            $"{paletteName} 主题截图几乎全透明，窗口可能没有真正绘制内容。");
    }

    [Fact]
    public void Light_and_dark_screenshots_should_differ()
    {
        // 只断言"文件非空"是不够的：如果主题切换失效，
        // 两张图会一模一样，而两个用例仍然全绿。
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
            "浅色与深色截图几乎完全相同，说明主题切换没有真正生效。");
    }

    private static string Render(string paletteName)
    {
        ThemedHost.EnsureResources();

        // 用与生产相同的入口切换主题：ThemeManager 替换调色板字典。
        var themeManager = new ThemeManager(System.Windows.Application.Current.Resources);
        themeManager.Apply(paletteName == "Dark" ? LfThemePreference.Dark : LfThemePreference.Light);

        // 必须把同一个 ThemeManager 交给 ViewModel：
        // 否则状态栏会显示"跟随系统"，而画面实际是强制浅色/深色 ——
        // 截图是阶段验收证据，里面的文字与实际状态不符会误导评审。
        // 生产设置文件路径：仅用于让工作台的"配置位置"一行显示真实值，
        // 与 App.xaml.cs 的接线一致（截图上不该显示"未知"）。
        var productionSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LinkForge",
            "settings.json");

        // 壳层自身的状态则用临时设置文件：截图必须可复现，
        // 不能因为开发机当前恰好把日志面板拖开而每张图都不一样。
        var scratchDirectory = Path.Combine(
            Path.GetTempPath(),
            "linkforge-screenshot-" + Guid.NewGuid().ToString("N"));

        var window = new ShellWindow(new ShellViewModel(
            themeManager,
            new AppSettingsStore(Path.Combine(scratchDirectory, "settings.json")),
            new DashboardHarness().ViewModel))
        {
            Width = ReferenceWidth,
            Height = ReferenceHeight,

            // 固定屏幕位置：ClearType 是子像素渲染，文字的抗锯齿结果取决于窗口落在
            // 屏幕的哪个像素相位上。默认 Manual + 系统级联会让每个窗口落在不同位置，
            // 于是同一主题、同一内容的两张截图会产生约 0.9% 的像素差异 ——
            // 截图是验收证据，必须字节可复现，否则"这张图对不对"无从比对。
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0
        };

        window.Show();

        for (var i = 0; i < 60; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
        }

        window.Measure(new Size(ReferenceWidth, ReferenceHeight));
        window.Arrange(new Rect(0, 0, ReferenceWidth, ReferenceHeight));
        window.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            ReferenceWidth,
            ReferenceHeight,
            96,
            96,
            PixelFormats.Pbgra32);

        // 必须在窗口仍然可见时渲染：Close() 之后视觉树被断开，只会输出全透明像素。
        bitmap.Render(window);
        bitmap.Freeze();

        window.Close();

        for (var i = 0; i < 60; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
        }

        var directory = Path.Combine(FindRepositoryRoot(), "screenshots");
        Directory.CreateDirectory(directory);

        var file = Path.Combine(
            directory,
            $"stage03-shellwindow-{paletteName.ToLowerInvariant()}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Open(file, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);

        return file;
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
