using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinkForge.Adapters.Mqtt;
using LinkForge.App.Controls;
using LinkForge.App.Presentation;
using LinkForge.App.Services;
using LinkForge.Domain.Diagnostics;

namespace LinkForge.App.ViewModels;

/// <summary>一条订阅（用于界面展示，Topic 已脱敏）。</summary>
/// <param name="TopicFilter">Topic 过滤器（已脱敏）。</param>
/// <param name="GrantedQualityOfService">Broker 授权的 QoS；-1 表示被拒绝。</param>
public sealed record MqttSubscriptionItem(string TopicFilter, int GrantedQualityOfService)
{
    /// <summary>显示文本。</summary>
    public string DisplayText =>
        $"{TopicFilter} · QoS {GrantedQualityOfService}";
}

/// <summary>MQTT 基础调试器视图模型（LF-008）。</summary>
/// <remarks>
/// <para>
/// 状态来自真实的 MQTT 传输：连接、订阅、发布、断线与消息流均通过
/// <see cref="IMqttTransport"/> 事件与返回值表达，不使用任何模拟数据。
/// </para>
/// <para>
/// “暂停显示”只停止刷新可见列表，消息仍会进入内部缓冲（上限 1000 条），
/// 恢复显示时一次性补齐；暂停时丢弃消息会让用户误以为设备没有上报。
/// </para>
/// <para>
/// 凭据不写入日志、订阅列表、复制文本或导出文件；Topic 与载荷在进入
/// UI 之前就已脱敏（见 <see cref="MqttMessageEntry"/>）。
/// </para>
/// </remarks>
public sealed partial class MqttWorkbenchViewModel : ObservableObject
{
    private const int BufferLimit = 1000;

    private readonly IMqttTransport _transport;
    private readonly IMqttResultExporter _exporter;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _uiContext;
    private readonly List<MqttMessageEntry> _buffer = [];

    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _subscribeCts;
    private CancellationTokenSource? _publishCts;
    private bool _paused;
    private bool _disconnectRequested;

    /// <summary>创建 MQTT 工作台。</summary>
    public MqttWorkbenchViewModel(
        IMqttTransport transport,
        IMqttResultExporter exporter,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(exporter);

        _transport = transport;
        _exporter = exporter;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _uiContext = SynchronizationContext.Current;

        _transport.MessageReceived += OnMessageReceived;
        _transport.ConnectionStatusChanged += OnConnectionStatusChanged;

        Messages = [];
        Logs = [];
        Subscriptions = [];
        SummaryItems = [];

        State = TaskRunState.Pending;
        ConnectionState = MqttUiConnectionState.Disconnected;
        ConnectionText = "未连接";
        CurrentStage = "尚未连接";
    }

    /// <summary>当前显示的脱敏消息（受筛选与暂停影响）。</summary>
    public ObservableCollection<MqttMessageEntry> Messages { get; }

    /// <summary>连接和操作日志。</summary>
    public ObservableCollection<LogEntry> Logs { get; }

    /// <summary>订阅结果。</summary>
    public ObservableCollection<MqttSubscriptionItem> Subscriptions { get; }

    /// <summary>连接摘要。</summary>
    public ObservableCollection<ResultSummaryItem> SummaryItems { get; }

    /// <summary>Broker 主机。</summary>
    [ObservableProperty]
    public partial string Host { get; set; } = "127.0.0.1";

    /// <summary>Broker 端口。</summary>
    [ObservableProperty]
    public partial string PortText { get; set; } = "1883";

    /// <summary>Client ID。</summary>
    [ObservableProperty]
    public partial string ClientId { get; set; } = "linkforge-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>用户名。</summary>
    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    /// <summary>密码；不写入日志/历史/导出。</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>是否使用 TLS。</summary>
    [ObservableProperty]
    public partial bool UseTls { get; set; }

    /// <summary>连接超时秒数文本。</summary>
    [ObservableProperty]
    public partial string ConnectTimeoutText { get; set; } = "10";

    /// <summary>订阅 Topic。</summary>
    [ObservableProperty]
    public partial string SubscribeTopic { get; set; } = "#";

