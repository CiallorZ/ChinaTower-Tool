using System.IO;
using LinkForge.App.ViewModels;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

public class ReportPageViewModelTests
{
    [Fact]
    public async Task Empty_history_should_expose_empty_state_and_block_generation()
    {
        using var harness = new ReportPageHarness();

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.HasRuns.ShouldBeFalse();
        harness.ViewModel.IsLoading.ShouldBeFalse();
        harness.ViewModel.CanPreview.ShouldBeFalse();
        harness.ViewModel.CanSave.ShouldBeFalse();
        harness.ViewModel.EmptyDescription.ShouldContain("先");
    }

    [Fact]
    public async Task History_should_be_loaded_and_first_run_selected()
    {
        using var harness = new ReportPageHarness();
        var older = ReportPageHarness.SampleRun("older:443") with
        {
            StartedAt = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero)
        };
        var newer = ReportPageHarness.SampleRun("newer:443") with
        {
            StartedAt = new DateTimeOffset(2026, 10, 21, 1, 0, 0, TimeSpan.Zero)
        };

        harness.History.Entries.Add(older);
        harness.History.Entries.Add(newer);

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.HasRuns.ShouldBeTrue();
        harness.ViewModel.Runs.Count.ShouldBe(2);
        harness.ViewModel.Runs[0].TargetLabel.ShouldBe("newer:443");
        harness.ViewModel.SelectedRun.ShouldNotBeNull();
        harness.ViewModel.CanPreview.ShouldBeTrue();
        harness.ViewModel.Steps.Count.ShouldBe(2);
    }

    [Fact]
    public async Task History_load_failure_should_be_reported_without_throwing()
    {
        using var harness = new ReportPageHarness();
        harness.History.LoadException = new IOException("历史文件被占用");

        await harness.ViewModel.LoadAsync();

        harness.ViewModel.LoadErrorMessage.ShouldNotBeNull();
        harness.ViewModel.LoadErrorMessage!.ShouldContain("读取任务历史失败");
        harness.ViewModel.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task Preview_should_run_scan_and_enable_save_for_clean_data()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();

        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);

        harness.ViewModel.IsPreviewReady.ShouldBeTrue();
        harness.ViewModel.IsPreviewClean.ShouldBeTrue();
        harness.ViewModel.CanSave.ShouldBeTrue();
        harness.ViewModel.PreviewScanText.ShouldContain("通过");
        harness.ViewModel.PreviewHtml.ShouldNotBeNull();
        harness.ViewModel.PreviewHtml!.ShouldContain("<!DOCTYPE html>");

        // 预览不写文件。
        File.Exists(harness.Exporter.Destination!).ShouldBeFalse();
    }

    [Fact]
    public async Task Preview_should_disable_save_when_sensitive_data_found()
    {
        using var harness = new ReportPageHarness();
        var leaky = ReportPageHarness.SampleRun() with
        {
            Steps =
            [
                new DiagnosticStepResult(
                    "leak",
                    "请求",
                    DiagnosticStatus.Failed,
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromSeconds(1),
                    "Authorization: Bearer abcdef123456789 被拒绝。")
            ]
        };
        harness.History.Entries.Add(leaky);
        await harness.ViewModel.LoadAsync();

        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);

        harness.ViewModel.IsPreviewReady.ShouldBeTrue();
        harness.ViewModel.IsPreviewClean.ShouldBeFalse();
        harness.ViewModel.CanSave.ShouldBeFalse();
        harness.ViewModel.HasActionError.ShouldBeTrue();
        harness.ViewModel.PreviewScanText.ShouldContain("疑似敏感信息");
    }

    [Fact]
    public async Task Save_should_write_file_and_expose_path()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();
        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        harness.ViewModel.HasActionError.ShouldBeFalse();
        harness.ViewModel.LastGeneratedPath.ShouldNotBeNull();
        File.Exists(harness.ViewModel.LastGeneratedPath!).ShouldBeTrue();
        harness.ViewModel.HasLastGeneratedPath.ShouldBeTrue();

        var html = await File.ReadAllTextAsync(harness.ViewModel.LastGeneratedPath!);
        html.ShouldContain("网络基础诊断");
        html.ShouldContain("10.0.0.8:443");
    }

    [Fact]
    public async Task Cancelled_dialog_should_report_cancellation_without_error()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();
        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);
        harness.Exporter.Destination = null;

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        harness.ViewModel.ActionFeedback.ShouldContain("取消");
        harness.ViewModel.HasActionError.ShouldBeFalse();
        harness.ViewModel.LastGeneratedPath.ShouldBeNull();
    }

    [Fact]
    public async Task Switching_run_should_invalidate_previous_preview()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun("first:443"));
        harness.History.Entries.Add(ReportPageHarness.SampleRun("second:443") with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await harness.ViewModel.LoadAsync();
        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);
        harness.ViewModel.IsPreviewReady.ShouldBeTrue();

        harness.ViewModel.SelectedRun = harness.ViewModel.Runs[1];

        // 关键：旧预览必须失效，否则会把上一份任务的报告存成新任务的名字。
        harness.ViewModel.IsPreviewReady.ShouldBeFalse();
        harness.ViewModel.CanSave.ShouldBeFalse();
        harness.ViewModel.PreviewHtml.ShouldBeNull();
    }

    [Fact]
    public async Task Changing_project_name_should_invalidate_preview()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();
        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);
        harness.ViewModel.IsPreviewReady.ShouldBeTrue();

        harness.ViewModel.ProjectName = "新的项目名称";

        harness.ViewModel.IsPreviewReady.ShouldBeFalse();
        harness.ViewModel.PreviewScanText.ShouldContain("重新预览");
    }

    [Fact]
    public async Task Project_name_should_be_written_into_report()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();
        harness.ViewModel.ProjectName = "园区一期验收";

        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);
        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        var html = await File.ReadAllTextAsync(harness.ViewModel.LastGeneratedPath!);
        html.ShouldContain("园区一期验收");
    }

    [Fact]
    public async Task Write_failure_should_be_reported_as_error()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();
        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);

        // 目标目录不存在且父路径非法，触发真实写入失败。
        harness.Exporter.Destination = Path.Combine(harness.RootDirectory, "bad\u0000name.html");

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        harness.ViewModel.HasActionError.ShouldBeTrue();
        harness.ViewModel.ActionFeedback.ShouldContain("失败");
        harness.ViewModel.LastGeneratedPath.ShouldBeNull();
    }

    [Fact]
    public async Task Reveal_should_delegate_to_explorer_contract()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();
        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);
        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        harness.ViewModel.RevealReportCommand.Execute(null);

        harness.Exporter.RevealedPath.ShouldBe(harness.ViewModel.LastGeneratedPath);
    }

    [Fact]
    public async Task Reveal_failure_should_not_throw()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();
        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);
        await harness.ViewModel.SaveCommand.ExecuteAsync(null);
        harness.Exporter.RevealException = new System.ComponentModel.Win32Exception("拒绝访问");

        harness.ViewModel.RevealReportCommand.Execute(null);

        harness.ViewModel.HasActionError.ShouldBeTrue();
        harness.ViewModel.ActionFeedback.ShouldContain("无法打开文件位置");
    }

    [Fact]
    public async Task Save_should_be_blocked_without_preview()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();

        harness.ViewModel.CanSave.ShouldBeFalse();
        harness.Exporter.ChooseCount.ShouldBe(0);

        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        // 命令不可执行时不应弹出保存对话框。
        harness.Exporter.ChooseCount.ShouldBe(0);
    }

    [Fact]
    public async Task Report_should_preserve_run_state_semantics()
    {
        using var harness = new ReportPageHarness();
        harness.History.Entries.Add(ReportPageHarness.SampleRun());
        await harness.ViewModel.LoadAsync();
        await harness.ViewModel.PreviewCommand.ExecuteAsync(null);
        await harness.ViewModel.SaveCommand.ExecuteAsync(null);

        var html = await File.ReadAllTextAsync(harness.ViewModel.LastGeneratedPath!);

        // 1 通过 + 1 跳过 => 部分成功；跳过不得渲染成失败。
        html.ShouldContain("部分成功");
        html.ShouldContain("已跳过");
        html.ShouldContain("本阶段未实现媒体接收");
        html.ShouldContain("2 个步骤：1 通过 / 1 跳过");
    }
}
