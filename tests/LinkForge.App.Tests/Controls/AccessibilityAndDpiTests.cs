using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkForge.App.Controls;
using LinkForge.App.Services;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.App.Tests.ViewModels;
using LinkForge.App.ViewModels;
using LinkForge.App.Views;
using LinkForge.App.Views.Pages;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 阶段 06 的自动化可访问性与高 DPI 验收。
/// </summary>
/// <remarks>
/// 这些测试验证 WPF 视觉树中的真实绑定和布局，而不是只检查 XAML 文本。
/// Windows 10 真机、屏幕阅读器人工验收和完整 WCAG 合规仍不能由本测试替代。
/// </remarks>
public class AccessibilityAndDpiTests
{
    private const int ReferenceWidth = 1366;
    private const int ReferenceHeight = 768;
    private static readonly object RenderLock = new();

    [Fact]
    public void Form_controls_should_expose_their_visible_labels_and_hints()
    {
        var report = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var labeled = new LabeledTextBox
            {
                Label = "目标地址",
                Hint = "只填写一个主机名或 IP。",
                IsRequired = true
            };
            var secret = new SecretTextBox
            {
                Label = "设备密码",
                Placeholder = "凭据不会写入日志。"
            };
            var panel = new StackPanel();
            panel.Children.Add(labeled);
            panel.Children.Add(secret);

            var window = ThemedHost.Host(panel);
            window.Width = 640;
            window.Height = 480;
            window.Show();
            Pump();
            window.UpdateLayout();

            var textBox = FindDescendants<TextBox>(labeled).FirstOrDefault();
            var passwordBox = FindDescendants<Wpf.Ui.Controls.PasswordBox>(secret).FirstOrDefault();

            var result = (
                TextName: textBox is null ? null : AutomationProperties.GetName(textBox),
                TextHint: textBox is null ? null : AutomationProperties.GetHelpText(textBox),
                TextRequired: textBox is null
                    ? false
                    : AutomationProperties.GetIsRequiredForForm(textBox),
                PasswordName: passwordBox is null ? null : AutomationProperties.GetName(passwordBox),
                WrapperFocusable: labeled.Focusable || secret.Focusable,
                InputFocusable: textBox?.Focusable == true);

            window.Close();
            Pump();
            return result;
        });

        report.TextName.ShouldBe("目标地址");
        report.TextHint.ShouldBe("只填写一个主机名或 IP。");
        report.TextRequired.ShouldBeTrue();
        report.PasswordName.ShouldBe("设备密码");
        report.WrapperFocusable.ShouldBeFalse("组合控件的标签容器不应抢走输入焦点。");
        report.InputFocusable.ShouldBeTrue("内部输入框必须可通过键盘聚焦。");
    }

    [Fact]
    public void Security_page_should_expose_names_for_inputs_and_page_owned_buttons()
    {
        var report = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            using var harness = new SecurityCenterHarness();
            var page = new SecurityCenterPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page, ReferenceWidth, ReferenceHeight);

            window.Show();
            Pump();
            window.UpdateLayout();

            var run = harness.ViewModel.RunCommand.ExecuteAsync(null);
            Drive(run);
            Pump();
            window.UpdateLayout();

            var inputProblems = new List<string>();
            foreach (var field in FindDescendants<LabeledTextBox>(page)
                         .Where(static item => item.Visibility == Visibility.Visible))
            {
                var input = FindDescendants<TextBox>(field).FirstOrDefault();
                if (input is null)
                {
                    inputProblems.Add($"{field.Label}: 未生成输入框");
                    continue;
                }

                var name = AutomationProperties.GetName(input);
                if (string.IsNullOrWhiteSpace(name) || name != field.Label)
                {
                    inputProblems.Add($"{field.Label}: AutomationProperties.Name={name ?? "<null>"}");
                }

                if (string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(input)))
                {
                    inputProblems.Add($"{field.Label}: 缺少 AutomationProperties.HelpText");
                }

                if (!input.IsTabStop)
                {
                    inputProblems.Add($"{field.Label}: 输入框不是 Tab 停靠点");
                }
            }

            var buttonProblems = new List<string>();
            foreach (var button in FindDescendants<Button>(page)
                         .Where(static item => item.Visibility == Visibility.Visible)
                         .Where(item => IsPageOwnedButton(item, page)))
            {
                if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                {
                    buttonProblems.Add(DescribeButton(button, page));
                }
            }

            var rawOutput = page.FindName("RawOutputTextBox") as TextBox;

            var result = (
                inputProblems,
                buttonProblems,
                HasRawOutputName: rawOutput is not null
                    && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(rawOutput)),
                HasResult: harness.ViewModel.HasResult);

            window.Close();
            Pump();
            return result;
        });

        report.inputProblems.ShouldBeEmpty(
            "安全页输入框缺少可访问名称或 Tab 支持：" + string.Join("; ", report.inputProblems));
        report.buttonProblems.ShouldBeEmpty(
            "安全页按钮缺少可访问名称：" + string.Join("; ", report.buttonProblems));
        report.HasResult.ShouldBeTrue("安全页必须进入真实结果状态后才验证复制/导出按钮。");
        report.HasRawOutputName.ShouldBeTrue("原始输出框必须提供可访问名称。");
    }

    [Fact]
    public void Shell_should_expose_accessible_names_for_text_actions()
    {
        var problems = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            var window = new ShellWindow(new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel));

            window.Show();
            Pump();
            Layout(window, ReferenceWidth, ReferenceHeight);

            var expectedContents = new[] { "跟随系统", "浅色", "深色", "日志面板" };
            var result = new List<string>();

            foreach (var content in expectedContents)
            {
                var button = FindDescendants<Button>(window)
                    .FirstOrDefault(item => string.Equals(item.Content as string, content,
                        StringComparison.Ordinal));

                if (button is null)
                {
                    result.Add($"{content}: 未找到按钮");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                {
                    result.Add($"{content}: 缺少 AutomationProperties.Name");
                }
            }

            window.Close();
            Pump();
            return result;
        });

        problems.ShouldBeEmpty("壳层文本按钮缺少可访问名称：" + string.Join("; ", problems));
    }

    [Fact]
    public void Shell_should_support_a_non_trapping_keyboard_focus_path()
    {
        var report = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            var window = new ShellWindow(new ShellViewModel(
                dashboard: new DashboardHarness().ViewModel));

            window.Show();
            Pump();
            Layout(window, ReferenceWidth, ReferenceHeight);
            window.Activate();
            Pump();

            var paneToggle = (Button)window.FindName("PaneToggleButton")!;
            paneToggle.Focus();
            Keyboard.Focus(paneToggle);
            Pump();

            var initialFocusWithin = paneToggle.IsKeyboardFocusWithin;
            var visited = new HashSet<DependencyObject> { paneToggle };
            IInputElement? current = paneToggle;

            for (var index = 0; index < 8; index++)
            {
                if (current is not UIElement element)
                {
                    break;
                }

                if (!element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
                {
                    break;
                }

                Pump();

                current = Keyboard.FocusedElement;
                if (current is DependencyObject focusedElement)
                {
                    visited.Add(focusedElement);
                }
            }

            var result = (
                initialFocusWithin,
                visited.Count,
                CurrentIsInsideWindow: Keyboard.FocusedElement is DependencyObject focused
                    && IsDescendantOrSelf(window, focused));

            window.Close();
            Pump();
            return result;
        });

        report.initialFocusWithin.ShouldBeTrue("首个壳层动作应能获得键盘焦点。");
        report.Count.ShouldBeGreaterThanOrEqualTo(
            4,
            "Tab 焦点不能在单个控件内循环；至少应跨越多个可操作区域。");
        report.CurrentIsInsideWindow.ShouldBeTrue("焦点移动后仍应留在壳层窗口内。");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Shell_should_render_non_blank_at_supported_dpi_scales(double scale)
    {
        var report = StaRunner.Run(() =>
        {
            lock (RenderLock)
            {
                ThemedHost.EnsureResources();
                var window = new ShellWindow(new ShellViewModel(
                    dashboard: new DashboardHarness().ViewModel))
                {
                    Width = ReferenceWidth,
                    Height = ReferenceHeight,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 0,
                    Top = 0
                };

                window.Show();
                Pump();
                Layout(window, ReferenceWidth, ReferenceHeight);

                var dpi = 96.0 * scale;
                var pixelWidth = (int)Math.Round(ReferenceWidth * scale);
                var pixelHeight = (int)Math.Round(ReferenceHeight * scale);
                var bitmap = new RenderTargetBitmap(
                    pixelWidth,
                    pixelHeight,
                    dpi,
                    dpi,
                    PixelFormats.Pbgra32);
                bitmap.Render(window);
                bitmap.Freeze();

                var result = (
                    BitmapWidth: bitmap.PixelWidth,
                    BitmapHeight: bitmap.PixelHeight,
                    OpaqueRatio: OpaqueRatio(bitmap),
                    LayoutWidth: window.ActualWidth);

                window.Close();
                Pump();
                return result;
            }
        });

        report.BitmapWidth.ShouldBe((int)Math.Round(ReferenceWidth * scale));
        report.BitmapHeight.ShouldBe((int)Math.Round(ReferenceHeight * scale));
        report.OpaqueRatio.ShouldBeGreaterThan(
            0.5,
            $"{scale * 100:0}% DPI 下窗口几乎全透明，可能没有完成真实绘制。");
        report.LayoutWidth.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Security_page_should_keep_primary_action_reachable_at_minimum_size()
    {
        var report = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            using var harness = new SecurityCenterHarness();
            var page = new SecurityCenterPage { DataContext = harness.ViewModel };
            var window = CreateWindow(page, 1024, 640);

            window.Show();
            Pump();
            window.UpdateLayout();

            var primary = FindDescendants<Button>(page)
                .FirstOrDefault(static button =>
                    button.Visibility == Visibility.Visible
                    && string.Equals(button.Content as string, "开始检查", StringComparison.Ordinal));

            if (primary is null)
            {
                window.Close();
                Pump();
                return (Found: false, Reachable: false, Scrollable: false);
            }

            primary.BringIntoView();
            Pump();
            window.UpdateLayout();

            var bounds = primary.TransformToAncestor(window).TransformBounds(
                new Rect(new Point(0, 0), primary.RenderSize));
            var scrollViewer = FindDescendants<ScrollViewer>(page).FirstOrDefault();

            var result = (
                Found: true,
                Reachable: bounds.Top >= -1 && bounds.Bottom <= window.ActualHeight + 1,
                Scrollable: scrollViewer is not null && scrollViewer.ViewportHeight > 0);

            window.Close();
            Pump();
            return result;
        });

        report.Found.ShouldBeTrue("最小窗口下仍必须找到主操作按钮。");
        report.Reachable.ShouldBeTrue("最小窗口下主操作按钮应能通过滚动到达，而不能被裁切。");
        report.Scrollable.ShouldBeTrue("页面必须保留可用滚动容器。");
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Security_page_should_remain_reachable_on_high_scale_viewports(double scale)
    {
        var report = StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();
            using var harness = new SecurityCenterHarness();
            var page = new SecurityCenterPage { DataContext = harness.ViewModel };

            // 用 2560x1440 的物理屏幕折算 DIP 视口，避免把 200% 缩放
            // 误测成 1366x768 物理屏幕上的不可用最小窗口。
            var viewportWidth = 2560.0 / scale;
            var viewportHeight = 1440.0 / scale;
            var window = CreateWindow(page, viewportWidth, viewportHeight);

            window.Show();
            Pump();
            window.UpdateLayout();

            var primary = FindDescendants<Button>(page)
                .FirstOrDefault(static button =>
                    button.Visibility == Visibility.Visible
                    && string.Equals(button.Content as string, "开始检查", StringComparison.Ordinal));

            if (primary is null)
            {
                window.Close();
                Pump();
                return (Found: false, Reachable: false, HasScrollViewer: false);
            }

            primary.BringIntoView();
            Pump();
            window.UpdateLayout();

            var bounds = primary.TransformToAncestor(window).TransformBounds(
                new Rect(new Point(0, 0), primary.RenderSize));
            var result = (
                Found: true,
                Reachable: bounds.Top >= -1 && bounds.Bottom <= window.ActualHeight + 1,
                HasScrollViewer: FindDescendants<ScrollViewer>(page).Any());

            window.Close();
            Pump();
            return result;
        });

        report.Found.ShouldBeTrue($"{scale * 100:0}% 缩放下未找到主操作按钮。");
        report.Reachable.ShouldBeTrue(
            $"{scale * 100:0}% 缩放下主操作按钮必须可滚动到达。");
        report.HasScrollViewer.ShouldBeTrue("安全页必须提供页面级滚动容器。");
    }

    private static Window CreateWindow(FrameworkElement page, double width, double height) =>
        new()
        {
            Content = page,
            Width = width,
            Height = height,
            MinWidth = 0,
            MinHeight = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0
        };

    private static void Layout(Window window, double width, double height)
    {
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
    }

    private static void Drive(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }

        task.IsCompleted.ShouldBeTrue("异步页面操作未在 5 秒内完成。");
        if (task.Exception is { } exception)
        {
            throw exception;
        }

        Pump();
    }

    private static void Pump()
    {
        for (var index = 0; index < 60; index++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
        }
    }

    private static double OpaqueRatio(RenderTargetBitmap bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var opaque = 0;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] > 0)
            {
                opaque++;
            }
        }

        return (double)opaque / (bitmap.PixelWidth * bitmap.PixelHeight);
    }

    private static bool IsDescendantOrSelf(DependencyObject root, DependencyObject candidate)
    {
        for (DependencyObject? current = candidate; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPageOwnedButton(Button button, DependencyObject page)
    {
        for (var current = VisualTreeHelper.GetParent(button); current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ComboBox
                or CheckBox
                or TextBox
                or PasswordBox
                or Wpf.Ui.Controls.TextBox
                or Wpf.Ui.Controls.PasswordBox)
            {
                return false;
            }

            if (ReferenceEquals(current, page))
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeButton(Button button, DependencyObject page)
    {
        var content = button.Content as string ?? button.Content?.ToString() ?? "<null>";
        return $"{content}: 缺少 AutomationProperties.Name ({button.GetType().Name})";
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

            foreach (var nested in FindDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