    /// <summary>订阅 QoS 文本。</summary>
    [ObservableProperty]
    public partial string SubscribeQosText { get; set; } = "0";

    /// <summary>发布 Topic。</summary>
    [ObservableProperty]
    public partial string PublishTopic { get; set; } = "linkforge/demo";

    /// <summary>发布载荷。</summary>
    [ObservableProperty]
    public partial string PublishPayload { get; set; } = "{\"online\":true}";

    /// <summary>发布 QoS 文本。</summary>
    [ObservableProperty]
    public partial string PublishQosText { get; set; } = "0";

    /// <summary>是否 Retain。</summary>
    [ObservableProperty]
    public partial bool Retain { get; set; }

    /// <summary>过滤文本；匹配 Topic 或载荷。</summary>
    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>主机错误。</summary>
    [ObservableProperty]
    public partial string? HostError { get; set; }

    /// <summary>端口错误。</summary>
    [ObservableProperty]
    public partial string? PortError { get; set; }

    /// <summary>客户端 ID 错误。</summary>
    [ObservableProperty]
    public partial string? ClientIdError { get; set; }

    /// <summary>连接超时错误。</summary>
    [ObservableProperty]
    public partial string? ConnectTimeoutError { get; set; }

    /// <summary>订阅字段错误。</summary>
    [ObservableProperty]
    public partial string? SubscribeError { get; set; }

    /// <summary>发布字段错误。</summary>
    [ObservableProperty]
    public partial string? PublishError { get; set; }

    /// <summary>当前连接状态。</summary>
    [ObservableProperty]
    public partial MqttUiConnectionState ConnectionState { get; private set; }

    /// <summary>连接状态文字。</summary>
    [ObservableProperty]
    public partial string ConnectionText { get; private set; } = string.Empty;

    /// <summary>任务状态（用于状态组件）。</summary>
    [ObservableProperty]
    public partial TaskRunState State { get; private set; }

    /// <summary>当前阶段。</summary>
    [ObservableProperty]
    public partial string CurrentStage { get; private set; } = string.Empty;

    /// <summary>是否正在连接/操作。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>是否显示错误。</summary>
    [ObservableProperty]
    public partial bool HasError { get; private set; }

    /// <summary>可读错误信息。</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; private set; } = string.Empty;

    /// <summary>错误分类（用于提示修正方向）。</summary>
    [ObservableProperty]
    public partial string ErrorHint { get; private set; } = string.Empty;

    /// <summary>复制/导出反馈。</summary>
    [ObservableProperty]
    public partial string ActionFeedback { get; private set; } = string.Empty;

    /// <summary>已运行时间（连接阶段）。</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = "00:00.0";

    /// <summary>是否已建立过会话（用于空状态判断）。</summary>
    [ObservableProperty]
    public partial bool HasSession { get; private set; }

    /// <summary>是否存在依赖缺失（MQTT 客户端库无法加载）。</summary>
    [ObservableProperty]
    public partial bool IsDependencyMissing { get; private set; }

    /// <summary>缺失依赖名称。</summary>
    [ObservableProperty]
    public partial string MissingDependencyName { get; private set; } = string.Empty;

    /// <summary>依赖缺失的影响说明。</summary>
    [ObservableProperty]
    public partial string MissingDependencyImpact { get; private set; } = string.Empty;

    /// <summary>依赖缺失的配置指引。</summary>
    [ObservableProperty]
    public partial string MissingDependencyGuidance { get; private set; } = string.Empty;

    /// <summary>暂停期间累计但尚未显示的消息数量。</summary>
    [ObservableProperty]
    public partial int PendingMessageCount { get; private set; }

    /// <summary>暂停提示。</summary>
    public string PauseNotice => PendingMessageCount > 0
        ? $"显示已暂停，期间收到 {PendingMessageCount} 条消息；恢复后会补齐。"
        : "显示已暂停；消息仍在后台记录。";

    /// <summary>是否存在暂停期间的消息。</summary>
    public bool HasPendingMessages => PendingMessageCount > 0;

