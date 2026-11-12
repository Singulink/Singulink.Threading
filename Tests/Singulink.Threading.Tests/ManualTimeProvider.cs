namespace Singulink.Threading.Tests;

/// <summary>
/// A time provider whose time only moves when <see cref="Advance"/> is called. Timers fire on the thread that advances the time.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// Gets the number of timers that are waiting to fire.
    /// </summary>
    public int PendingTimerCount
    {
        get {
            lock (_sync)
                return _timers.Count(t => t.DueTicks is not null);
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync)
            return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the time forward and fires the timers that are due, in order of their due times.
    /// </summary>
    public void Advance(TimeSpan time)
    {
        long target;

        lock (_sync)
        {
            target = _now + time.Ticks;
        }

        while (true)
        {
            ManualTimer? next;

            lock (_sync)
            {
                next = _timers.Where(t => t.DueTicks <= target).MinBy(t => t.DueTicks);

                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = Math.Max(_now, next.DueTicks!.Value);
                next.DueTicks = next.PeriodTicks is long period ? _now + period : null;
            }

            next.Fire();
        }
    }

    /// <summary>
    /// Waits until at least the specified number of timers are waiting to fire, for code under test that creates a timer on another thread.
    /// </summary>
    public void WaitForPendingTimers(int count)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (PendingTimerCount < count)
        {
            if (DateTime.UtcNow > timeout)
                throw new TimeoutException("The expected timers were not created.");

            Thread.Sleep(1);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueTicks { get; set; }

        public long? PeriodTicks { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (!owner._timers.Contains(this))
                    owner._timers.Add(this);

                DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime.Ticks;
                PeriodTicks = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.Ticks;
                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._sync)
                owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
