namespace LFTPPilot.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    private readonly object _gate = new();
    private readonly HashSet<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = initialUtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _utcNow;
    }

    public override long GetTimestamp()
    {
        lock (_gate) return _utcNow.UtcTicks;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public int ScheduledTimerCount
    {
        get
        {
            lock (_gate) return _timers.Count(timer => !timer.Disposed && timer.DueAt is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            Change(timer, dueTime, period);
        }
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
        lock (_gate) _utcNow += amount;

        while (true)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                foreach (var timer in _timers.ToArray())
                {
                    if (timer.Disposed || timer.DueAt is not { } dueAt || dueAt > _utcNow) continue;
                    callbacks.Add((timer.Callback, timer.State));
                    timer.DueAt = timer.Period == Timeout.InfiniteTimeSpan ? null : _utcNow + timer.Period;
                }
            }
            if (callbacks.Count == 0) return;
            foreach (var callback in callbacks) callback.Callback(callback.State);
        }
    }

    private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        ValidateTimeout(dueTime, nameof(dueTime));
        ValidateTimeout(period, nameof(period));
        lock (_gate)
        {
            if (timer.Disposed) return false;
            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _utcNow + dueTime;
            timer.Period = period;
            return true;
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            timer.Disposed = true;
            timer.DueAt = null;
            _timers.Remove(timer);
        }
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? DueAt { get; set; }
        public TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;
        public bool Disposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