    /// <summary>当前是否有可见消息。</summary>
    public bool HasMessages => Messages.Count > 0;

    /// <summary>是否存在订阅。</summary>
    public bool HasSubscriptions => Subscriptions.Count > 0;

    /// <summary>消息计数文本（包含暂停期间的消息）。</summary>
    public string MessageCountText => $"{_buffer.Count:N0} 条消息";

    /// <summary>订阅计数文本。</summary>
    public string SubscriptionCountText =>
        Subscriptions.Count == 0 ? "尚未订阅" : $"{Subscriptions.Count:N0} 个 Topic";

    /// <summary>是否已连接。</summary>
    public bool IsConnected => ConnectionState == MqttUiConnectionState.Connected;

    /// <summary>能否发起连接。</summary>
    public bool CanConnect => !IsBusy && !IsConnected;

    /// <summary>能否断开。</summary>
    public bool CanDisconnect => !IsBusy && IsConnected;

    /// <summary>能否订阅。</summary>
    public bool CanSubscribe => !IsBusy && IsConnected;

    /// <summary>能否发布。</summary>
    public bool CanPublish => !IsBusy && IsConnected;

    /// <summary>能否取消当前操作。</summary>
    public bool CanCancel =>
        _operationCts is not null || _subscribeCts is not null || _publishCts is not null;

