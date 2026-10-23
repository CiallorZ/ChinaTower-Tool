using LinkForge.App.Services;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>Contract tests for the real version, installation and signature page.</summary>
public sealed class AboutViewModelTests
{
    [Fact]
    public void Default_state_should_be_an_honest_empty_state()
    {
        var harness = new AboutHarness();

        harness.ViewModel.HasLoaded.ShouldBeFalse();
        harness.ViewModel.IsLoading.ShouldBeFalse();
        harness.ViewModel.Facts.ShouldBeEmpty();
        harness.ViewModel.Version.ShouldBe("未知");
        harness.ViewModel.Commit.ShouldBe("未知");
        harness.ViewModel.SignatureSummary.ShouldBe("尚未读取");
    }

    [Fact]
    public async Task Load_should_populate_real_environment_facts()
    {
        var harness = new AboutHarness();

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        harness.ViewModel.HasLoaded.ShouldBeTrue();
        harness.ViewModel.IsLoading.ShouldBeFalse();
        harness.ViewModel.ErrorMessage.ShouldBeNull();
        harness.ViewModel.ProductName.ShouldBe("LinkForge 视联网调试工具箱");
        harness.ViewModel.Version.ShouldBe("1.0.0");
        harness.ViewModel.Commit.ShouldBe("0123456789ab");
        harness.ViewModel.Facts.Count.ShouldBe(11);
        harness.ViewModel.Facts.ShouldContain(f => f.Label == "安装形态" && f.Value.Contains("Inno Setup"));
        harness.ViewModel.Facts.ShouldContain(f => f.Label == "进程架构" && f.Value == "X64");
        harness.ViewModel.RepositoryUrl.ShouldBe("https://github.com/CiallorZ/ChinaTower-Tool");
        harness.ViewModel.CopySummaryCommand.CanExecute(null).ShouldBeTrue();
    }

    [Theory]
    [InlineData(LfCodeSignatureState.Unsigned, true)]
    [InlineData(LfCodeSignatureState.Invalid, true)]
    [InlineData(LfCodeSignatureState.Unknown, true)]
    [InlineData(LfCodeSignatureState.Trusted, false)]
    public async Task Signature_warning_should_match_the_local_verification_result(
        LfCodeSignatureState state,
        bool expectedWarning)
    {
        var harness = new AboutHarness();
        harness.Info.Result = AboutHarness.Snapshot(state);

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        harness.ViewModel.HasSignatureWarning.ShouldBe(expectedWarning);
        harness.ViewModel.SignatureNotes.ShouldNotBeEmpty();
        harness.ViewModel.SignatureNotes.ShouldContain(
            note => note.Contains("不代表软件供应链已完成审计", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_failure_should_be_reported_without_claiming_loaded()
    {
        var harness = new AboutHarness();
        harness.Info.Behavior = _ =>
            Task.FromException<AboutSnapshot>(new InvalidOperationException("读取失败"));

        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        harness.ViewModel.HasLoaded.ShouldBeFalse();
        harness.ViewModel.IsLoading.ShouldBeFalse();
        harness.ViewModel.ErrorMessage.ShouldNotBeNull().ShouldContain("读取失败");
    }

    [Fact]
    public async Task Cancellation_should_not_be_reported_as_a_read_failure()
    {
        var harness = new AboutHarness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Info.Behavior = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return AboutHarness.Snapshot();
        };

        var load = harness.ViewModel.LoadCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.ViewModel.LoadCancelCommand.Execute(null);
        await load.WaitAsync(TimeSpan.FromSeconds(5));

        harness.ViewModel.IsLoading.ShouldBeFalse();
        harness.ViewModel.ErrorMessage.ShouldBeNull();
        harness.ViewModel.ActionFeedback.ShouldContain("取消");
    }

    [Fact]
    public async Task Copy_summary_should_contain_only_visible_environment_facts()
    {
        var harness = new AboutHarness();
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        harness.ViewModel.CopySummaryCommand.Execute(null);

        var copied = harness.Actions.CopiedText.ShouldNotBeNull();
        copied.ShouldContain("版本：1.0.0");
        copied.ShouldContain("程序路径");
        copied.ShouldContain("不包含凭据");
        copied.ShouldNotContain("password", Case.Insensitive);
        copied.ShouldNotContain("token", Case.Insensitive);
        copied.ShouldNotContain("secret", Case.Insensitive);
        harness.ViewModel.HasActionError.ShouldBeFalse();
    }

    [Fact]
    public async Task Directory_and_repository_actions_should_use_real_values()
    {
        var harness = new AboutHarness();
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        harness.ViewModel.OpenDataDirectoryCommand.Execute(null);
        harness.ViewModel.OpenLogsDirectoryCommand.Execute(null);
        harness.ViewModel.OpenRepositoryCommand.Execute(null);

        harness.Actions.OpenedDirectory.ShouldBe(@"C:\Users\tester\AppData\Local\LinkForge\logs");
        harness.Actions.OpenedUrl.ShouldBe(new Uri("https://github.com/CiallorZ/ChinaTower-Tool"));
        harness.ViewModel.HasActionError.ShouldBeFalse();
    }

    [Fact]
    public async Task Action_failures_should_be_readable_and_not_escape_the_page()
    {
        var harness = new AboutHarness();
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);
        harness.Actions.Failure = new InvalidOperationException("目录不可用");

        harness.ViewModel.OpenDataDirectoryCommand.Execute(null);

        harness.ViewModel.HasActionError.ShouldBeTrue();
        harness.ViewModel.ActionFeedback.ShouldContain("操作失败");
        harness.ViewModel.ActionFeedback.ShouldContain("目录不可用");
    }

    [Fact]
    public async Task Invalid_repository_url_should_not_launch_an_external_process()
    {
        var harness = new AboutHarness();
        harness.Info.Result = AboutHarness.Snapshot(repositoryUrl: "not a url");
        await harness.ViewModel.LoadCommand.ExecuteAsync(null);

        harness.ViewModel.OpenRepositoryCommand.Execute(null);

        harness.Actions.OpenedUrl.ShouldBeNull();
        harness.ViewModel.HasActionError.ShouldBeTrue();
        harness.ViewModel.ActionFeedback.ShouldContain("仓库地址无效");
    }
}
