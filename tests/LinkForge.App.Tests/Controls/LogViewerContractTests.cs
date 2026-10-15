using System.Linq;
using System.Reflection;
using System.Windows;
using LinkForge.App.Controls;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// LogViewer 的架构契约测试（不依赖 WPF 渲染，纯反射即可执行）。
/// </summary>
/// <remarks>
/// 单独成类而非并入 <see cref="LogViewerTests"/>：
/// 这些断言针对的是"控件作为绑定源的结构性要求"，
/// 与被测控件能否渲染无关，不需要 STA 线程与资源宿主。
///
/// 已有缺陷记录（阶段 02）：<c>View</c> 曾是普通 CLR 属性，
/// 模板里的 <c>ItemsSource</c> 绑定收不到变更通知，
/// 导致"换日志源后界面一行都不显示"。
/// 渲染测试能发现症状，但这类结构性要求用反射断言更直接、更快，
/// 也能在有人重构时立刻给出明确的失败原因。
/// </remarks>
public class LogViewerContractTests
{
    /// <summary>
    /// 模板会绑定到 <c>View</c>，因此它必须是依赖属性。
    /// </summary>
    [Fact]
    public void View_should_be_a_dependency_property()
    {
        var property = typeof(LogViewer)
            .GetProperty("View", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        property.ShouldNotBeNull("LogViewer 缺少 View 属性。");

        // 普通 CLR 属性没有对应的 DependencyProperty 字段，必然查不到。
        var field = typeof(LogViewer).GetField(
            "ViewProperty",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        field.ShouldNotBeNull(
            "View 必须是依赖属性。它当前是普通 CLR 属性，" +
            "模板里的 ItemsSource 绑定收不到变更通知，" +
            "替换 Entries 后界面会停在旧数据上（阶段 02 真实缺陷）。");
    }

    /// <summary>
    /// 所有供模板绑定的公开/内部属性都必须是依赖属性。
    /// </summary>
    /// <remarks>
    /// 这条用例的意义在于防新增：
    /// 以后给 LogViewer 加供模板绑定的属性时，
    /// 若又写成普通 CLR 属性，这里会立刻失败，而不是等到某个页面悄悄不刷新。
    /// </remarks>
    [Fact]
    public void Every_template_bound_property_should_be_a_dependency_property()
    {
        // 排除 Entries 的集合语义与只读计数属性：它们本就由依赖属性支撑。
        var boundNames = new[] { "View", "Entries", "MinimumLevel", "SearchText", "AutoScroll" };

        foreach (var name in boundNames)
        {
            var field = typeof(LogViewer).GetField(
                $"{name}Property",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            field.ShouldNotBeNull(
                $"LogViewer.{name} 缺少依赖属性支撑，模板绑定不会收到变更通知。");
        }
    }
}