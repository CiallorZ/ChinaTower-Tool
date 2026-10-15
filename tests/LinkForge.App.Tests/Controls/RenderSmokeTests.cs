using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Tests.DesignTokens;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 渲染冒烟测试：强制走完一次真实布局，捕获"构建通过但运行时炸"的缺陷。
/// </summary>
/// <remarks>
/// 真实缺陷记录（阶段 02，运行时崩溃捕获）：
/// 控件模板内的 <c>{StaticResource Lf*}</c> 在 <c>dotnet build</c> 下完全正常，
/// 但运行时走到 <c>ApplyTemplate</c> 时抛 XamlParseException
/// "无法找到名为 LfFontSizeCaption 的资源"。
///
/// 根因：模板是延迟实例化的，<c>LoadContent</c> 阶段模板根元素尚未进入逻辑树，
/// <c>StaticResource</c> 沿树向上查找会落空。
/// 仅调用 <c>ApplyTemplate()</c> 也不足以暴露——必须先让元素进入布局。
///
/// 因此这里统一走"进窗口 -> Measure -> Arrange -> UpdateLayout"，
/// 与运行时渲染路径一致。
/// </remarks>
public class RenderSmokeTests
{
    public static TheoryData<Type> ComponentTypes => new()
    {
        typeof(StatusBadge),
        typeof(EmptyState),
        typeof(LoadingState),
        typeof(ErrorPanel),
        typeof(DependencyMissingPanel),
        typeof(TaskProgressPanel),
        typeof(ResultSummary),
        typeof(LabeledTextBox),
        typeof(SecretTextBox),
        typeof(LogViewer)
    };

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void Component_should_survive_a_full_layout_pass(Type componentType)
    {
        // 只要模板解析抛异常，StaRunner.Run 就会把异常抛出来，测试即失败。
        StaRunner.Run(() =>
        {
            var control = (Control)Activator.CreateInstance(componentType)!;

            if (componentType == typeof(ResultSummary))
            {
                // 结论摘要必须有内容才能触发项模板实例化，
                // 空模板不会暴露 DataTemplate 内的问题。
                ((ResultSummary)control).Items =
                    new System.Collections.ObjectModel.ObservableCollection<ResultSummaryItem>
                    {
                        new() { Label = "目标", Value = "10.0.0.1" },
                        new() { Label = "连通性", Value = "通过", Status = LinkForge.Domain.Diagnostics.DiagnosticStatus.Passed }
                    };
            }

            var window = ThemedHost.Host(control);
            Layout(window);
        });
    }

    [Fact]
    public void Log_viewer_rows_should_render_without_throwing()
    {
        // 日志行必须真正生成容器，否则项模板的缺陷测不出来。
        StaRunner.Run(() =>
        {
            var viewer = new LogViewer
            {
                Entries =
                [
                    new LogEntry(DateTimeOffset.Now, LfLogLevel.Information, "Network", "run", null, "connected"),
                    new LogEntry(DateTimeOffset.Now, LfLogLevel.Error, "Network", "run", null, "refused")
                ]
            };

            var window = ThemedHost.Host(viewer);
            Layout(window);

            var list = FindListBox(viewer);
            list.ShouldNotBeNull("未找到日志列表。");
        });
    }

    /// <summary>
    /// 真正的生产路径守卫：直接加载 <c>MainWindow</c> 并真正显示。
    /// </summary>
    /// <remarks>
    /// 两个容易被跳过、但缺一不可的要素：
    ///
    /// 1. 必须实例化 <c>MainWindow</c> 本身，而不是把控件挂在测试自己构造的宿主窗口里。
    ///    阶段 02 的真实崩溃（"无法找到名为 LfBorderThickness 的资源"）就发生在这条路径上：
    ///    <c>MainWindow.xaml</c> 在 Window 自身上就引用了 <c>LfWindowMinWidth</c> 等令牌，
    ///    其求值时机与挂在宿主窗口里的控件并不相同。
    ///
    /// 2. 必须调用 <c>Show()</c> 并清空 Dispatcher 队列。只做 Measure/Arrange 不够：
    ///    部分资源引用要等窗口真正进入呈现阶段才求值。这一点已用 Mutation 验证 ——
    ///    把某个令牌改回 StaticResource 后，不 Show 的测试依然全绿，Show 之后立刻失败。
    ///
    /// 缺任何一条，这条测试都会退化成"看起来在测、其实测不到"的摆设。
    /// </remarks>
    [Fact]
    public void MainWindow_should_survive_a_full_layout_pass()
    {
        Exception? failure = null;

        StaRunner.Run(() =>
        {
            ThemedHost.EnsureResources();

            var window = new MainWindow();
            var settled = false;

            window.Dispatcher.UnhandledException += (_, args) =>
            {
                failure = args.Exception;
                args.Handled = true;
            };

            window.Loaded += (_, _) =>
            {
                try
                {
                    Layout(window);
                    window.UpdateLayout();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    settled = true;
                    window.Close();
                }
            };

            window.Show();

            // Show() 只把渲染消息入队，必须让 Dispatcher 真正跑完这批消息。
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => { }));

            for (var i = 0; i < 60 && !settled; i++)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => { }));
            }

            if (!settled && window.IsLoaded)
            {
                try
                {
                    Layout(window);
                    window.UpdateLayout();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            if (window.IsVisible)
            {
                window.Close();
            }

            _ = frame;
        });

        if (failure is not null)
        {
            throw new InvalidOperationException("MainWindow 在加载/渲染阶段抛出异常。", failure);
        }
    }

    private static void Layout(Window window)
    {
        window.Width = 900;
        window.Height = 600;
        window.Measure(new Size(900, 600));
        window.Arrange(new Rect(0, 0, 900, 600));
        window.UpdateLayout();
    }

    private static ListBox? FindListBox(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is ListBox list)
            {
                return list;
            }

            if (FindListBox(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
