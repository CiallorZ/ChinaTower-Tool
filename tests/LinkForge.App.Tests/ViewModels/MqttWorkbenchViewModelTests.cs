using System.IO;
using LinkForge.Adapters.Mqtt;
using LinkForge.App.ViewModels;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>
/// LF-008 MQTT 工作台视图模型测试。
/// </summary>
/// <remarks>
/// 验收重点：字段校验、认证失败与取消的区分、断线后状态不得残留为已连接、
/// 暂停不丢消息、以及复制/导出内容确实经过脱敏。
/// </remarks>
public class MqttWorkbenchViewModelTests
{
    [Fact]
    public void Empty_host_should_show_a_field_error_and_not_connect()
    {
        using var harness = new MqttWorkbenchHarness();
        var viewModel = harness.ViewModel;

        viewModel.Host = "  ";
        viewModel.ConnectCommand.Execute(null);

        viewModel.HostError.ShouldNotBeNullOrWhiteSpace();
        harness.Transport.ConnectCallCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("mqtt")]
    public void Invalid_port_should_be_rejected(string port)
    {
        using var harness = new MqttWorkbenchHarness();
        var viewModel = harness.ViewModel;

        viewModel.PortText = port;
        viewModel.ConnectCommand.Execute(null);

        viewModel.PortError.ShouldNotBeNullOrWhiteSpace();
        harness.Transport.ConnectCallCount.ShouldBe(0);
    }

    [Fact]
    public void Empty_client_id_should_be_rejected()
    {
        using var harness = new MqttWorkbenchHarness();
        var viewModel = harness.ViewModel;

        viewModel.ClientId = " ";
        viewModel.ConnectCommand.Execute(null);

        viewModel.ClientIdError.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("301")]
    [InlineData("ten")]
    public void Invalid_timeout_should_be_rejected(string timeout)
    {
        using var harness = new MqttWorkbenchHarness();
        var viewModel = harness.ViewModel;

        viewModel.ConnectTimeoutText = timeout;
        viewModel.ConnectCommand.Execute(null);

        viewModel.ConnectTimeoutError.ShouldNotBeNullOrWhiteSpace();
        harness.Transport.ConnectCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Successful_connect_should_report_connected_and_populate_summary()
    {
        using var harness = new MqttWorkbenchHarness();
        var viewModel = harness.ViewModel;

        await harness.ConnectAsync();

        viewModel.IsConnected.ShouldBeTrue();
        viewModel.ConnectionText.ShouldBe("已连接");
        viewModel.State.ShouldBe(TaskRunState.Completed);
        viewModel.SummaryItems.ShouldContain(static item => item.Label == "Broker");
        viewModel.Logs.ShouldNotBeEmpty();
        harness.Transport.LastOptions!.Host.ShouldBe("127.0.0.1");
    }

    [Fact]
    public async Task Authentication_failure_should_be_failed_and_stay_disconnected()
    {
        using var harness = new MqttWorkbenchHarness();
        harness.Transport.ConnectBehavior = (_, _) =>
            Task.FromException(new MqttTransportException(
                MqttFailureKind.AuthenticationFailed,
                "Broker 拒绝连接：BadUserNameOrPassword。请检查用户名、密码或访问策略。"));

        var viewModel = harness.ViewModel;
        viewModel.Username = "device";
        viewModel.Password = "wrong-secret";

        await viewModel.ConnectCommand.ExecuteAsync(null);

        viewModel.State.ShouldBe(TaskRunState.Failed);
        viewModel.IsConnected.ShouldBeFalse();
        viewModel.ConnectionText.ShouldBe("连接失败");
        viewModel.HasError.ShouldBeTrue();
        viewModel.ErrorMessage.ShouldContain("拒绝");
        viewModel.ErrorHint.ShouldContain("口令");
        viewModel.Logs.ShouldAllBe(static log => !log.Message.Contains("wrong-secret"));
    }

    [Fact]
    public async Task Cancelling_connect_should_be_cancelled_not_failed()
    {
        using var harness = new MqttWorkbenchHarness();
        harness.Transport.ConnectBehavior = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
        };

        var viewModel = harness.ViewModel;
        var run = viewModel.ConnectCommand.ExecuteAsync(null);

        viewModel.CanCancel.ShouldBeTrue();
        viewModel.CancelCommand.Execute(null);
        await run;

        viewModel.State.ShouldBe(TaskRunState.Cancelled);
        viewModel.IsConnected.ShouldBeFalse();
        viewModel.ConnectionText.ShouldContain("取消");
    }

    [Fact]
    public async Task Broker_disconnect_should_immediately_clear_connected_state()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;
        harness.RaiseDisconnected();

