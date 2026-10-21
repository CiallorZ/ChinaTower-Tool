using System.IO;
using LinkForge.Adapters.Mqtt;
using LinkForge.App.Services;
using LinkForge.App.ViewModels;

namespace LinkForge.App.Tests.ViewModels;

/// <summary>构造 MQTT 工作台的测试夹具。</summary>
internal sealed class MqttWorkbenchHarness : IDisposable
{
    public MqttWorkbenchHarness(TimeProvider? timeProvider = null)
    {
        Transport = new FakeMqttTransport();
        Exporter = new FakeMqttResultExporter();

        // 视图模型会捕获构造时的 SynchronizationContext 用于把传输事件投递到 UI 线程。
        // 单元测试没有可泵送的 WPF Dispatcher，若不替换，事件会排在 xUnit 的异步上下文里
        // 直到测试结束才执行，导致“消息明明发了却读不到”的假失败。
        // 这里使用同步上下文，仅改变投递时机，不改变生产代码的线程模型。
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());

        try
        {
            ViewModel = new MqttWorkbenchViewModel(Transport, Exporter, timeProvider);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    public FakeMqttTransport Transport { get; }

    public FakeMqttResultExporter Exporter { get; }

    public MqttWorkbenchViewModel ViewModel { get; }

    /// <summary>让传输层以“已连接”状态建立会话。</summary>
    public async Task ConnectAsync()
    {
        Transport.ConnectBehavior ??= (_, _) =>
        {
            Transport.IsConnected = true;
            return Task.CompletedTask;
        };

        await ViewModel.ConnectCommand.ExecuteAsync(null);
    }

    /// <summary>向视图模型投递一条真实路径的接收消息。</summary>
    public void Receive(
        string topic,
        string payload,
        int qualityOfService = 0,
        bool retain = false)
    {
        Transport.RaiseMessage(new MqttReceivedMessage(
            DateTimeOffset.Now,
            topic,
            payload,
            qualityOfService,
            retain,
            System.Text.Encoding.UTF8.GetByteCount(payload)));
    }

    public void RaiseDisconnected(string detail = "Broker 关闭了连接。") =>
        Transport.RaiseStatus(new MqttConnectionStatusChanged(false, "已断开", detail));

    public void Dispose() => Transport.Dispose();
}

/// <summary>立即执行的同步上下文：让 Post 在调用线程上完成。</summary>
internal sealed class ImmediateSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);

    public override void Send(SendOrPostCallback d, object? state) => d(state);
}

/// <summary>可编排的 MQTT 传输替身；事件与状态语义与生产实现保持一致。</summary>
internal sealed class FakeMqttTransport : IMqttTransport
{
    public bool IsConnected { get; set; }

    public event EventHandler<MqttReceivedMessage>? MessageReceived;

    public event EventHandler<MqttConnectionStatusChanged>? ConnectionStatusChanged;

    public Func<MqttConnectionOptions, CancellationToken, Task>? ConnectBehavior { get; set; }

    public Func<CancellationToken, Task>? DisconnectBehavior { get; set; }

    public Func<IReadOnlyList<MqttSubscription>, CancellationToken, Task<IReadOnlyList<MqttSubscribeResult>>>?
        SubscribeBehavior { get; set; }

    public Func<MqttPublishRequest, CancellationToken, Task>? PublishBehavior { get; set; }

    public MqttConnectionOptions? LastOptions { get; private set; }

    public MqttPublishRequest? LastPublish { get; private set; }

    public int ConnectCallCount { get; private set; }

    public int PublishCallCount { get; private set; }

    public IReadOnlyList<MqttSubscription>? LastSubscriptions { get; private set; }

    public Task ConnectAsync(MqttConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ConnectCallCount++;
        LastOptions = options;

        if (ConnectBehavior is { } behavior)
        {
            return behavior(options, cancellationToken);
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (DisconnectBehavior is { } behavior)
        {
            return behavior(cancellationToken);
        }

        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MqttSubscribeResult>> SubscribeAsync(
        IReadOnlyList<MqttSubscription> subscriptions,
        CancellationToken cancellationToken = default)
    {
        LastSubscriptions = subscriptions;

        return SubscribeBehavior is { } behavior
            ? behavior(subscriptions, cancellationToken)
            : Task.FromResult<IReadOnlyList<MqttSubscribeResult>>(
                [.. subscriptions.Select(static item => new MqttSubscribeResult(
                    item.TopicFilter,
                    item.QualityOfService,
                    "GrantedQoS"))]);
    }

    public Task PublishAsync(
        MqttPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        PublishCallCount++;
        LastPublish = request;

        return PublishBehavior?.Invoke(request, cancellationToken) ?? Task.CompletedTask;
    }

    public void RaiseMessage(MqttReceivedMessage message) =>
        MessageReceived?.Invoke(this, message);

    public void RaiseStatus(MqttConnectionStatusChanged status) =>
        ConnectionStatusChanged?.Invoke(this, status);

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>记录复制/导出内容，不触碰剪贴板与文件对话框。</summary>
internal sealed class FakeMqttResultExporter : IMqttResultExporter
{
    public string? CopiedText { get; private set; }

    public string? ExportedFileName { get; private set; }

    public string? ExportedContent { get; private set; }

    public string? ExportedFilter { get; private set; }

    public Func<string, string, CancellationToken, Task<string?>>? ExportBehavior { get; set; }

    public Exception? CopyException { get; set; }

    public void Copy(string text)
    {
        if (CopyException is { } exception)
        {
            throw exception;
        }

        CopiedText = text;
    }

    public Task<string?> ExportAsync(
        string suggestedFileName,
        string content,
        string filter,
        CancellationToken cancellationToken = default)
    {
        ExportedFileName = suggestedFileName;
        ExportedContent = content;
        ExportedFilter = filter;

        return ExportBehavior?.Invoke(suggestedFileName, content, cancellationToken)
            ?? Task.FromResult<string?>(
                Path.Combine(Path.GetTempPath(), suggestedFileName));
    }
}
