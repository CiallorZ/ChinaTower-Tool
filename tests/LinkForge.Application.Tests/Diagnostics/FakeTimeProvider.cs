namespace LinkForge.Application.Tests.Diagnostics;

/// <summary>
/// 可手动推进的假时钟，用于在测试里精确触发超时而不真的等待。
/// </summary>
/// <remarks>
/// <para>
/// 刻意自己实现而不是引入 <c>Microsoft.Extensions.TimeProvider.Testing</c>：
/// 本项目只需要“推进时间 + 触发定时器”两项能力，
/// 为此新增一个运行期可见的依赖（还要走 ADR 登记）不值得。
/// 本类位于测试项目，不进入任何产品程序集。
/// </para>
/// <para>
/// 为什么必须能触发 <see cref="TimeProvider.CreateTimer"/>：
/// <c>CancellationTokenSource(TimeSpan, TimeProvider)</c> 内部就是用定时器实现超时的。
/// 一个只会返回时间的假时钟会让所有超时测试静默失效——
/// 表现为“测试通过了，但超时路径从未被走到”。
/// </para>
/// </remarks>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now;

    /// <summary>
    /// 创建假时钟。
    /// </summary>
    /// <param name="start">起始时间；null 时使用固定基准时间。</param>
    public FakeTimeProvider(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <inheritdoc />
    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new FakeTimer(this, callback, state);

        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);

        return timer;
    }

    /// <summary>
    /// 把时钟向前推进，并触发所有到期的定时器。
    /// </summary>
    /// <param name="delta">推进的时间量。</param>
    /// <remarks>
    /// 先推进时间再触发回调：回调里通常会再次读取 <see cref="GetUtcNow"/>，
    /// 若顺序反过来，回调看到的是旧时间，超时判断会差一个 tick。
    /// </remarks>
    public void Advance(TimeSpan delta)
    {
        List<FakeTimer> due;

        lock (_gate)
        {
            _now = _now.Add(delta);
            due = [.. _timers.Where(t => t.IsDue(_now))];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer : ITimer
    {
        private readonly FakeTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool IsDue(DateTimeOffset now) =>
            !_disposed && _dueAt is { } due && now >= due;

        public void Fire()
        {
            if (_disposed)
            {
                return;
            }

            if (_period > TimeSpan.Zero)
            {
                _dueAt = _dueAt?.Add(_period);
            }
            else
            {
                // 一次性定时器：触发后不再到期，但对象仍存活直到 Dispose，
                // 与 System.Threading.Timer 的语义一致。
                _dueAt = null;
            }

            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            _period = period;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : _owner.GetUtcNow().Add(dueTime);

            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            _dueAt = null;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}