        viewModel.IsConnected.ShouldBeFalse();
        viewModel.ConnectionText.ShouldBe("已断开");
        viewModel.State.ShouldBe(TaskRunState.Inconclusive);
        viewModel.ConnectionText.ShouldNotContain("已连接");
    }

    [Fact]
    public async Task Voluntary_disconnect_should_not_be_reported_as_interrupted()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;

        harness.Transport.DisconnectBehavior = _ =>
        {
            // 与生产实现一致：DisconnectAsync 返回前会触发 Disconnected 事件。
            harness.Transport.IsConnected = false;
            harness.RaiseDisconnected();
            return Task.CompletedTask;
        };

        await viewModel.DisconnectCommand.ExecuteAsync(null);

        viewModel.IsConnected.ShouldBeFalse();
        viewModel.State.ShouldBe(TaskRunState.Completed);
        viewModel.CurrentStage.ShouldBe("已断开");
    }

    [Fact]
    public async Task Pause_should_keep_collecting_messages_and_flush_on_resume()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;
        viewModel.TogglePauseCommand.Execute(null);

        viewModel.IsPaused.ShouldBeTrue();

        harness.Receive("device/a", "one");
        harness.Receive("device/b", "two");

        viewModel.Messages.ShouldBeEmpty("暂停时列表不刷新，但消息必须仍在缓冲。");
        viewModel.PendingMessageCount.ShouldBe(2);
        viewModel.PauseNotice.ShouldContain("2");
        viewModel.MessageCountText.ShouldContain("2");

        viewModel.TogglePauseCommand.Execute(null);

        viewModel.IsPaused.ShouldBeFalse();
        viewModel.Messages.Count.ShouldBe(2);
        viewModel.PendingMessageCount.ShouldBe(0);
        viewModel.Messages.ShouldContain(static message => message.DisplayPayload == "two");
    }

    [Fact]
    public async Task Filter_should_apply_to_visible_and_exported_messages()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;
        harness.Receive("camera/a/status", "{\"online\":true}");
        harness.Receive("sensor/b", "{\"temp\":21}");

        viewModel.Messages.Count.ShouldBe(2);

        viewModel.FilterText = "camera";
        viewModel.Messages.ShouldHaveSingleItem();

        viewModel.CopyMessagesCommand.Execute(null);

        harness.Exporter.CopiedText.ShouldNotBeNull();
        harness.Exporter.CopiedText.ShouldContain("camera/a/status");
        harness.Exporter.CopiedText.ShouldNotContain("sensor/b");
    }

    [Fact]
    public async Task Copy_should_redact_credentials_in_topics_and_payloads()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;
        harness.Receive(
            "device/token=abc123",
            "{\"password\":\"SuperSecret123\",\"value\":1}");

        viewModel.CopyMessagesCommand.Execute(null);

        harness.Exporter.CopiedText.ShouldNotBeNull();
        harness.Exporter.CopiedText.ShouldNotContain("SuperSecret123");
        harness.Exporter.CopiedText.ShouldNotContain("abc123");
        harness.Exporter.CopiedText.ShouldContain("password");
    }

    [Fact]
    public async Task Export_json_should_include_messages_and_not_contain_password()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;
        viewModel.Password = "BrokerPassword987";
        harness.Receive("device/a", "{\"token\":\"abcdef123456\"}");

        await viewModel.ExportJsonCommand.ExecuteAsync(null);

        harness.Exporter.ExportedContent.ShouldNotBeNull();
        harness.Exporter.ExportedContent.ShouldContain("mqtt-message-stream");
        harness.Exporter.ExportedContent.ShouldNotContain("BrokerPassword987");
        harness.Exporter.ExportedContent.ShouldNotContain("abcdef123456");
        harness.Exporter.ExportedFileName!.ShouldEndWith(".json");
    }

    [Fact]
    public async Task Export_csv_should_be_quoted_and_include_direction()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;
        harness.Receive("device/a", "hello, \"world\"");

        await viewModel.ExportCsvCommand.ExecuteAsync(null);

        harness.Exporter.ExportedContent.ShouldNotBeNull();
        harness.Exporter.ExportedContent.ShouldStartWith("time,direction,topic");
        harness.Exporter.ExportedContent.ShouldContain("\"hello, \"\"world\"\"\"");
        harness.Exporter.ExportedFileName!.ShouldEndWith(".csv");
        harness.Exporter.ExportedFilter!.ShouldContain("CSV");
    }

    [Fact]
    public async Task Export_without_messages_should_explain_and_not_open_a_dialog()
    {
        using var harness = new MqttWorkbenchHarness();
        var viewModel = harness.ViewModel;

        await viewModel.ExportJsonCommand.ExecuteAsync(null);

        harness.Exporter.ExportedContent.ShouldBeNull();
        viewModel.ActionFeedback.ShouldContain("没有可导出");
    }

    [Fact]
    public async Task Export_cancellation_should_be_reported_without_failure()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();
        harness.Receive("device/a", "payload");
        harness.Exporter.ExportBehavior = (_, _, _) => Task.FromResult<string?>(null);

        var viewModel = harness.ViewModel;
        await viewModel.ExportJsonCommand.ExecuteAsync(null);

        viewModel.ActionFeedback.ShouldContain("取消");
    }

    [Fact]
    public async Task Export_failure_should_be_reported_without_crashing()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();
        harness.Receive("device/a", "payload");
        harness.Exporter.ExportBehavior = (_, _, _) =>
            Task.FromException<string?>(new IOException("磁盘只读。"));

        var viewModel = harness.ViewModel;
        await viewModel.ExportJsonCommand.ExecuteAsync(null);

        viewModel.ActionFeedback.ShouldContain("导出失败");
        viewModel.ActionFeedback.ShouldContain("磁盘只读");
    }

    [Fact]
    public async Task Publish_should_record_a_published_message_and_keep_payload_redacted()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;
        viewModel.PublishTopic = "linkforge/test";
        viewModel.PublishPayload = "{\"api_key\":\"sk-abcdefghijklmnopqrstuvwxyz\"}";

        await viewModel.PublishCommand.ExecuteAsync(null);

        harness.Transport.PublishCallCount.ShouldBe(1);
        harness.Transport.LastPublish!.Topic.ShouldBe("linkforge/test");
        viewModel.Messages.ShouldHaveSingleItem();
        viewModel.Messages[0].IsPublished.ShouldBeTrue();
        viewModel.Messages[0].DisplayPayload.ShouldNotContain("sk-abcdefghijklmnopqrstuvwxyz");
        viewModel.ActionFeedback.ShouldContain("已发布");
    }

    [Fact]
    public async Task Publish_with_wildcard_topic_should_be_rejected_before_send()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;
        viewModel.PublishTopic = "device/+/status";

        await viewModel.PublishCommand.ExecuteAsync(null);

        viewModel.PublishError.ShouldNotBeNullOrWhiteSpace();
        harness.Transport.PublishCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_publish_should_be_cancelled_not_failed()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();
        harness.Transport.PublishBehavior = async (_, token) =>
            await Task.Delay(Timeout.Infinite, token);

        var viewModel = harness.ViewModel;
        var run = viewModel.PublishCommand.ExecuteAsync(null);

        viewModel.CanCancel.ShouldBeTrue();
        viewModel.CancelCommand.Execute(null);
        await run;

        viewModel.State.ShouldBe(TaskRunState.Cancelled);
        viewModel.PublishError!.ShouldContain("取消");
    }

    [Fact]
    public async Task Cancelling_subscribe_should_be_cancelled_not_failed()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();
        harness.Transport.SubscribeBehavior = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return [];
        };

        var viewModel = harness.ViewModel;
        var run = viewModel.SubscribeCommand.ExecuteAsync(null);

        viewModel.CancelCommand.Execute(null);
        await run;

        viewModel.State.ShouldBe(TaskRunState.Cancelled);
        viewModel.SubscribeError!.ShouldContain("取消");
    }

    [Fact]
    public async Task Subscribe_should_expose_granted_qos_and_redacted_topic()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();
        harness.Transport.SubscribeBehavior = (items, _) => Task.FromResult<IReadOnlyList<MqttSubscribeResult>>(
        [
            new(items[0].TopicFilter, 1, "GrantedQoS1")
        ]);

        var viewModel = harness.ViewModel;
        viewModel.SubscribeTopic = "device/+/status";
        viewModel.SubscribeQosText = "1";

        await viewModel.SubscribeCommand.ExecuteAsync(null);

        viewModel.Subscriptions.ShouldHaveSingleItem().GrantedQualityOfService.ShouldBe(1);
        viewModel.HasSubscriptions.ShouldBeTrue();
        viewModel.SubscribeError.ShouldBeNull();
    }

    [Fact]
    public async Task Missing_client_library_should_be_reported_as_dependency_missing()
    {
        using var harness = new MqttWorkbenchHarness();
        harness.Transport.ConnectBehavior = (_, _) =>
            Task.FromException(new DllNotFoundException("无法加载 MQTTnet.dll。"));

        var viewModel = harness.ViewModel;
        await viewModel.ConnectCommand.ExecuteAsync(null);

        viewModel.IsDependencyMissing.ShouldBeTrue();
        viewModel.MissingDependencyName.ShouldContain("MQTT");
        viewModel.ConnectionText.ShouldBe("依赖缺失");
        viewModel.State.ShouldBe(TaskRunState.Failed);
    }

    [Fact]
    public async Task Oversized_payload_should_be_truncated_with_a_visible_notice()
    {
        using var harness = new MqttWorkbenchHarness();
        await harness.ConnectAsync();

        var viewModel = harness.ViewModel;
        harness.Receive("device/big", new string('x', 9000));

        var message = viewModel.Messages.ShouldHaveSingleItem();
        message.IsPayloadTruncated.ShouldBeTrue();
        message.DisplayPayload.Length.ShouldBe(8192);
        message.PayloadNotice.ShouldContain("截断");

        viewModel.CopyMessagesCommand.Execute(null);
        harness.Exporter.CopiedText!.ShouldContain("截断");
    }

    [Fact]
    public void Operations_should_require_a_connection()
    {
        using var harness = new MqttWorkbenchHarness();
        var viewModel = harness.ViewModel;

        viewModel.CanConnect.ShouldBeTrue();
        viewModel.CanDisconnect.ShouldBeFalse();
        viewModel.CanSubscribe.ShouldBeFalse();
        viewModel.CanPublish.ShouldBeFalse();
        viewModel.CanCancel.ShouldBeFalse();
    }
}
