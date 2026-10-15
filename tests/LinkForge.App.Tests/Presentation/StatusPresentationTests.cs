using LinkForge.App.Presentation;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.Presentation;

/// <summary>
/// 状态→视觉映射测试（ADR-016 / ADR-019 的 UI 侧红线）。
/// </summary>
public class StatusPresentationTests
{
    [Fact]
    public void Skipped_must_not_be_rendered_as_failure()
    {
        // ADR-016 红线：Skipped 不计入失败。
        var map = StatusPresentation.ForDiagnosticStatus(DiagnosticStatus.Skipped);

        map.Visual.ShouldNotBe(LfStatusVisual.Danger);
        map.Visual.ShouldBe(LfStatusVisual.Neutral);
        map.AccessibleText.ShouldContain("未执行");
    }

    [Fact]
    public void Unknown_must_not_be_rendered_as_success_or_failure()
    {
        var map = StatusPresentation.ForDiagnosticStatus(DiagnosticStatus.Unknown);

        map.Visual.ShouldNotBe(LfStatusVisual.Success);
        map.Visual.ShouldNotBe(LfStatusVisual.Danger);
        map.Visual.ShouldBe(LfStatusVisual.Unknown);
    }

    [Fact]
    public void Task_inconclusive_must_not_be_rendered_as_completed_or_failed()
    {
        // ADR-019：无有效结论既不是完成也不是失败。
        var map = StatusPresentation.ForTaskRunState(TaskRunState.Inconclusive);

        map.Visual.ShouldNotBe(LfStatusVisual.Success);
        map.Visual.ShouldNotBe(LfStatusVisual.Danger);
        map.DisplayText.ShouldContain("无有效结论");
    }

    [Fact]
    public void Partially_succeeded_must_use_warning_not_danger()
    {
        var map = StatusPresentation.ForTaskRunState(TaskRunState.PartiallySucceeded);

        map.Visual.ShouldBe(LfStatusVisual.Warning);
        map.DisplayText.ShouldBe("部分成功");
    }

    [Fact]
    public void Missing_dependency_must_be_distinct_from_failure()
    {
        // UI 规范 §5.6：缺少依赖不能伪装成普通失败。
        var map = StatusPresentation.ForMissingDependency("Npcap");

        map.Visual.ShouldBe(LfStatusVisual.DependencyMissing);
        map.Visual.ShouldNotBe(LfStatusVisual.Danger);
        map.AccessibleText.ShouldContain("Npcap");
        map.AccessibleText.ShouldContain("不可用");
    }

    [Fact]
    public void Missing_dependency_without_name_should_still_be_actionable()
    {
        var map = StatusPresentation.ForMissingDependency(null);

        map.AccessibleText.ShouldContain("所需组件");
        map.DisplayText.ShouldBe("依赖缺失");
    }

    [Theory]
    [InlineData(DiagnosticStatus.Passed, LfStatusVisual.Success)]
    [InlineData(DiagnosticStatus.Failed, LfStatusVisual.Danger)]
    [InlineData(DiagnosticStatus.Warning, LfStatusVisual.Warning)]
    [InlineData(DiagnosticStatus.Skipped, LfStatusVisual.Neutral)]
    [InlineData(DiagnosticStatus.Unknown, LfStatusVisual.Unknown)]
    [InlineData(DiagnosticStatus.Cancelled, LfStatusVisual.Cancelled)]
    [InlineData(DiagnosticStatus.TimedOut, LfStatusVisual.Danger)]
    [InlineData(DiagnosticStatus.Running, LfStatusVisual.Running)]
    [InlineData(DiagnosticStatus.NotRun, LfStatusVisual.Neutral)]
    public void Every_diagnostic_status_should_map_to_expected_visual(
        DiagnosticStatus status,
        LfStatusVisual expected)
    {
        StatusPresentation.ForDiagnosticStatus(status).Visual.ShouldBe(expected);
    }

    [Theory]
    [InlineData(TaskRunState.Pending, LfStatusVisual.Neutral)]
    [InlineData(TaskRunState.Preparing, LfStatusVisual.Running)]
    [InlineData(TaskRunState.Running, LfStatusVisual.Running)]
    [InlineData(TaskRunState.Completed, LfStatusVisual.Success)]
    [InlineData(TaskRunState.PartiallySucceeded, LfStatusVisual.Warning)]
    [InlineData(TaskRunState.Failed, LfStatusVisual.Danger)]
    [InlineData(TaskRunState.Cancelled, LfStatusVisual.Cancelled)]
    [InlineData(TaskRunState.Inconclusive, LfStatusVisual.Unknown)]
    public void Every_task_state_should_map_to_expected_visual(
        TaskRunState state,
        LfStatusVisual expected)
    {
        StatusPresentation.ForTaskRunState(state).Visual.ShouldBe(expected);
    }

    [Fact]
    public void Every_status_should_provide_icon_text_and_accessible_text()
    {
        // UI 规范 §5.4/§6：不得仅靠颜色表达状态，必须有图标与文字。
        foreach (var status in Enum.GetValues<DiagnosticStatus>())
        {
            var map = StatusPresentation.ForDiagnosticStatus(status);
            AssertComplete(map, $"步骤状态 {status}");
        }

        foreach (var state in Enum.GetValues<TaskRunState>())
        {
            var map = StatusPresentation.ForTaskRunState(state);
            AssertComplete(map, $"任务状态 {state}");
        }
    }

    [Fact]
    public void Token_suffix_should_only_reference_existing_token_groups()
    {
        // 视觉语义必须映射到真实存在的 LfBrush* 令牌组，否则渲染时取不到颜色。
        string[] known = ["Success", "Running", "Warning", "Danger", "Unknown", "DependencyMissing"];

        foreach (var visual in Enum.GetValues<LfStatusVisual>())
        {
            var suffix = StatusPresentation.TokenSuffix(visual);
            known.ShouldContain(suffix, $"视觉语义 {visual} 映射到了不存在的令牌组 {suffix}");
        }
    }

    [Fact]
    public void Distinct_visuals_should_have_distinct_icons()
    {
        // 图标是除颜色之外的第二重区分手段；不同语义不应共用同一图标，
        // 否则色觉障碍用户无法区分（UI 规范 §6）。
        var diagnostics = Enum.GetValues<DiagnosticStatus>()
            .Select(s => StatusPresentation.ForDiagnosticStatus(s))
            .ToList();

        diagnostics.Select(d => d.IconGlyph).Distinct().Count()
            .ShouldBeGreaterThanOrEqualTo(6);
    }

    private static void AssertComplete(StatusVisualMap map, string label)
    {
        map.IconGlyph.ShouldNotBeNullOrWhiteSpace($"{label} 缺少图标");
        map.DisplayText.ShouldNotBeNullOrWhiteSpace($"{label} 缺少显示文案");
        map.AccessibleText.ShouldNotBeNullOrWhiteSpace($"{label} 缺少可访问描述");
        map.DisplayText.Length.ShouldBeLessThanOrEqualTo(
            8,
            $"{label} 的标签文案过长（{map.DisplayText}），会破坏标签布局一致性。");
    }
}
