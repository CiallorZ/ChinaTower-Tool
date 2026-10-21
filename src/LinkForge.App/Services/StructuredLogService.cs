using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LinkForge.App.Presentation;
using LinkForge.Application.Logging;

namespace LinkForge.App.Services;

/// <summary>
/// 应用级结构化日志聚合与持久化。
/// </summary>
/// <remarks>
/// 页面仍可保留自己的局部日志视图，但壳层日志面板只订阅本服务。
/// 所有条目在进入 UI 前已经由 <see cref="LogEntry"/> 脱敏，
/// 文件存储写入前还会再次经过 <see cref="IStructuredLogStore"/> 的统一脱敏。
/// </remarks>
public interface IStructuredLogService : IDisposable, INotifyPropertyChanged
{
    /// <summary>UI 可观察的最近日志。</summary>
    ObservableCollection<LogEntry> Entries { get; }

    /// <summary>最近一次日志文件错误；正常时为空。</summary>
    string? StorageError { get; }

    /// <summary>把页面局部日志集合接入应用级日志。</summary>
    void Attach(ObservableCollection<LogEntry> source);

    /// <summary>发布一条已脱敏日志。</summary>
    void Publish(LogEntry entry);

    /// <summary>读取当日历史日志；失败不会阻断应用启动。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>等待当前已发布的日志写入磁盘。</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 进程内结构化日志服务。
/// </summary>
public sealed class StructuredLogService : ObservableObject, IStructuredLogService
{
    /// <summary>日志面板最多保留的内存条目数。</summary>
    public const int MaximumEntries = 5000;

    private readonly IStructuredLogStore _store;
    private readonly System.Threading.Channels.Channel<QueueItem> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writerTask;
    private readonly HashSet<ObservableCollection<LogEntry>> _attachedSources = [];
    private readonly Dispatcher? _uiDispatcher;
    private string? _storageError;
    private bool _disposed;

    /// <summary>创建日志服务。</summary>
    public StructuredLogService(IStructuredLogStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        // 在应用启动时构造服务，此时当前线程就是 UI Dispatcher。
        // 测试宿主或后台构造没有 Dispatcher 时直接执行，避免误投到无关的
        // Application.Current 队列而让状态更新永远等待一个不存在的消息泵。
        _uiDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        _queue = System.Threading.Channels.Channel.CreateUnbounded<QueueItem>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        _writerTask = Task.Run(ProcessQueueAsync);
    }

    /// <inheritdoc />
    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <inheritdoc />
    public string? StorageError
    {
        get => _storageError;
        private set
        {
            if (SetProperty(ref _storageError, value))
            {
                OnPropertyChanged(nameof(HasStorageError));
            }
        }
    }

    /// <summary>是否发生日志文件错误。</summary>
    public bool HasStorageError => !string.IsNullOrWhiteSpace(StorageError);

    /// <inheritdoc />
    public void Attach(ObservableCollection<LogEntry> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!_attachedSources.Add(source))
        {
            return;
        }

        foreach (var entry in source.ToArray())
        {
            Publish(entry);
        }

        source.CollectionChanged += OnSourceCollectionChanged;
    }

    /// <inheritdoc />
    public void Publish(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_disposed)
        {
            return;
        }

        PostToUi(() => AddToVisibleEntries(entry));

        if (_queue.Writer.TryWrite(new QueueItem(entry.ToStructuredRecord(), null)))
        {
            return;
        }

        SetStorageError("日志写入队列已关闭，当前日志未能持久化。");
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var records = await _store
                .ReadRecentAsync(MaximumEntries, cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in records)
            {
                var entry = LogEntry.FromStructuredRecord(record);
                PostToUi(() => AddToVisibleEntries(entry));
            }

            SetStorageError(null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException)
        {
            SetStorageError($"读取历史日志失败：{ex.GetType().Name} — {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_queue.Writer.TryWrite(new QueueItem(null, completion)))
        {
            return;
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var source in _attachedSources)
        {
            source.CollectionChanged -= OnSourceCollectionChanged;
        }

        _attachedSources.Clear();
        _queue.Writer.TryComplete();

        try
        {
            if (!_writerTask.Wait(TimeSpan.FromSeconds(3)))
            {
                _shutdown.Cancel();
                _writerTask.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static e => e is OperationCanceledException))
        {
            // 关闭期间的取消是预期行为。
        }

        _shutdown.Dispose();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (var item in e.NewItems.OfType<LogEntry>())
        {
            Publish(item);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                if (item.Record is not null)
                {
                    try
                    {
                        await _store.AppendAsync(item.Record, _shutdown.Token).ConfigureAwait(false);
                        SetStorageError(null);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is IOException
                        or UnauthorizedAccessException
                        or NotSupportedException
                        or ArgumentException)
                    {
                        SetStorageError($"写入日志失败：{ex.GetType().Name} — {ex.Message}");
                    }
                }

                item.Completion?.TrySetResult();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // 正常关闭。
        }
    }

    private void AddToVisibleEntries(LogEntry entry)
    {
        Entries.Add(entry);

        while (Entries.Count > MaximumEntries)
        {
            Entries.RemoveAt(0);
        }
    }

    private void SetStorageError(string? message)
    {
        PostToUi(() => StorageError = message);
    }

    private void PostToUi(Action action)
    {
        if (_uiDispatcher is null
            || _uiDispatcher.HasShutdownStarted
            || _uiDispatcher.HasShutdownFinished
            || _uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _uiDispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            // 应用正在退出时 dispatcher 已关闭；不再制造新的 UI 工作。
        }
    }

    private sealed record QueueItem(
        StructuredLogRecord? Record,
        TaskCompletionSource? Completion);
}