    /// <summary>是否暂停显示。</summary>
    public bool IsPaused
    {
        get => _paused;
        private set
        {
            if (SetProperty(ref _paused, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));

                if (!value)
                {
                    RebuildVisibleMessages();
                }
            }
        }
    }

    /// <summary>暂停按钮文字。</summary>
    public string PauseButtonText => IsPaused ? "继续显示" : "暂停显示";

    /// <summary>连接。</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!ValidateConnection())
        {
            return;
        }

        HasError = false;
        ErrorMessage = string.Empty;
        ErrorHint = string.Empty;
        IsBusy = true;
        _disconnectRequested = false;
        ConnectionState = MqttUiConnectionState.Connecting;
        ConnectionText = "正在连接…";
        State = TaskRunState.Preparing;
        CurrentStage = "正在连接 Broker…";
        ActionFeedback = string.Empty;
        HasSession = true;
        ElapsedText = "00:00.0";
        SummaryItems.Clear();
        NotifyCommands();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operationCts = linked;
        var startedAt = _timeProvider.GetUtcNow();
        var ticker = TickElapsedAsync(startedAt, linked.Token);

        try
        {
            AppendLog(LfLogLevel.Information, $"连接 {Host}:{PortText}（TLS：{(UseTls ? "启用" : "关闭")}）");
            State = TaskRunState.Running;

            await _transport.ConnectAsync(
                new MqttConnectionOptions(
                    Host.Trim(),
                    ParsePort(PortText),
                    ClientId.Trim(),
                    UseTls,
                    Username,
                    Password,
                    TimeSpan.FromSeconds(ParseTimeout(ConnectTimeoutText))),
                linked.Token);

            ConnectionState = MqttUiConnectionState.Connected;
            ConnectionText = "已连接";
            State = TaskRunState.Completed;
            CurrentStage = "连接成功";
            ElapsedText = DashboardViewModel.FormatElapsed(_timeProvider.GetUtcNow() - startedAt);
            AppendLog(LfLogLevel.Information, "Broker 连接成功。");
            ApplyConnectionSummary();
        }
        catch (OperationCanceledException)
        {
            ConnectionState = MqttUiConnectionState.Disconnected;
            ConnectionText = "连接已取消";
            State = TaskRunState.Cancelled;
            CurrentStage = "已取消";
            AppendLog(LfLogLevel.Warning, "连接已取消。");
        }
        catch (MqttTransportException ex)
        {
            ConnectionState = MqttUiConnectionState.Disconnected;
            ConnectionText = "连接失败";
            State = TaskRunState.Failed;
            CurrentStage = "连接失败";
            HasError = true;
            ErrorMessage = SecretRedactor.Redact(ex.Message) ?? "MQTT 连接失败。";
            ErrorHint = HintFor(ex.Kind);
            AppendLog(LfLogLevel.Critical, ex.Message);
            SummaryItems.Clear();
            SummaryItems.Add(new ResultSummaryItem
            {
                Label = "结论",
                Value = ErrorMessage,
                Status = DiagnosticStatus.Failed
            });
        }
        catch (Exception ex)
        {
            ConnectionState = MqttUiConnectionState.Disconnected;
            ConnectionText = "连接失败";
            State = TaskRunState.Failed;
            CurrentStage = "连接失败";
            ApplyUnexpectedFailure(ex);
            AppendLog(LfLogLevel.Critical, ErrorMessage);
        }
        finally
        {
            linked.Cancel();
            await ticker;
            IsBusy = false;
            _operationCts = null;
            NotifyCommands();
        }
    }

    /// <summary>断开。</summary>
    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        _disconnectRequested = true;
        ConnectionState = MqttUiConnectionState.Disconnecting;
        ConnectionText = "正在断开…";
        CurrentStage = "正在断开…";
        NotifyCommands();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operationCts = linked;

        try
        {
            await _transport.DisconnectAsync(linked.Token);
            ConnectionState = MqttUiConnectionState.Disconnected;
            ConnectionText = "未连接";
            State = TaskRunState.Completed;
            CurrentStage = "已断开";
            AppendLog(LfLogLevel.Information, "已主动断开 Broker。");
            ApplyConnectionSummary();
        }
        catch (OperationCanceledException)
        {
            ConnectionState = MqttUiConnectionState.Disconnected;
            ConnectionText = "未连接";
            State = TaskRunState.Cancelled;
            CurrentStage = "断开已取消";
        }
        catch (Exception ex)
        {
            ConnectionState = MqttUiConnectionState.Disconnected;
            ConnectionText = "未连接";
            State = TaskRunState.Failed;
            HasError = true;
            ErrorMessage = SecretRedactor.Redact(ex.Message) ?? "断开失败。";
            ErrorHint = "连接可能已由对端关闭；重新连接前请复核 Broker 状态。";
        }
        finally
        {
            IsBusy = false;
            _operationCts = null;
            NotifyCommands();
        }
    }

    /// <summary>订阅。</summary>
    [RelayCommand(CanExecute = nameof(CanSubscribe))]
    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        SubscribeError = ValidateSubscription();

        if (SubscribeError is not null)
        {
            return;
        }

        IsBusy = true;
        CurrentStage = $"正在订阅 {SecretRedactor.Redact(SubscribeTopic.Trim())}…";
        State = TaskRunState.Running;
        NotifyCommands();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _subscribeCts = linked;
        NotifyCommands();

        try
        {
            var results = await _transport.SubscribeAsync(
                [new MqttSubscription(SubscribeTopic.Trim(), ParseQos(SubscribeQosText))],
                linked.Token);

            foreach (var result in results)
            {
                Subscriptions.Add(new MqttSubscriptionItem(
                    SecretRedactor.Redact(result.TopicFilter) ?? string.Empty,
                    result.GrantedQualityOfService));
                AppendLog(LfLogLevel.Information, $"订阅成功：{result.TopicFilter}，授权 QoS {result.GrantedQualityOfService}。");
            }

            State = TaskRunState.Completed;
            CurrentStage = "订阅成功";
            ApplyConnectionSummary();
            OnPropertyChanged(nameof(HasSubscriptions));
            OnPropertyChanged(nameof(SubscriptionCountText));
        }
        catch (MqttTransportException ex)
        {
            SubscribeError = SecretRedactor.Redact(ex.Message) ?? "订阅失败。";
            State = TaskRunState.Failed;
            CurrentStage = "订阅失败";
            AppendLog(LfLogLevel.Critical, ex.Message);
        }
        catch (OperationCanceledException)
        {
            SubscribeError = "订阅已取消。";
            State = TaskRunState.Cancelled;
            CurrentStage = "订阅已取消";
        }
        finally
        {
            IsBusy = false;
            _subscribeCts = null;
            NotifyCommands();
        }
    }

    /// <summary>发布。</summary>
    [RelayCommand(CanExecute = nameof(CanPublish))]
    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        PublishError = ValidatePublish();

        if (PublishError is not null)
        {
            return;
        }

        IsBusy = true;
        CurrentStage = "正在发布消息…";
        State = TaskRunState.Running;
        ActionFeedback = string.Empty;
        NotifyCommands();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _publishCts = linked;
        NotifyCommands();

        try
        {
            await _transport.PublishAsync(
                new MqttPublishRequest(
                    PublishTopic.Trim(),
                    PublishPayload,
                    ParseQos(PublishQosText),
                    Retain),
                linked.Token);

            RecordMessage(new MqttMessageEntry(
                DateTimeOffset.Now,
                PublishTopic.Trim(),
                PublishPayload,
                ParseQos(PublishQosText),
                Retain,
                Encoding.UTF8.GetByteCount(PublishPayload),
                true));

            AppendLog(LfLogLevel.Information, $"已发布到 {PublishTopic.Trim()}。");
            State = TaskRunState.Completed;
            CurrentStage = "发布成功";
            ActionFeedback = $"已发布 {Encoding.UTF8.GetByteCount(PublishPayload):N0} 字节。";
            ApplyConnectionSummary();
        }
        catch (MqttTransportException ex)
        {
            PublishError = SecretRedactor.Redact(ex.Message) ?? "发布失败。";
            State = TaskRunState.Failed;
            CurrentStage = "发布失败";
            ActionFeedback = PublishError;
            AppendLog(LfLogLevel.Critical, ex.Message);
        }
        catch (OperationCanceledException)
        {
            PublishError = "发布已取消。";
            State = TaskRunState.Cancelled;
            CurrentStage = "发布已取消";
            ActionFeedback = PublishError;
        }
        finally
        {
            IsBusy = false;
            _publishCts = null;
            NotifyCommands();
        }
    }

    /// <summary>取消当前连接、断开、订阅或发布。</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _operationCts?.Cancel();
        _subscribeCts?.Cancel();
        _publishCts?.Cancel();
    }

    /// <summary>暂停/继续显示。</summary>
    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    /// <summary>清空消息。</summary>
    [RelayCommand]
    private void ClearMessages()
    {
        _buffer.Clear();
        Messages.Clear();
        PendingMessageCount = 0;
        NotifyPendingChanged();
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(MessageCountText));
    }

    /// <summary>复制脱敏消息。</summary>
    [RelayCommand]
    private void CopyMessages()
    {
        if (Messages.Count == 0)
        {
            ActionFeedback = "当前没有可复制的消息。";
            return;
        }

        try
        {
            _exporter.Copy(BuildCopyText());
            ActionFeedback = "已复制脱敏消息。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            ActionFeedback = $"复制失败：{ex.Message}";
        }
    }

    /// <summary>导出 JSON。</summary>
    [RelayCommand]
    private Task ExportJsonAsync(CancellationToken cancellationToken) =>
        ExportAsync("json", cancellationToken);

    /// <summary>导出 CSV。</summary>
    [RelayCommand]
    private Task ExportCsvAsync(CancellationToken cancellationToken) =>
        ExportAsync("csv", cancellationToken);

    private async Task ExportAsync(string format, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0)
        {
            ActionFeedback = "当前没有可导出的消息。";
            return;
        }

        try
        {
            var isCsv = format == "csv";
            var fileName = $"linkforge-mqtt-{DateTime.Now:yyyyMMdd-HHmmss}.{format}";
            var content = isCsv ? BuildCsv() : BuildJson();
            var filter = isCsv
                ? "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
                : "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";

            var path = await _exporter.ExportAsync(
                fileName,
                content,
                filter,
                cancellationToken);

            ActionFeedback = path is null ? "已取消导出。" : $"已导出：{path}";
        }
        catch (OperationCanceledException)
        {
            ActionFeedback = "已取消导出。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ActionFeedback = $"导出失败：{ex.Message}";
        }
    }

    partial void OnFilterTextChanged(string value) => RebuildVisibleMessages();

    private void OnMessageReceived(object? sender, MqttReceivedMessage message)
    {
        Post(() =>
        {
            RecordMessage(new MqttMessageEntry(
                message.ReceivedAt,
                message.Topic,
                message.Payload,
                message.QualityOfService,
                message.Retain,
                message.PayloadBytes,
                false));

            AppendLog(LfLogLevel.Debug, $"收到 {SecretRedactor.Redact(message.Topic)}。");
        });
    }

    private void OnConnectionStatusChanged(object? sender, MqttConnectionStatusChanged status)
    {
        Post(() =>
        {
            if (status.IsConnected)
            {
                ConnectionState = MqttUiConnectionState.Connected;
                ConnectionText = "已连接";
                State = TaskRunState.Completed;
                CurrentStage = "已连接";
                HasSession = true;
                ApplyConnectionSummary();
            }
            else
            {
                if (_disconnectRequested)
                {
                    // 主动断开时传输层也会回调 Disconnected；
                    // 此时不能把结果改写成“连接中断”，否则与用户操作矛盾。
                    _disconnectRequested = false;
                    NotifyCommands();
                    return;
                }

                ConnectionState = MqttUiConnectionState.Disconnected;
                ConnectionText = "已断开";
                State = TaskRunState.Inconclusive;
                CurrentStage = "连接中断";
                HasError = true;
                ErrorMessage = "Broker 主动断开或网络中断，当前未连接。";
                ErrorHint = "检查 Broker 状态与网络；重连前不需要重启应用。";
                AppendLog(LfLogLevel.Warning, SecretRedactor.Redact(status.Detail ?? status.Summary) ?? status.Summary);
                SummaryItems.Clear();
                SummaryItems.Add(new ResultSummaryItem
                {
                    Label = "结论",
                    Value = ErrorMessage,
                    Status = DiagnosticStatus.Unknown
                });
                NotifyCommands();
            }
        });
    }

    /// <summary>
    /// 记录一条消息。
    /// </summary>
    /// <remarks>
    /// 暂停时只写缓冲、不动可见列表；恢复时由 <see cref="RebuildVisibleMessages"/>
    /// 一次性补齐，避免逐条刷新造成卡顿，也避免“暂停即丢消息”。
    /// </remarks>
    private void RecordMessage(MqttMessageEntry message)
    {
        _buffer.Insert(0, message);
        OnPropertyChanged(nameof(MessageCountText));

        while (_buffer.Count > BufferLimit)
        {
            _buffer.RemoveAt(_buffer.Count - 1);
        }

        if (IsPaused)
        {
            PendingMessageCount++;
            NotifyPendingChanged();
            return;
        }

        if (!MatchesFilter(message))
        {
            return;
        }

        Messages.Insert(0, message);
        OnPropertyChanged(nameof(HasMessages));

        while (Messages.Count > BufferLimit)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }
    }

    private void RebuildVisibleMessages()
    {
        Messages.Clear();

        foreach (var message in _buffer)
        {
            if (MatchesFilter(message))
            {
                Messages.Add(message);
            }
        }

        PendingMessageCount = 0;
        NotifyPendingChanged();
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(MessageCountText));
    }

    private bool MatchesFilter(MqttMessageEntry message) =>
        string.IsNullOrWhiteSpace(FilterText)
        || message.Topic.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
        || message.DisplayPayload.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    private void NotifyPendingChanged()
    {
        OnPropertyChanged(nameof(HasPendingMessages));
        OnPropertyChanged(nameof(PauseNotice));
    }

    private void AppendLog(LfLogLevel level, string message)
    {
        var entry = new LogEntry(
            DateTimeOffset.Now,
            level,
            "MQTT",
            string.Empty,
            SecretRedactor.Redact(Host),
            message);

        Logs.Add(entry);
    }

    private bool ValidateConnection()
    {
        HostError = string.IsNullOrWhiteSpace(Host) ? "Broker 地址不能为空。" : null;
        PortError = IsPortValid(PortText) ? null : "端口必须是 1–65535 的整数。";
        ClientIdError = string.IsNullOrWhiteSpace(ClientId) ? "Client ID 不能为空。" : null;
        ConnectTimeoutError = IsTimeoutValid(ConnectTimeoutText) ? null : "超时必须是 1–300 秒的整数。";

        return HostError is null && PortError is null && ClientIdError is null && ConnectTimeoutError is null;
    }

    private string? ValidateSubscription()
    {
        if (string.IsNullOrWhiteSpace(SubscribeTopic))
        {
            return "订阅 Topic 不能为空。";
        }

        return ValidateQos(SubscribeQosText, out _) ? null : "订阅 QoS 只能是 0、1 或 2。";
    }

    private string? ValidatePublish()
    {
        if (string.IsNullOrWhiteSpace(PublishTopic)
            || PublishTopic.Contains('#', StringComparison.Ordinal)
            || PublishTopic.Contains('+', StringComparison.Ordinal))
        {
            return "发布 Topic 不能为空，也不能包含通配符。";
        }

        return ValidateQos(PublishQosText, out _) ? null : "发布 QoS 只能是 0、1 或 2。";
    }

    private string BuildCopyText()
    {
        var builder = new StringBuilder();

        foreach (var message in Messages)
        {
            builder.AppendLine($"[{message.TimeText}] {message.DirectionText} {message.Topic} ({message.MetadataText})");
            builder.AppendLine(message.DisplayPayload);

            if (message.IsPayloadTruncated)
            {
                builder.AppendLine(message.PayloadNotice);
            }
        }

        builder.AppendLine("说明：Topic 与载荷在显示、复制和导出前均已脱敏；凭据不在此文本中。");
        return builder.ToString();
    }

    private string BuildJson()
    {
        var document = new
        {
            product = "LinkForge",
            kind = "mqtt-message-stream",
            generatedAt = DateTimeOffset.Now,
            connection = new
            {
                host = SecretRedactor.Redact(Host),
                port = ParsePort(PortText),
                tls = UseTls,
                state = ConnectionState.ToString(),
                clientId = SecretRedactor.Redact(ClientId)
            },
            subscriptions = Subscriptions.Select(item => item.DisplayText),
            messages = Messages.Select(message => new
            {
                time = message.ReceivedAt,
                direction = message.DirectionText,
                topic = message.Topic,
                payload = message.DisplayPayload,
                qos = message.QualityOfService,
                retain = message.Retain,
                bytes = message.PayloadBytes,
                truncated = message.IsPayloadTruncated
            }),
            redactionNotice = "所有 Topic 与载荷在导出前均已脱敏；用户名与口令不导出。"
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }

    private string BuildCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("time,direction,topic,payload,qos,retain,bytes,truncated");

        foreach (var message in Messages)
        {
            builder.AppendLine(string.Join(
                ',',
                Csv(message.ReceivedAt.ToString("O", CultureInfo.InvariantCulture)),
                Csv(message.DirectionText),
                Csv(message.Topic),
                Csv(message.DisplayPayload),
                message.QualityOfService.ToString(CultureInfo.InvariantCulture),
                message.Retain ? "true" : "false",
                message.PayloadBytes.ToString(CultureInfo.InvariantCulture),
                message.IsPayloadTruncated ? "true" : "false"));
        }

        return builder.ToString();
    }

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static bool IsPortValid(string text) =>
        int.TryParse(text, out var port) && port is >= 1 and <= 65535;

    private static bool IsTimeoutValid(string text) =>
        int.TryParse(text, out var timeout) && timeout is >= 1 and <= 300;

    private static bool ValidateQos(string text, out int qos) =>
        int.TryParse(text, out qos) && qos is >= 0 and <= 2;

    private static int ParsePort(string text) => int.TryParse(text, out var port) ? port : 1883;

    private static int ParseTimeout(string text) => int.TryParse(text, out var timeout) ? timeout : 10;

    private static int ParseQos(string text) => int.TryParse(text, out var qos) ? qos : 0;

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanSubscribe));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(CanCancel));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        SubscribeCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void ApplyConnectionSummary()
    {
        SummaryItems.Clear();
        SummaryItems.Add(new ResultSummaryItem
        {
            Label = "Broker",
            Value = $"{SecretRedactor.Redact(Host)}:{ParsePort(PortText)}"
        });
        SummaryItems.Add(new ResultSummaryItem
        {
            Label = "TLS",
            Value = UseTls ? "启用（校验证书）" : "关闭"
        });
        SummaryItems.Add(new ResultSummaryItem
        {
            Label = "状态",
            Value = ConnectionText,
            Status = IsConnected ? DiagnosticStatus.Passed : DiagnosticStatus.Unknown
        });
        SummaryItems.Add(new ResultSummaryItem
        {
            Label = "订阅",
            Value = SubscriptionCountText
        });
    }

    private async Task TickElapsedAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var elapsed = _timeProvider.GetUtcNow() - startedAt;
            _ = PostAsync(() => ElapsedText = DashboardViewModel.FormatElapsed(elapsed));
        }
    }

    private static string HintFor(MqttFailureKind kind) => kind switch
    {
        MqttFailureKind.AuthenticationFailed =>
            "检查用户名、口令与 Broker 的访问控制；口令不会写入日志。",
        MqttFailureKind.TimedOut =>
            "检查 Broker 地址、端口、防火墙与网络延迟；可适当提高连接超时。",
        MqttFailureKind.ConnectionFailed =>
            "确认 Broker 正在监听、端口正确；启用 TLS 时确认证书链与主机名有效。",
        MqttFailureKind.InvalidOptions =>
            "修正字段错误后重试。",
        _ => "查看日志中的技术信息后重试。"
    };

    /// <summary>
    /// 处理非协议异常：客户端库缺失必须显示“依赖缺失”，而不是普通连接失败。
    /// </summary>
    /// <remarks>
    /// MQTTnet 以 NuGet 随应用分发，正常安装不会缺；
    /// 但单文件裁剪、杀软隔离或手工替换 DLL 都可能导致运行时加载失败。
    /// 这类问题重试没有意义，必须给出可执行的修复方向，且不能伪装成网络故障。
    /// </remarks>
    private void ApplyUnexpectedFailure(Exception exception)
    {
        var missingType = exception is TypeInitializationException or DllNotFoundException
            or FileNotFoundException or FileLoadException
            or BadImageFormatException;

        if (missingType)
        {
            IsDependencyMissing = true;
            MissingDependencyName = "MQTTnet 客户端库";
            MissingDependencyImpact = "无法创建 MQTT 客户端，连接、订阅、发布与消息接收全部不可用。";
            MissingDependencyGuidance =
                "请确认发布包中包含 MQTTnet.dll 且未被安全软件隔离；"
                + "若为 x86 进程，请确认使用的是 x86 兼容构建。";
            ErrorMessage = "MQTT 客户端库加载失败，当前无法连接 Broker。";
            ErrorHint = MissingDependencyGuidance;
            ConnectionState = MqttUiConnectionState.Disconnected;
            ConnectionText = "依赖缺失";
            CurrentStage = "依赖缺失";
            return;
        }

        HasError = true;
        ErrorMessage = SecretRedactor.Redact($"{exception.GetType().Name}：{exception.Message}")
            ?? "MQTT 操作失败。";
        ErrorHint = "请检查 Broker 地址、网络、TLS 与客户端库状态。";
    }

    private void Post(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return;
        }

        _uiContext.Post(static state => ((Action)state!)(), action);
    }

    /// <summary>
    /// 投递到 UI 线程并等待执行完成。
    /// </summary>
    /// <remarks>
    /// 传输事件在后台线程触发，读集合前必须等待投递完成；
    /// 否则测试与调用方会看到“命令已结束但消息还没进列表”的中间态。
    /// </remarks>
    private Task PostAsync(Action action)
    {
        if (_uiContext is null)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource();

        _uiContext.Post(
            _ =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            },
            null);

        return completion.Task;
    }
}
