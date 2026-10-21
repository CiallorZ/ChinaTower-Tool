using LinkForge.Adapters.Http;
using LinkForge.App.Presentation;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>LF-007 API 工作台状态、校验与脱敏测试。</summary>
public class ApiWorkbenchViewModelTests
{
    [Fact]
    public void Empty_url_should_show_field_error_and_not_send()
    {
        using var harness = new ApiWorkbenchHarness();

        harness.ViewModel.SendCommand.Execute(null);

        harness.ViewModel.UrlError.ShouldNotBeNullOrWhiteSpace();
        harness.Transport.CallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("ftp://example.test/")]
    [InlineData("example.test/api")]
    [InlineData("http://user:pass@example.test/")]
    public void Invalid_or_credential_bearing_url_should_be_rejected(string url)
    {
        using var harness = new ApiWorkbenchHarness();
        harness.ViewModel.Url = url;

        harness.ViewModel.SendCommand.Execute(null);

        harness.ViewModel.UrlError.ShouldNotBeNullOrWhiteSpace();
        harness.Transport.CallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("301")]
    [InlineData("abc")]
    public void Invalid_timeout_should_be_rejected(string timeout)
    {
        using var harness = new ApiWorkbenchHarness();
        harness.ViewModel.Url = "http://127.0.0.1:9/";
        harness.ViewModel.TimeoutText = timeout;

        harness.ViewModel.SendCommand.Execute(null);

        harness.ViewModel.TimeoutError.ShouldNotBeNullOrWhiteSpace();
        harness.Transport.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Successful_response_should_populate_status_headers_body_and_history()
    {
        using var harness = new ApiWorkbenchHarness();
        var viewModel = harness.ViewModel;
        viewModel.Url = "http://127.0.0.1:8080/api";

        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Completed);
        viewModel.HasResponse.ShouldBeTrue();
        viewModel.ResponseStatusText.ShouldBe("200 OK");
        viewModel.ResponseBody.ShouldContain("\"ok\":true");
        viewModel.ResponseHeaders.ShouldContain(static h => h.Name == "Content-Type");
        viewModel.History.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Http_404_should_still_be_a_completed_response_not_transport_failure()
    {
        using var harness = new ApiWorkbenchHarness();
        harness.Transport.Behavior = (request, _) => Task.FromResult(new HttpApiResponse
        {
            Method = request.Method,
            FinalUri = new Uri(request.Url),
            StatusCode = 404,
            ReasonPhrase = "Not Found",
            ResponseHeaders = new Dictionary<string, string[]>(),
            Body = "not-found",
            Duration = TimeSpan.FromMilliseconds(30),
            IsTruncated = false,
            ResponseBytes = 9
        });

        var viewModel = harness.ViewModel;
        viewModel.Url = "http://127.0.0.1:8080/missing";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Completed);
        viewModel.ResponseStatusText.ShouldBe("404 Not Found");
        viewModel.ResponseBody.ShouldBe("not-found");
        viewModel.Conclusion.ShouldContain("不是传输失败");
    }

    [Fact]
    public async Task Timeout_should_be_failed_with_timeout_specific_message()
    {
        using var harness = new ApiWorkbenchHarness();
        harness.Transport.Behavior = (_, _) => throw new HttpApiTransportException(
            HttpApiFailureKind.Timeout,
            "请求超过 1 秒，已停止。");

        var viewModel = harness.ViewModel;
        viewModel.Url = "http://127.0.0.1:8080/slow";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Failed);
        viewModel.CurrentStage.ShouldContain("超时");
        viewModel.Conclusion.ShouldContain("超时");
    }

    [Fact]
    public async Task Cancellation_should_be_cancelled_not_failed()
    {
        using var harness = new ApiWorkbenchHarness();
        harness.Transport.Behavior = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("不应到达这里。");
        };

        var viewModel = harness.ViewModel;
        viewModel.Url = "http://127.0.0.1:8080/slow";

        var run = viewModel.SendCommand.ExecuteAsync(null);
        viewModel.CancelCommand.Execute(null);
        await run;

        viewModel.State.ShouldBe(TaskRunState.Cancelled);
        viewModel.Conclusion.ShouldContain("取消");
    }

    [Fact]
    public async Task Variables_should_expand_into_the_actual_request()
    {
        using var harness = new ApiWorkbenchHarness();
        var viewModel = harness.ViewModel;
        viewModel.Url = "{{baseUrl}}/devices";

        await viewModel.SendCommand.ExecuteAsync(null);

        harness.Transport.LastRequest.ShouldNotBeNull();
        harness.Transport.LastRequest!.Url.ShouldBe("https://api.example.com/devices");
    }

    [Fact]
    public void Undefined_variable_should_be_a_field_error()
    {
        using var harness = new ApiWorkbenchHarness();
        harness.ViewModel.Url = "{{missingBase}}/devices";

        harness.ViewModel.SendCommand.Execute(null);

        var error = harness.ViewModel.UrlError
            ?? throw new InvalidOperationException("未定义变量应产生 URL 字段错误。");
        error.ShouldContain("missingBase");
        harness.Transport.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Credentials_and_response_secrets_should_not_reach_copy_or_export()
    {
        using var harness = new ApiWorkbenchHarness();
        harness.Transport.Behavior = (request, _) => Task.FromResult(new HttpApiResponse
        {
            Method = request.Method,
            FinalUri = new Uri(request.Url),
            StatusCode = 401,
            ReasonPhrase = "Unauthorized",
            ResponseHeaders = new Dictionary<string, string[]>
            {
                ["Set-Cookie"] = ["session=SuperSecret123"]
            },
            Body = "{\"token\":\"SuperSecret123\"}",
            Duration = TimeSpan.FromMilliseconds(10),
            IsTruncated = false,
            ResponseBytes = 28
        });

        var viewModel = harness.ViewModel;
        viewModel.Url = "http://127.0.0.1:8080/api";
        viewModel.SelectedAuthMode = viewModel.AuthModes.Single(static a => a.Key == "Bearer");
        viewModel.BearerToken = "SuperSecret123";

        await viewModel.SendCommand.ExecuteAsync(null);
        viewModel.CopySummaryCommand.Execute(null);
        await viewModel.ExportCommand.ExecuteAsync(null);

        harness.Exporter.CopiedText.ShouldNotBeNull();
        harness.Exporter.CopiedText.ShouldNotContain("SuperSecret123");
        var exported = harness.Exporter.ExportedContent
            ?? throw new InvalidOperationException("导出内容为空。");
        exported.ShouldNotContain("SuperSecret123");
        exported.ShouldContain("\"value\": \"***\"");
        harness.HistoryStore.Entries.ShouldNotBeEmpty();
        harness.HistoryStore.Entries[0].DisplayUrl.ShouldNotContain("SuperSecret123");
    }

    [Fact]
    public async Task Truncated_response_should_expose_a_clear_notice()
    {
        using var harness = new ApiWorkbenchHarness();
        harness.Transport.Behavior = (request, _) => Task.FromResult(new HttpApiResponse
        {
            Method = request.Method,
            FinalUri = new Uri(request.Url),
            StatusCode = 200,
            ReasonPhrase = "OK",
            ResponseHeaders = new Dictionary<string, string[]>(),
            Body = new string('x', 32),
            Duration = TimeSpan.FromMilliseconds(12),
            IsTruncated = true,
            ResponseBytes = 16 * 1024
        });

        var viewModel = harness.ViewModel;
        viewModel.Url = "http://127.0.0.1:8080/large";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.IsResponseTruncated.ShouldBeTrue();
        viewModel.TruncationNotice.ShouldContain("截断");
    }
}
