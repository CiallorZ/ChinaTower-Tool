using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 真实渲染 <c>MainWindow</c> 并把浅/深色截图落到 <c>screenshots/</c>。
/// </summary>
/// <remarks>
/// 刻意用 <see cref="RenderTargetBitmap"/> 渲染视觉树，而不是屏幕截图：
/// 屏幕截图依赖窗口焦点、屏幕分辨率与前台状态，
/// 换台机器就可能对不上，也无法在 CI 里跑。
/// 直接渲染视觉树则与设备无关，结果可复现。
///
/// 与 <see cref="RenderSmokeTests.MainWindow_should_survive_a_full_layout_pass"/>
/// 的区别：冒烟测试只断言"不抛异常"，
/// 这里进一步断言"真的画出了非空位图"——
/// 全部元素都不可见的窗口同样能通过冒烟测试，但产品是坏的。
///
/// 有副作用（写文件）仍然保留在测试中：
/// 阶段验收需要浅/深两张截图作为证据，
/// 而它的生成过程本身就是"两个主题都能渲染"的验证。
/// </remarks>
public class MainWindowScreenshotTests
{
    // 与规范 §3.1 的参考分辨率一致，便于按设计基准比对。
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 800;

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void MainWindow_should_render_a_non_blank_bitmap(string paletteName)
    {
        var path = StaRunner.Run(() => Render(paletteName));

        var info = new FileInfo(path);
        info.Exists.ShouldBeTrue($"截图未生成：{path}");
        info.Length.ShouldBeGreaterThan(0, $"截图为空文件：{path}");

        // 位图有内容但全透明，等于什么都没画出来。
        var opaque = StaRunner.Run(() =>
        {
            var decoder = BitmapDecoder.Create(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            var frame = decoder.Frames[0];
            var stride = frame.PixelWidth * 4;
            var pixels = new byte[stride * frame.PixelHeight];
            frame.CopyPixels(pixels, stride, 0);

            var opaqueCount = 0;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 0)
                {
                    opaqueCount++;
                }
            }

            return opaqueCount;
        });

        opaque.ShouldBeGreaterThan(
            ReferenceWidth * ReferenceHeight / 2,
            $"{paletteName} 主题截图几乎全透明，窗口可能没有真正绘制内容。");
    }

    /// <summary>
    /// 用指定调色板渲染 <see cref="MainWindow"/>，落盘截图并返回文件路径。
    /// </summary>
    /// <remarks>
    /// 抽成独立方法是为了让"非空"与"两主题不同"两个用例走同一条渲染路径：
    /// 若各写一份，修好一处而漏改另一处时会得到互相矛盾的证据。
    /// </remarks>
    private static string Render(string paletteName)
    {
        ThemedHost.EnsureResources();

        // 先把 WPF-UI 自身主题显式归位：
        // ApplicationThemeManager 是进程级全局状态，其他测试显示过 FluentWindow 之后
        // 会残留深色资源，导致本用例的输出依赖"谁先跑"，证据不可复现。
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);

        // 用与生产相同的入口切换主题：ThemeManager 替换调色板字典。
        var themeManager = new ThemeManager(System.Windows.Application.Current.Resources);

        themeManager.Apply(paletteName == "Dark"
            ? LfThemePreference.Dark
            : LfThemePreference.Light);

        var window = new MainWindow
        {
            Width = ReferenceWidth,
            Height = ReferenceHeight,

            // 固定屏幕位置：ClearType 是子像素渲染，文字的抗锯齿结果取决于窗口落在
            // 屏幕的哪个像素相位上。默认 Manual + 系统级联会让每个窗口落在不同位置，
            // 于是同一主题、同一内容的两张截图会产生约 0.9% 的像素差异 ——
            // 截图是验收证据，必须字节可复现，否则"这张图对不对"无从比对。
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,

            // 必须显式注入，不能依赖 (Application.Current as App)?.ThemeManager：
            // 测试宿主里的 Application.Current 是基类 Application（见 StaRunner），
            // 那条路径恒为 null，状态栏会永远显示"浅色"，
            // 于是"浅色截图"与"深色截图"其实都是同一个残留主题，
            // 两张图字节相同而用例全绿。
            ThemeManager = themeManager
        };

        var settled = false;
        RenderTargetBitmap? bitmap = null;

        window.Loaded += (_, _) =>
        {
            try
            {
                window.Measure(new Size(ReferenceWidth, ReferenceHeight));
                window.Arrange(new Rect(0, 0, ReferenceWidth, ReferenceHeight));
                window.UpdateLayout();

                // 必须在窗口仍然可见时渲染：
                // Close() 之后视觉树被断开，RenderTargetBitmap 只会输出全透明像素。
                bitmap = new RenderTargetBitmap(
                    ReferenceWidth,
                    ReferenceHeight,
                    96,
                    96,
                    PixelFormats.Pbgra32);
                bitmap.Render(window);
                bitmap.Freeze();
            }
            finally
            {
                settled = true;
                window.Close();
            }
        };

        window.Show();

        for (var i = 0; i < 60 && !settled; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
        }

        if (window.IsVisible)
        {
            window.Close();
        }

        var directory = ScreenshotArtifacts.Directory;
        Directory.CreateDirectory(directory);

        var file = Path.Combine(directory, $"stage02-mainwindow-{paletteName.ToLowerInvariant()}.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var stream = File.Open(file, FileMode.Create, FileAccess.Write))
        {
            encoder.Save(stream);
        }

        return file;
    }
    [Fact]
    public void Light_and_dark_screenshots_should_differ()
    {
        // 只断言"文件非空"是不够的。
        //
        // 真实缺陷（阶段 03 发现，阶段 02 遗留）：本用例原本只在
        // `Application.Current is App` 时注入 ThemeManager，
        // 而测试宿主里的 Application.Current 是基类 Application（见 StaRunner），
        // 该分支永不成立 —— 两张"不同主题"的截图实际是同一个残留调色板，
        // 字节完全相同，用例却全绿。
        //
        // 因此这里补上行为断言：两个主题必须真的画出不同的像素。
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
