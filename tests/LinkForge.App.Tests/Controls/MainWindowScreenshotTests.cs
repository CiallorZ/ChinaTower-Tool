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
        var path = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            // 必须构造真实的 App 实例：
            // MainWindow 读主题状态走的是 (Application.Current as App)?.ThemeManager，
            // 不构造 App 的话它是 null，状态栏文本会退化为硬编码的浅色。
            // 这条路径上的缺陷，只在有 App 上下文时才暴露。
            // StaRunner 已保证 Application.Current 存在。
            // 不能 new App()：WPF 限制同一 AppDomain 只能有一个 Application 实例。
            // 因此只在它恰好是 App 时才注入 ThemeManager；
            // 否则窗口读到的 ThemeManager 为 null，状态栏退化为浅色，但截图仍能验证颜色与布局。
            if (System.Windows.Application.Current is App app)
            {
                app.ThemeManager = new ThemeManager(app.Resources);
                app.ThemeManager.Apply(paletteName == "Dark"
                    ? LfThemePreference.Dark
                    : LfThemePreference.Light);
            }

            var window = new MainWindow
            {
                Width = ReferenceWidth,
                Height = ReferenceHeight
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

            var directory = Path.Combine(FindRepositoryRoot(), "screenshots");
            Directory.CreateDirectory(directory);

            var file = Path.Combine(directory, $"stage02-mainwindow-{paletteName.ToLowerInvariant()}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = File.Open(file, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(stream);
            }

            return file;
        });

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
