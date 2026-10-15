using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Tests.DesignTokens;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.Controls;

/// <summary>
/// 基础组件样式契约测试（UI 规范 §5、§10）。
/// </summary>
/// <remarks>
/// 为什么需要这类测试：<c>dotnet build</c> 能发现 XAML 语法错误，
/// 但发现不了"模板里引用了不存在的资源键"、"控件样式没注册到应用资源"
/// 这类问题——它们只在实例化时才暴露。
///
/// 真实缺陷记录（阶段 01）：控件样式漏合并字典时，构建完全正常，
/// 运行时才回退成无样式外观。因此这里逐个实例化组件并强制应用模板。
/// </remarks>
public class ComponentStylesTests
{
    /// <summary>每个基础组件都必须有默认样式（供 Theory 逐项验证）。</summary>
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

    /// <summary>组件类型列表，供遍历型用例使用。</summary>
    /// <remarks>
    /// 单独定义一份而不从 <see cref="ComponentTypes"/> 取值：
    /// <c>TheoryData</c> 的行项是 <c>object?[]</c>，遍历时要拆箱，
    /// 既难读又容易在新增类型时漏改。
    /// </remarks>
    private static readonly Type[] AllComponentTypes =
    [
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
    ];

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void Every_component_should_have_a_default_style(Type componentType)
    {
        var style = StaRunner.Run(() =>
        {
            var control = (Control)Activator.CreateInstance(componentType)!;
            ThemedHost.Host(control);

            return control.Style ?? Application.Current.TryFindResource(componentType);
        });

        style.ShouldNotBeNull(
            $"{componentType.Name} 没有默认样式。请确认 Themes/Controls/*.xaml 已合并进 App.xaml。" +
            "缺少样式的控件会显示为空白。");
    }

    [Theory]
    [MemberData(nameof(ComponentTypes))]
    public void Every_component_should_apply_a_non_empty_template(Type componentType)
    {
        // 有 Style 但模板是空的，表现同样是"什么都看不到"，必须单独断言。
        var visualCount = StaRunner.Run(() =>
        {
            var control = (Control)Activator.CreateInstance(componentType)!;
            ThemedHost.Host(control);

            return CountVisualChildren(control);
        });

        visualCount.ShouldBeGreaterThan(0, $"{componentType.Name} 的模板是空的。");
    }

    [Fact]
    public void Every_component_should_render_in_both_themes()
    {
        // 深色下缺色是最常见的回归（ADR-017/018 就是为了修这类问题）。
        var (light, dark) = StaRunner.Run(() => (
            RenderWithPalette("Light"),
            RenderWithPalette("Dark")));

        light.ShouldBe(AllComponentTypes.Length, "浅色主题下有组件未能渲染。");
        dark.ShouldBe(AllComponentTypes.Length, "深色主题下有组件未能渲染（多为色令牌缺失）。");
    }

    [Fact]
    public void Status_badge_should_render_all_nine_diagnostic_statuses()
    {
        // 九种步骤状态都必须能渲染；任一状态缺色会显示成透明，用户看不见。
        var resolved = StaRunner.Run(() =>
        {
            var results = new List<(string Status, string Text, string Glyph, Brush? Foreground, Brush? Background)>();

            foreach (var status in Enum.GetValues<DiagnosticStatus>())
            {
                var badge = new StatusBadge { DiagnosticStatus = status };
                ThemedHost.Host(badge);

                results.Add((status.ToString(), badge.ResolvedText, badge.ResolvedGlyph,
                    badge.ResolvedForeground, badge.ResolvedBackground));
            }

            return results;
        });

        resolved.Count.ShouldBe(9);

        foreach (var (status, text, glyph, foreground, background) in resolved)
        {
            text.ShouldNotBeNullOrWhiteSpace($"{status} 没有显示文案。");
            glyph.ShouldNotBeNullOrWhiteSpace($"{status} 没有图标（禁止仅靠颜色表达状态）。");
            foreground.ShouldNotBeNull($"{status} 的前景色令牌未解析到。");
            background.ShouldNotBeNull($"{status} 的背景色令牌未解析到。");
        }
    }

    [Fact]
    public void Status_badge_should_render_all_task_run_states()
    {
        var texts = StaRunner.Run(() =>
        {
            var results = new List<string>();

            foreach (var state in Enum.GetValues<TaskRunState>())
            {
                var badge = new StatusBadge { TaskState = state };
                ThemedHost.Host(badge);

                results.Add(badge.ResolvedText);
            }

            return results;
        });

        texts.ShouldAllBe(text => !string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void Empty_state_without_action_should_hide_the_action_area()
    {
        // 空状态里一个点了没反应的按钮比没有按钮更糟。
        var hasAction = StaRunner.Run(() =>
        {
            var empty = new EmptyState { Title = "暂无任务" };
            ThemedHost.Host(empty);

            return empty.HasAction;
        });

        hasAction.ShouldBeFalse();
    }

    [Fact]
    public void Dependency_missing_badge_should_not_use_failure_colors()
    {
        // 依赖缺失必须区别于失败（规范 §5.6），否则用户会去排查并不存在的故障。
        var (missingVisual, failureVisual) = StaRunner.Run(() =>
        {
            var missing = new StatusBadge { DependencyName = "Npcap" };
            var failed = new StatusBadge { DiagnosticStatus = DiagnosticStatus.Failed };

            ThemedHost.Host(missing);
            ThemedHost.Host(failed);

            return (missing.ResolvedVisual, failed.ResolvedVisual);
        });

        missingVisual.ShouldBe(LfStatusVisual.DependencyMissing);
        failureVisual.ShouldBe(LfStatusVisual.Danger);
    }

    [Fact]
    public void Task_progress_should_stop_showing_progress_at_terminal_states()
    {
        // 终态继续显示进度条会误导用户以为任务仍在运行。
        var activeStates = StaRunner.Run(() =>
        {
            var results = new Dictionary<TaskRunState, bool>();

            foreach (var state in Enum.GetValues<TaskRunState>())
            {
                var progress = new TaskProgressPanel { TaskState = state };
                ThemedHost.Host(progress);

                results[state] = progress.IsActive;
            }

            return results;
        });

        activeStates[TaskRunState.Preparing].ShouldBeTrue();
        activeStates[TaskRunState.Running].ShouldBeTrue();

        foreach (var state in new[]
        {
            TaskRunState.Completed, TaskRunState.Failed, TaskRunState.Cancelled,
            TaskRunState.Inconclusive, TaskRunState.PartiallySucceeded
        })
        {
            activeStates[state].ShouldBeFalse($"{state} 已到终态，不应仍显示进度。");
        }
    }

    /// <summary>
    /// 用指定调色板渲染全部组件，返回成功渲染的组件数。
    /// </summary>
    private static int RenderWithPalette(string paletteName)
    {
        ThemedHost.UsePalette(paletteName);

        var rendered = 0;

        foreach (var componentType in AllComponentTypes)
        {
            var control = (Control)Activator.CreateInstance(componentType)!;
            ThemedHost.Host(control);

            if (VisualTreeHelper.GetChildrenCount(control) > 0)
            {
                rendered++;
            }
        }

        return rendered;
    }

    private static int CountVisualChildren(DependencyObject parent)
    {
        var count = 0;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            count++;
            count += CountVisualChildren(VisualTreeHelper.GetChild(parent, i));
        }

        return count;
    }
}
