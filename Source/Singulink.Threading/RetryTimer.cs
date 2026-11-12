using Singulink.Threading.Utilities;

namespace Singulink.Threading;

/// <summary>
/// Provides the waits between attempts of an operation that is retried after a delay until a time limit passes, with time measured by a
/// <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// The caller makes an attempt, decides whether a failure is worth another try, and if it is, calls <see cref="TryWait"/> or
/// <see cref="TryWaitAsync"/>. These wait for the delay and return <see langword="true"/> while the time limit has not passed, and return
/// <see langword="false"/> without waiting once it has. The timer starts when it is created with <see cref="Start(TimeSpan, TimeSpan)"/>:</para>
/// <code language="csharp">
/// var retry = RetryTimer.Start(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
///
/// while (!TryMoveFile(source, destination))
/// {
///     if (!await retry.TryWaitAsync(cancellationToken))
///         return false;
/// }
///
/// return true;
/// </code>
/// <para>
/// The last wait is cut short so that it ends when the time limit passes, so a final attempt is made at the limit. Waits are rounded up to whole
/// milliseconds, the resolution of system timers, so that a wait is never cut to nothing and the attempts never run without a pause. A time limit of
/// zero allows one attempt and no retries.</para>
/// <para>
/// Code that has to decide whether to give up separately from the wait, for example while it holds a lock that must not be held across the wait, can
/// check <see cref="CanRetry"/> and then call <see cref="Wait"/> or <see cref="WaitAsync"/>.</para>
/// <para>
/// The timer is a small immutable struct: starting it and checking it allocate nothing, so an operation that succeeds on its first attempt costs no
/// allocation. Copies of the timer share its start time.</para>
/// </remarks>
public readonly struct RetryTimer
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly TimeProvider? _timeProvider;
    private readonly long _startTimestamp;
    private readonly TimeSpan _timeLimit;
    private readonly TimeSpan _delay;

    private RetryTimer(TimeSpan timeLimit, TimeSpan delay, TimeProvider timeProvider)
    {
        if (timeLimit < TimeSpan.Zero && timeLimit != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeLimit), timeLimit, "The value is negative and is not Timeout.InfiniteTimeSpan.");

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(delay));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delay, MaxDelay, nameof(delay));

        _timeProvider = timeProvider;
        _timeLimit = timeLimit;
        _delay = delay;
        _startTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>
    /// Gets a value indicating whether the instance is in its default, uninitialized state.
    /// </summary>
    public bool IsDefault => _timeProvider is null;

    /// <summary>
    /// Gets the time that has passed since the timer was started.
    /// </summary>
    public TimeSpan Elapsed
    {
        get {
            Throw.NotInitializedIf(_timeProvider is null);
            return _timeProvider.GetElapsedTime(_startTimestamp);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the time limit has not yet passed, so another attempt can be made after a wait.
    /// </summary>
    public bool CanRetry => GetWaitTime() > TimeSpan.Zero;

    /// <summary>
    /// Starts a timer that measures time with <see cref="TimeProvider.System"/>.
    /// </summary>
    /// <param name="timeLimit">The time from now after which no more attempts are made. Must be at least zero, or <see cref="Timeout.InfiniteTimeSpan"/>
    /// to retry until a wait is canceled.</param>
    /// <param name="delay">The time to wait between two attempts. Must be greater than zero and at most <see cref="int.MaxValue"/> milliseconds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeLimit"/> is negative and is not <see cref="Timeout.InfiniteTimeSpan"/>, or
    /// <paramref name="delay"/> is out of range.</exception>
    public static RetryTimer Start(TimeSpan timeLimit, TimeSpan delay) => new(timeLimit, delay, TimeProvider.System);

    /// <summary>
    /// Starts a timer that measures time with the specified time provider.
    /// </summary>
    /// <param name="timeLimit">The time from now after which no more attempts are made. Must be at least zero, or <see cref="Timeout.InfiniteTimeSpan"/>
    /// to retry until a wait is canceled.</param>
    /// <param name="delay">The time to wait between two attempts. Must be greater than zero and at most <see cref="int.MaxValue"/> milliseconds.</param>
    /// <param name="timeProvider">The time provider that measures the time limit and the waits.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeLimit"/> is negative and is not <see cref="Timeout.InfiniteTimeSpan"/>, or
    /// <paramref name="delay"/> is out of range.</exception>
    public static RetryTimer Start(TimeSpan timeLimit, TimeSpan delay, TimeProvider timeProvider) => new(timeLimit, delay, timeProvider);

    /// <summary>
    /// Waits before another attempt if the time limit has not passed.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns><see langword="true"/> after the wait if another attempt can be made; <see langword="false"/> without waiting if the time limit has
    /// passed.</returns>
    /// <remarks>
    /// The wait is the delay, or the time left until the time limit passes if that is shorter, rounded up to whole milliseconds. The calling thread is
    /// blocked during the wait.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before or during the wait.</exception>
    public bool TryWait(CancellationToken cancellationToken = default)
    {
        var waitTime = GetWaitTime();

        if (waitTime <= TimeSpan.Zero)
            return false;

        Sleep(waitTime, cancellationToken);
        return true;
    }

    /// <summary>
    /// Asynchronously waits before another attempt if the time limit has not passed.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A task that contains <see langword="true"/> after the wait if another attempt can be made, or <see langword="false"/> without waiting if
    /// the time limit has passed.</returns>
    /// <remarks>
    /// The wait is the delay, or the time left until the time limit passes if that is shorter, rounded up to whole milliseconds. When the time limit has
    /// passed, the returned task has already completed and nothing is allocated.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before or during the wait.</exception>
    public ValueTask<bool> TryWaitAsync(CancellationToken cancellationToken = default)
    {
        var waitTime = GetWaitTime();

        if (waitTime <= TimeSpan.Zero)
            return new(false);

        return new(DelayThenTrueAsync(Task.Delay(waitTime, _timeProvider!, cancellationToken)));

        static async Task<bool> DelayThenTrueAsync(Task delay)
        {
            await delay.ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// Waits before another attempt.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <remarks>
    /// The wait is the delay, or the time left until the time limit passes if that is shorter, rounded up to whole milliseconds. If the time limit has
    /// passed, the method returns at once. The calling thread is blocked during the wait.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before or during the wait.</exception>
    public void Wait(CancellationToken cancellationToken = default)
    {
        var waitTime = GetWaitTime();

        if (waitTime > TimeSpan.Zero)
            Sleep(waitTime, cancellationToken);
    }

    /// <summary>
    /// Asynchronously waits before another attempt.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A task that completes when the wait is over.</returns>
    /// <remarks>
    /// The wait is the delay, or the time left until the time limit passes if that is shorter, rounded up to whole milliseconds. If the time limit has
    /// passed, the returned task has already completed.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before or during the wait.</exception>
    public ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        var waitTime = GetWaitTime();

        if (waitTime <= TimeSpan.Zero)
            return default;

        return new(Task.Delay(waitTime, _timeProvider!, cancellationToken));
    }

    /// <summary>
    /// Gets the time to wait before the next attempt: the delay, or the time left until the time limit passes if that is shorter, rounded up to whole
    /// milliseconds. Zero or negative when the time limit has passed.
    /// </summary>
    private TimeSpan GetWaitTime()
    {
        Throw.NotInitializedIf(_timeProvider is null);

        var waitTime = _delay;

        if (_timeLimit != Timeout.InfiniteTimeSpan)
        {
            var remaining = _timeLimit - _timeProvider.GetElapsedTime(_startTimestamp);

            if (remaining <= TimeSpan.Zero)
                return remaining;

            if (remaining < waitTime)
                waitTime = remaining;
        }

        // System timers, Thread.Sleep and Task.Delay truncate to whole milliseconds, which would turn a wait of less than one into no wait at all.
        long partialTicks = waitTime.Ticks % TimeSpan.TicksPerMillisecond;
        return partialTicks is 0 ? waitTime : TimeSpan.FromTicks(waitTime.Ticks - partialTicks + TimeSpan.TicksPerMillisecond);
    }

    private void Sleep(TimeSpan waitTime, CancellationToken cancellationToken)
    {
        if (_timeProvider == TimeProvider.System)
        {
            // Blocks without allocating a task or a timer.

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.WaitHandle.WaitOne(waitTime);
                cancellationToken.ThrowIfCancellationRequested();
            }
            else
            {
                Thread.Sleep(waitTime);
            }
        }
        else
        {
            // Other time providers can only be waited on through a timer they create, which Task.Delay sets up.
            Task.Delay(waitTime, _timeProvider!, cancellationToken).GetAwaiter().GetResult();
        }
    }
}
