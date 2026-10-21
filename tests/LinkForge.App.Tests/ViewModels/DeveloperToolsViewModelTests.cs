using System.Text;
using LinkForge.App.Presentation;
using LinkForge.App.ViewModels;
using LinkForge.DeveloperTools;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// LF-011 开发者工具视图模型测试。
/// </summary>
/// <remarks>
/// 验收重点：空状态为真、校验不触碰执行器、取消不是失败、
/// JWT 警告是"部分成功"而不是失败，以及剪贴板只复制真实输出。
/// </remarks>
public class DeveloperToolsViewModelTests
{
    [Fact]
    public void Default_state_should_be_a_true_empty_state()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.IsEmpty.ShouldBeTrue();
        viewModel.HasResult.ShouldBeFalse();
        viewModel.Output.ShouldBeEmpty();
        viewModel.SelectedTool.Kind.ShouldBe(DeveloperToolKind.JsonFormat);
        viewModel.State.ShouldBe(TaskRunState.Pending);
    }

    [Fact]
    public async Task Success_should_populate_real_output_and_summary()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.Input = """{"设备":"摄像机"}""";
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.HasResult.ShouldBeTrue();
        viewModel.IsRunning.ShouldBeFalse();
        viewModel.IsEmpty.ShouldBeFalse();
        viewModel.State.ShouldBe(TaskRunState.Completed);
        viewModel.IsWarning.ShouldBeFalse();
        viewModel.Output.ShouldContain("\"设备\"");
        viewModel.Conclusion.ShouldContain("JSON 合法");
    }

    [Fact]
    public async Task Required_tool_should_reject_missing_input_without_calling_the_executor()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.Input = string.Empty;
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.InputError.ShouldNotBeNullOrWhiteSpace();
        harness.Executor.CallCount.ShouldBe(0);
        viewModel.HasResult.ShouldBeFalse();
    }

    [Fact]
    public async Task Whitespace_input_must_still_reach_the_transformer()
    {
        // 空白是有意义的输入：对单个空格做 URL 编码得到 "%20"。
        // 若在页面层把空白一律当"未填写"，就会静默破坏编码语义。
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.SelectedTool = harness.Tool(DeveloperToolKind.UrlEncode);
        viewModel.Input = " ";
        await viewModel.RunCommand.ExecuteAsync(null);

        harness.Executor.CallCount.ShouldBe(1);
        viewModel.Output.ShouldBe("%20");
    }

    [Fact]
    public async Task Uuid_tool_should_accept_an_empty_input_and_generate_one()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.SelectedTool = harness.Tool(DeveloperToolKind.UuidGenerate);
        viewModel.Input = string.Empty;
        await viewModel.RunCommand.ExecuteAsync(null);

        harness.Executor.CallCount.ShouldBe(1);
        viewModel.State.ShouldBe(TaskRunState.Completed);
        Guid.TryParseExact(viewModel.Output, "D", out var generated).ShouldBeTrue();
        generated.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Oversized_input_should_be_rejected_before_reaching_the_executor()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.SelectedTool = harness.Tool(DeveloperToolKind.Base64Encode);
        viewModel.Input = new string('a', DeveloperTransforms.MaxInputBytes + 1);
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.InputError.ShouldNotBeNull().ShouldContain("1024 KiB");
        harness.Executor.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Jwt_result_should_be_a_warning_not_a_failure()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.SelectedTool = harness.Tool(DeveloperToolKind.JwtDecode);
        viewModel.Input = BuildJwt("""{"alg":"HS256","typ":"JWT"}""", """{"sub":"camera-01"}""");
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.PartiallySucceeded);
        viewModel.IsWarning.ShouldBeTrue();
        viewModel.HasResult.ShouldBeTrue();
        viewModel.Conclusion.ShouldContain("未验证签名");
        viewModel.Output.ShouldContain("未验证");
    }

    [Fact]
    public async Task Failed_conversion_should_be_failed_not_cancelled()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.SelectedTool = harness.Tool(DeveloperToolKind.JsonMinify);
        viewModel.Input = "{";
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Failed);
        viewModel.IsWarning.ShouldBeFalse();
        viewModel.Conclusion.ShouldContain("失败");
        viewModel.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_should_be_cancelled_not_failed()
    {
        var harness = new DeveloperToolsHarness();
        harness.Executor.Behavior = (_, _, token) =>
            Task.FromException<DeveloperToolResult>(new OperationCanceledException(token));
        var viewModel = harness.ViewModel;

        viewModel.Input = "{}";
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Cancelled);
        viewModel.Conclusion.ShouldContain("取消");
        viewModel.ConclusionNote.ShouldContain("取消不是失败");
        viewModel.IsWarning.ShouldBeFalse();
    }

    [Fact]
    public async Task Unexpected_executor_exception_should_not_escape_the_page()
    {
        var harness = new DeveloperToolsHarness();
        harness.Executor.Behavior = (_, _, _) =>
            Task.FromException<DeveloperToolResult>(new InvalidOperationException("boom"));
        var viewModel = harness.ViewModel;

        viewModel.Input = "{}";
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Failed);
        viewModel.CurrentStage.ShouldBe("执行失败");
        viewModel.HasResult.ShouldBeTrue();
    }

    [Fact]
    public async Task Run_command_should_be_unavailable_while_executing()
    {
        var harness = new DeveloperToolsHarness();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        harness.Executor.Behavior = async (_, _, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return DeveloperToolResult.Ok("done", "完成");
        };
        var viewModel = harness.ViewModel;

        viewModel.Input = "{}";
        var run = viewModel.RunCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.IsRunning.ShouldBeTrue();
        viewModel.CanExecute.ShouldBeFalse();
        viewModel.RunCommand.CanExecute(null).ShouldBeFalse();
        viewModel.CancelCommand.CanExecute(null).ShouldBeTrue();

        viewModel.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.State.ShouldBe(TaskRunState.Cancelled);
        viewModel.IsRunning.ShouldBeFalse();
        viewModel.CanExecute.ShouldBeTrue();
    }

    [Fact]
    public async Task Copy_should_put_the_real_output_on_the_clipboard_boundary()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.SelectedTool = harness.Tool(DeveloperToolKind.Base64Encode);
        viewModel.Input = "LinkForge";
        await viewModel.RunCommand.ExecuteAsync(null);
        viewModel.CopyResultCommand.Execute(null);

        harness.Clipboard.CopiedText.ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("LinkForge")));
        harness.Clipboard.CopiedText.ShouldBe(viewModel.Output);
        viewModel.ConclusionNote.ShouldContain("剪贴板");
    }

    [Fact]
    public async Task Clipboard_failure_should_become_readable_feedback()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.SelectedTool = harness.Tool(DeveloperToolKind.UuidGenerate);
        await RunCommandAsync(viewModel);
        harness.Clipboard.Failure = new InvalidOperationException("剪贴板被占用");
        viewModel.CopyResultCommand.Execute(null);

        viewModel.ConclusionNote.ShouldContain("复制失败");
    }

    [Fact]
    public async Task Clear_should_return_to_the_empty_state()
    {
        var harness = new DeveloperToolsHarness();
        var viewModel = harness.ViewModel;

        viewModel.Input = "{}";
        await viewModel.RunCommand.ExecuteAsync(null);
        viewModel.ClearCommand.Execute(null);

        viewModel.IsEmpty.ShouldBeTrue();
        viewModel.HasResult.ShouldBeFalse();
        viewModel.Input.ShouldBeEmpty();
        viewModel.Output.ShouldBeEmpty();
        viewModel.State.ShouldBe(TaskRunState.Pending);
        viewModel.Conclusion.ShouldBeEmpty();
    }

    [Fact]
    public void Every_transform_kind_should_have_exactly_one_ui_descriptor()
    {
        var kinds = DeveloperToolDescriptor.All.Select(static tool => tool.Kind).ToArray();
        var expected = Enum.GetValues<DeveloperToolKind>();

        kinds.Distinct().Count().ShouldBe(kinds.Length);
        kinds.Length.ShouldBe(expected.Length);

        foreach (var kind in expected)
        {
            kinds.ShouldContain(kind, $"{kind} 没有对应的界面入口。");
        }
    }

    [Fact]
    public void Every_descriptor_should_be_self_explanatory()
    {
        foreach (var tool in DeveloperToolDescriptor.All)
        {
            tool.Name.ShouldNotBeNullOrWhiteSpace($"{tool.Kind} 缺少名称。");
            tool.Description.ShouldNotBeNullOrWhiteSpace($"{tool.Kind} 缺少用途说明。");
            tool.InputLabel.ShouldNotBeNullOrWhiteSpace($"{tool.Kind} 缺少输入标签。");
            tool.Hint.ShouldNotBeNullOrWhiteSpace($"{tool.Kind} 缺少限制说明。");
            tool.ActionLabel.ShouldNotBeNullOrWhiteSpace($"{tool.Kind} 缺少动作文案。");
        }
    }

    [Fact]
    public void Jwt_entry_must_state_that_the_signature_is_not_verified()
    {
        var jwt = DeveloperToolDescriptor.All.Single(static tool =>
            tool.Kind == DeveloperToolKind.JwtDecode);

        jwt.Description.ShouldContain("不验证签名");
        jwt.Hint.ShouldContain("不写入日志");
    }

    private static Task RunCommandAsync(DeveloperToolsViewModel viewModel) =>
        viewModel.RunCommand.ExecuteAsync(null);

    private static string BuildJwt(string header, string payload) =>
        $"{Base64Url(header)}.{Base64Url(payload)}.ZGVtby1zaWduYXR1cmU";

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
