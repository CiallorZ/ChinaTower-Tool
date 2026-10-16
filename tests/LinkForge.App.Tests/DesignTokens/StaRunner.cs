using System.Windows;
using System.Windows.Threading;

namespace LinkForge.App.Tests.DesignTokens;

/// <summary>
/// 在带 WPF <see cref="Application"/> 的 STA 线程上执行委托。
/// </summary>
/// <remarks>
/// 两点必须同时满足，否则测试结果与运行时不一致：
/// <list type="number">
///   <item>STA 线程：WPF 的 XAML 解析与 <c>Freezable</c>（Brush）构造要求 STA；</item>
///   <item>存在 <see cref="Application"/> 实例：<c>pack://application:,,,/Assembly;component/...</c>
///         URI 依赖应用程序上下文才能解析，否则报 "The URI prefix is not recognized"。</item>
/// </list>
/// 因此不能只起一个裸 STA 线程，必须创建一个最小 <see cref="Application"/> 宿主。
/// </remarks>
internal static class StaRunner
{
    private static readonly Lazy<Dispatcher> DispatcherLazy = new(CreateDispatcher);

    internal static void Run(Action action) => Run<object?>(() =>
    {
        action();
        return null;
    });

    internal static T Run<T>(Func<T> func)
    {
        var dispatcher = DispatcherLazy.Value;
        T? result = default;
        Exception? failure = null;

        dispatcher.Invoke(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (failure is not null)
        {
            throw new InvalidOperationException($"WPF 测试线程执行失败：{failure.Message}", failure);
        }

        return result!;
    }

    private static Dispatcher CreateDispatcher()
    {
        using var ready = new ManualResetEventSlim(false);
        Dispatcher? dispatcher = null;
        Exception? startupFailure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // Application 只需存在，不调用 Run()；它提供 pack URI 解析所需的应用程序上下文。
                //
                // ShutdownMode 必须改为 OnExplicitShutdown：默认是 OnLastWindowClose，
                // 而渲染冒烟测试会 Show 一个真实窗口再 Close 它。
                // 默认模式下这会被判定为"最后一个窗口关闭"从而关停整个 Application，
                // 之后 Application.Current 变成 null，其余用例全线 NullReferenceException。
                // 测试宿主的生命周期由进程控制，不该由被测窗口控制。
                // 必须是基类 Application 而不是 LinkForge.App.App：
                // 后者在构造后会调度 OnStartup，而 OnStartup 会调用 ThemeManager.Apply，
                // 在没有合并生产字典的测试宿主里会抛"未找到调色板"。
                // 基类没有 OnStartup 逻辑，安全性更高；
                // MainWindow 读取 ThemeManager 的路径由 Screenshot 测试单独验证。
                _ = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                startupFailure = ex;
            }
            finally
            {
                ready.Set();
            }

            if (dispatcher is not null)
            {
                Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
            Name = "LinkForge.WpfTestThread"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue("WPF 测试线程未能在 30 秒内启动。");

        if (startupFailure is not null)
        {
            throw new InvalidOperationException("无法创建 WPF 测试宿主。", startupFailure);
        }

        return dispatcher ?? throw new InvalidOperationException("WPF 测试线程未能提供 Dispatcher。");
    }
}
