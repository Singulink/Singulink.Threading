using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Singulink.CodeAnalysis;
using Singulink.Enums;

namespace Singulink.Threading;

/// <summary>
/// A reader/writer lock for asynchronous code that can be held across an <see langword="await"/>.
/// </summary>
/// <remarks>
/// <para>
/// Any number of callers can hold the lock in read mode together, or one caller can hold it in write mode. The lock is entered through guards, like
/// <see cref="ReadWriteLock"/>: <c>Enter…GuardAsync</c> methods wait until the lock is entered, <c>TryEnter…GuardAsync</c> methods give up after a time
/// limit, and <c>TryEnter…Guard</c> methods do not wait at all. The <c>TryEnter</c> methods return a guard whose <c>IsEntered</c> property says whether
/// the lock was entered. Disposing a guard that did not enter the lock does nothing, so a result can always be assigned to a <see langword="using"/>
/// declaration:</para>
/// <code language="csharp">
/// using var guard = await rwLock.TryEnterReadGuardAsync(TimeSpan.FromSeconds(5), cancellationToken);
///
/// if (!guard.IsEntered)
///     return false;
///
/// await ReadDataAsync();
/// return true;
/// </code>
/// <para>
/// The lock has no thread affinity and no owner: a guard can be disposed on any thread, by any code that has it, so it can be held across an
/// <see langword="await"/>. For the same reason the lock is not reentrant and cannot tell that a caller already holds it. A caller that holds it in write
/// mode and enters it again, or holds it in read mode and enters it in write mode, waits for itself, and without a time limit that wait never
/// ends. Entering read mode again while holding it in read mode is a second hold that depends on the <see cref="Preference"/>: it never waits
/// under <see cref="AsyncReadWriteLockPreference.Readers"/>, and under the other two it waits behind a waiting writer, which is waiting for the
/// first hold, so it never ends either.</para>
/// <para>
/// The <see cref="Preference"/> given at construction decides who enters first when readers and writers both want the lock, and with that who can be
/// kept waiting forever; see <see cref="AsyncReadWriteLockPreference"/>. Writers always enter in the order they arrived. A waiter that gives up, because
/// its time limit passed or it was canceled, leaves the queue at once, and the callers it was holding up enter if they then can.</para>
/// <para>
/// Entering the lock without a wait and leaving it allocate nothing, and take one interlocked operation each while nothing is waiting for the lock; a
/// caller whose release lets a waiter in also takes a short internal lock. A call that has to wait allocates one object for the wait, a timer if it
/// has a time limit, and what registering with its cancellation token costs. The code that was waiting never continues on the thread that released
/// the lock, canceled the wait or ran the timer: its continuation is queued to the thread pool, or posted to the
/// <see cref="SynchronizationContext"/> or <see cref="TaskScheduler"/> that the <see langword="await"/> captured.</para>
/// <para>
/// The asynchronous methods throw directly only for an argument that is out of range. Every other failure is in the returned task: a canceled token,
/// too many read holds, or an exception that the <see cref="TimeProvider"/> throws when it creates the timer of a time limit. A call that fails
/// holds nothing and is not left waiting.</para>
/// <para>
/// Time limits are measured with the <see cref="TimeProvider"/> given at construction, <see cref="TimeProvider.System"/> by default. The lock holds no
/// resources between calls and is not disposable; a wait is ended early by canceling its token. There is no upgradeable read mode, no way to turn a
/// write hold into a read hold, and no method that blocks the calling thread until the lock is entered.</para>
/// <para>
/// A failure in resuming one waiter never reaches another caller. If the continuation of a wait cannot be scheduled when the lock completes the
/// wait, because the <see cref="SynchronizationContext"/> or <see cref="TaskScheduler"/> it was captured on throws, the exception is thrown to
/// nobody: not to the caller that released the lock, not to the code that canceled a token, and not on the thread of a timer. If that wait had
/// been granted the lock, the lock takes the hold back and passes it to whoever is next, so the lock is never left held by code that will not
/// run.</para>
/// <para>
/// The code that was awaiting the lock then normally never continues, as with any awaitable whose context refuses its continuation. A
/// context that runs or queues the continuation and throws all the same lets the code continue: it then either has the lock as usual, or, if
/// the lock took the hold back first, its <see langword="await"/> throws an <see cref="InvalidOperationException"/>. A timer that throws when
/// it is disposed is ignored in the same way.</para>
/// <para>
/// These guarantees cover a wait that the lock completes. If a wait is completed in the moment between the awaiting code's check for completion
/// and the registration of its continuation, the continuation is scheduled by the awaiting code's own thread, and a context that throws then
/// fails as it does with any other awaitable: the exception is rethrown on the thread pool, and a hold that the wait was granted is not
/// released.</para>
/// </remarks>
public sealed class AsyncReadWriteLock
{
    // THE STATE WORD
    //
    // _state holds everything the paths that do not wait need, so that they can enter and leave with one compare-and-swap and no lock:
    //
    //   bits 0-29   the number of read holds (bit 29 is also the limit flag, below)
    //   bit 30      WaitersFlag: a waiter is queued (or is about to be)
    //   bit 31      WriteFlag: the lock is held in write mode
    //   bits 32-63  the write epoch: a number that goes up by one each time the lock is entered in write mode
    //
    // Every change of _state is an interlocked operation, with or without _sync, because paths that hold _sync race with paths that do not.
    //
    // INVARIANTS
    //
    // (S1) WriteFlag is set only while the read count is zero, and the read count goes up only while WriteFlag is clear. Both are a compare-and-swap
    //      from a state that was seen to allow it, so a writer is never in the lock with a reader or with another writer.
    // (S2) The queues, the waiters in them and the sequence counter are only touched under _sync. A waiter is resolved (granted, or failed by a time
    //      limit or a cancellation) only by removing it from its queue under _sync, and whoever removed it is the only one that completes it. So a
    //      waiter gets exactly one outcome: a grant and a failure cannot both happen, whichever order their threads arrive in.
    // (S3) When _sync is released, WaitersFlag is set if and only if a queue is not empty.
    // (S4) When _sync is released, no queued waiter could be granted in the state that GrantLocked last saw. The only changes that can make a grant
    //      possible afterwards are a release and the removal of a waiter. A removal runs GrantLocked itself. A release that leaves the lock in a state
    //      where a waiter could enter always sees WaitersFlag and runs GrantLocked: a waiter sets the flag when it is queued and GrantLocked runs after
    //      that (EnterSlow), and the flag and the count are one word, so a release either came before the flag was set, and that GrantLocked saw it, or
    //      came after, and saw the flag.
    //
    // WHAT RUNS WHERE
    //
    // Nothing outside this class runs under _sync: no continuation, no timer or time provider call and no cancellation registration. Waiters are
    // completed after _sync is released, and their continuations are always queued, never run inline (Waiter).
    //
    // FAILURES
    //
    // Completing a waiter runs code that is not ours: the Dispose of its timer, and the SynchronizationContext or TaskScheduler that is to take its
    // continuation. The thread that completes a waiter is seldom the waiter's own: it is a caller that releases its hold, a caller that enters, a
    // timer's thread or a thread that cancels a token. So:
    //
    // (C1) Completing a waiter never throws (Waiter.Complete, CompleteGranted). Every exception from it goes to OnCompletionFailed, the one place that
    //      decides what becomes of it.
    // (C2) A waiter that was granted the lock and whose continuation could not be scheduled will never release its hold, so the hold is given back
    //      and the next waiters are granted (GiveBack). Giving back and handing out the guard are decided by one exchange (Waiter.TryRevoke), so a
    //      hold is never both given back and released by a guard.
    // (C3) An entering call can therefore fail only for reasons of its own (the time provider, the limit on read holds), and then it leaves no
    //      waiter queued and no hold behind (AbandonWaiter).

    private const long ReadCountMask = 0x3FFF_FFFF;
    private const long ReadLimitFlag = 0x2000_0000;
    private const long WaitersFlag = 0x4000_0000;
    private const long WriteFlag = 0x8000_0000;
    private const long HoldMask = WriteFlag | ReadCountMask;
    private const long LowMask = 0xFFFF_FFFF;
    private const int EpochShift = 32;
    private const long EpochIncrement = 1L << EpochShift;

    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    private static readonly Action<object?, CancellationToken> CancelCallback = static (state, _) => {
        var waiter = (Waiter)state!;
        waiter.Lock.FailWaiter(waiter, timedOut: false);
    };

    private static readonly TimerCallback TimeoutCallback = static state => {
        var waiter = (Waiter)state!;
        waiter.Lock.FailWaiter(waiter, timedOut: true);
    };

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly AsyncReadWriteLockPreference _preference;

    // The bits of _state that send a reader to the slow path. A reader never enters while WriteFlag is set, and must not push the count past the limit.
    // Unless readers are preferred, a queued waiter sends it there too: a reader then waits behind a waiting writer, which only the slow path can tell
    // from waiting readers.
    private readonly long _readFastPathBlockers;

    private long _state;

    // Under _sync (S2). The counts are written under _sync and read without it by the diagnostic properties.
    private WaiterQueue _readWaiters;
    private WaiterQueue _writeWaiters;
    private long _nextSequence;
    private int _waitingReadCount;
    private int _waitingWriteCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncReadWriteLock"/> class that grants the lock in arrival order and measures time limits with
    /// <see cref="TimeProvider.System"/>.
    /// </summary>
    public AsyncReadWriteLock() : this(AsyncReadWriteLockPreference.Fair, TimeProvider.System) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncReadWriteLock"/> class with the specified preference that measures time limits with
    /// <see cref="TimeProvider.System"/>.
    /// </summary>
    /// <param name="preference">Who enters first when readers and writers both want the lock.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preference"/> is not a defined value.</exception>
    public AsyncReadWriteLock(AsyncReadWriteLockPreference preference) : this(preference, TimeProvider.System) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="AsyncReadWriteLock"/> class with the specified preference and time provider.
    /// </summary>
    /// <param name="preference">Who enters first when readers and writers both want the lock.</param>
    /// <param name="timeProvider">The time provider that measures the time limits of the waits.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preference"/> is not a defined value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    public AsyncReadWriteLock(AsyncReadWriteLockPreference preference, TimeProvider timeProvider)
    {
        preference.ThrowIfNotDefined();

        _preference = preference;
        _timeProvider = timeProvider;

        _readFastPathBlockers = preference is AsyncReadWriteLockPreference.Readers
            ? WriteFlag | ReadLimitFlag
            : WriteFlag | ReadLimitFlag | WaitersFlag;
    }

    /// <summary>
    /// Gets who enters first when readers and writers both want the lock.
    /// </summary>
    public AsyncReadWriteLockPreference Preference => _preference;

    /// <summary>
    /// Gets a value indicating whether the lock is held in read mode by one or more callers.
    /// </summary>
    /// <remarks>
    /// The lock has no owner, so this says that someone holds it, not that the current caller does. The value is a snapshot that can be out of date
    /// when it is returned, unless the caller knows that it holds the lock itself.
    /// </remarks>
    public bool IsHeldForRead => (Volatile.Read(ref _state) & ReadCountMask) is not 0;

    /// <summary>
    /// Gets a value indicating whether the lock is held in write mode.
    /// </summary>
    /// <remarks>
    /// The lock has no owner, so this says that someone holds it, not that the current caller does. The value is a snapshot that can be out of date
    /// when it is returned, unless the caller knows that it holds the lock itself.
    /// </remarks>
    public bool IsHeldForWrite => (Volatile.Read(ref _state) & WriteFlag) is not 0;

    /// <summary>
    /// Gets the number of holds on the lock in read mode.
    /// </summary>
    /// <remarks>
    /// The value is a snapshot that can be out of date when it is returned.
    /// </remarks>
    public int CurrentReadCount => (int)(Volatile.Read(ref _state) & ReadCountMask);

    /// <summary>
    /// Gets the number of callers that are waiting to enter the lock in read mode.
    /// </summary>
    /// <remarks>
    /// The value is a snapshot that can be out of date when it is returned, and is read separately from the other properties.
    /// </remarks>
    public int WaitingReadCount => Volatile.Read(ref _waitingReadCount);

    /// <summary>
    /// Gets the number of callers that are waiting to enter the lock in write mode.
    /// </summary>
    /// <remarks>
    /// The value is a snapshot that can be out of date when it is returned, and is read separately from the other properties.
    /// </remarks>
    public int WaitingWriteCount => Volatile.Read(ref _waitingWriteCount);

    #region Read mode

    /// <summary>
    /// Asynchronously enters the lock in read mode and returns a guard that leaves it when it is disposed.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A task that contains a guard that entered the lock. The task has already completed, and nothing is allocated, when the lock is entered
    /// without waiting.</returns>
    /// <remarks>
    /// <para>
    /// The call waits while the lock is held in write mode and, unless the lock prefers readers, while a writer is waiting. The lock is not reentrant,
    /// so entering it again while the caller holds it can wait forever; see <see cref="AsyncReadWriteLockPreference"/>.</para>
    /// <para>
    /// Await the returned task once. For a time limit, use <see cref="TryEnterReadGuardAsync(TimeSpan, CancellationToken)"/>. A limit put on the task
    /// itself, such as with <c>WaitAsync</c>, ends the await but not the wait, and a hold that the lock grants after it is never released.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the lock was entered.</exception>
    /// <exception cref="InvalidOperationException">The lock is already held in read mode by the largest number of holds it can count (2^29).</exception>
    public ValueTask<AsyncReadGuard> EnterReadGuardAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<AsyncReadGuard>(cancellationToken);

        if (TryEnterReadFast())
            return new(new AsyncReadGuard(this, entered: true));

        return EnterReadSlow(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <summary>
    /// Asynchronously tries to enter the lock in read mode within a time limit and returns a guard whose <see cref="AsyncReadGuard.IsEntered"/> property
    /// says whether it did. Disposing the guard leaves the lock, or does nothing if the lock was not entered.
    /// </summary>
    /// <param name="timeout">The time to wait for the lock: zero to not wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait without a limit. At most
    /// <see cref="int.MaxValue"/> milliseconds. A time that is not a whole number of milliseconds is rounded up to the next whole millisecond.</param>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A task that contains the guard. The task has already completed, and nothing is allocated, when the lock is entered without waiting or
    /// <paramref name="timeout"/> is zero.</returns>
    /// <remarks>
    /// <para>
    /// The call waits while the lock is held in write mode and, unless the lock prefers readers, while a writer is waiting. An exception that the time
    /// provider throws when it creates the timer of the time limit is in the returned task. The lock is not reentrant, so entering it again while the
    /// caller holds it can wait until the time limit passes, or forever without one; see <see cref="AsyncReadWriteLockPreference"/>.</para>
    /// <para>
    /// Await the returned task once, and give the time limit to this call. A limit put on the task itself, such as with <c>WaitAsync</c>, ends the
    /// await but not the wait, and a hold that the lock grants after it is never released.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative and is not <see cref="Timeout.InfiniteTimeSpan"/>, or is
    /// more than <see cref="int.MaxValue"/> milliseconds. This is the only exception that is thrown by the call and not by the returned
    /// task.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the lock was entered or the time limit
    /// passed.</exception>
    /// <exception cref="InvalidOperationException">The lock is already held in read mode by the largest number of holds it can count (2^29).</exception>
    public ValueTask<AsyncReadGuard> TryEnterReadGuardAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        CheckTimeout(timeout);

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<AsyncReadGuard>(cancellationToken);

        if (TryEnterReadFast())
            return new(new AsyncReadGuard(this, entered: true));

        return EnterReadSlow(timeout, cancellationToken);
    }

    /// <summary>
    /// Tries to enter the lock in read mode without waiting and returns a guard whose <see cref="AsyncReadGuard.IsEntered"/> property says whether it
    /// did. Disposing the guard leaves the lock, or does nothing if the lock was not entered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock is entered if a call that waits would have entered it without waiting: it is not held in write mode and, unless the lock prefers
    /// readers, no writer is waiting. The call allocates nothing.</para>
    /// <para>
    /// The lock is not reentrant, but this call never waits for it. If the caller holds the lock in write mode, the call returns a guard that did not
    /// enter it. If the caller holds it in read mode, the call can take a second read hold, which its own guard releases.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The lock is already held in read mode by the largest number of holds it can count (2^29).</exception>
    public AsyncReadGuard TryEnterReadGuard()
    {
        if (TryEnterReadFast())
            return new AsyncReadGuard(this, entered: true);

        lock (_sync)
            return new AsyncReadGuard(this, TryEnterLocked(isWriter: false, out _));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnterReadFast()
    {
        long state = Volatile.Read(ref _state);

        while ((state & _readFastPathBlockers) is 0)
        {
            // WriteFlag was clear in the state that is replaced (S1).
            long current = Interlocked.CompareExchange(ref _state, state + 1, state);

            if (current == state)
                return true;

            state = current;
        }

        return false;
    }

    private ValueTask<AsyncReadGuard> EnterReadSlow(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Waiter? waiter;
        bool entered;

        try
        {
            waiter = EnterSlow(isWriter: false, timeout, cancellationToken, out entered, out _);
        }
        catch (Exception ex)
        {
            // The call holds nothing and has left no waiter behind (C3). Like every failure that is not an argument check, it is the task's.
            return ValueTask.FromException<AsyncReadGuard>(ex);
        }

        return waiter is null ? new(new AsyncReadGuard(this, entered)) : new(waiter, waiter.Version);
    }

    /// <summary>
    /// Releases one read hold. Called once by the guard that entered the lock.
    /// </summary>
    internal void ReleaseRead()
    {
        long state = Volatile.Read(ref _state);

        while (true)
        {
            // Only a guard that was disposed twice through a copy gets here without a hold of its own. It can be told from a real release only when no
            // read hold is left at all; otherwise it takes another reader's hold away, which nothing here can see.
            if ((state & ReadCountMask) is 0)
                ThrowNotHeld("read");

            long current = Interlocked.CompareExchange(ref _state, state - 1, state);

            if (current == state)
                break;

            state = current;
        }

        // The last reader out lets a waiting writer in. The flag is tested in the state that was replaced, which is what S4 relies on.
        if ((state & (ReadCountMask | WaitersFlag)) == (1 | WaitersFlag))
            GrantAfterRelease();
    }

    #endregion

    #region Write mode

    /// <summary>
    /// Asynchronously enters the lock in write mode and returns a guard that leaves it when it is disposed.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A task that contains a guard that entered the lock. The task has already completed, and nothing is allocated, when the lock is entered
    /// without waiting.</returns>
    /// <remarks>
    /// <para>
    /// The call waits while the lock is held in either mode and while writers that arrived earlier are waiting. The lock is not reentrant, so entering
    /// it again while the caller holds it can wait forever; see <see cref="AsyncReadWriteLockPreference"/>.</para>
    /// <para>
    /// Await the returned task once. For a time limit, use <see cref="TryEnterWriteGuardAsync(TimeSpan, CancellationToken)"/>. A limit put on the task
    /// itself, such as with <c>WaitAsync</c>, ends the await but not the wait, and a hold that the lock grants after it is never released.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the lock was entered.</exception>
    public ValueTask<AsyncWriteGuard> EnterWriteGuardAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<AsyncWriteGuard>(cancellationToken);

        if (TryEnterWriteFast(out uint epoch))
            return new(new AsyncWriteGuard(this, epoch, entered: true));

        return EnterWriteSlow(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <summary>
    /// Asynchronously tries to enter the lock in write mode within a time limit and returns a guard whose <see cref="AsyncWriteGuard.IsEntered"/>
    /// property says whether it did. Disposing the guard leaves the lock, or does nothing if the lock was not entered.
    /// </summary>
    /// <param name="timeout">The time to wait for the lock: zero to not wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait without a limit. At most
    /// <see cref="int.MaxValue"/> milliseconds. A time that is not a whole number of milliseconds is rounded up to the next whole millisecond.</param>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A task that contains the guard. The task has already completed, and nothing is allocated, when the lock is entered without waiting or
    /// <paramref name="timeout"/> is zero.</returns>
    /// <remarks>
    /// <para>
    /// The call waits while the lock is held in either mode and while writers that arrived earlier are waiting. An exception that the time provider
    /// throws when it creates the timer of the time limit is in the returned task. The lock is not reentrant, so entering it again while the caller
    /// holds it can wait until the time limit passes, or forever without one; see <see cref="AsyncReadWriteLockPreference"/>.</para>
    /// <para>
    /// Await the returned task once, and give the time limit to this call. A limit put on the task itself, such as with <c>WaitAsync</c>, ends the
    /// await but not the wait, and a hold that the lock grants after it is never released.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative and is not <see cref="Timeout.InfiniteTimeSpan"/>, or is
    /// more than <see cref="int.MaxValue"/> milliseconds. This is the only exception that is thrown by the call and not by the returned
    /// task.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the lock was entered or the time limit
    /// passed.</exception>
    public ValueTask<AsyncWriteGuard> TryEnterWriteGuardAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        CheckTimeout(timeout);

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<AsyncWriteGuard>(cancellationToken);

        if (TryEnterWriteFast(out uint epoch))
            return new(new AsyncWriteGuard(this, epoch, entered: true));

        return EnterWriteSlow(timeout, cancellationToken);
    }

    /// <summary>
    /// Tries to enter the lock in write mode without waiting and returns a guard whose <see cref="AsyncWriteGuard.IsEntered"/> property says whether it
    /// did. Disposing the guard leaves the lock, or does nothing if the lock was not entered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock is entered if it is not held in either mode and nothing is waiting for it. The call allocates nothing.</para>
    /// <para>
    /// The lock is not reentrant, but this call never waits for it: if the caller already holds the lock in either mode, the call returns a guard that
    /// did not enter it.</para>
    /// </remarks>
    public AsyncWriteGuard TryEnterWriteGuard()
    {
        if (TryEnterWriteFast(out uint epoch))
            return new AsyncWriteGuard(this, epoch, entered: true);

        lock (_sync)
        {
            bool entered = TryEnterLocked(isWriter: true, out epoch);
            return new AsyncWriteGuard(this, epoch, entered);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryEnterWriteFast(out uint epoch)
    {
        long state = Volatile.Read(ref _state);

        // Free and nothing queued: no holds and no flags, whatever the epoch is.
        if ((state & LowMask) is 0)
        {
            long entered = (state | WriteFlag) + EpochIncrement;

            if (Interlocked.CompareExchange(ref _state, entered, state) == state)
            {
                epoch = GetEpoch(entered);
                return true;
            }
        }

        // The slow path decides, and retries if the state only changed under the compare-and-swap.
        epoch = 0;
        return false;
    }

    private ValueTask<AsyncWriteGuard> EnterWriteSlow(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Waiter? waiter;
        bool entered;
        uint epoch;

        try
        {
            waiter = EnterSlow(isWriter: true, timeout, cancellationToken, out entered, out epoch);
        }
        catch (Exception ex)
        {
            // The call holds nothing and has left no waiter behind (C3). Like every failure that is not an argument check, it is the task's.
            return ValueTask.FromException<AsyncWriteGuard>(ex);
        }

        return waiter is null ? new(new AsyncWriteGuard(this, epoch, entered)) : new(waiter, waiter.Version);
    }

    /// <summary>
    /// Releases the write hold that was entered with the specified epoch. Called once by the guard that entered the lock.
    /// </summary>
    /// <remarks>
    /// The epoch makes the release exact. The state holds the epoch of the current write hold, and the compare-and-swap that clears WriteFlag only
    /// succeeds from a state with this guard's epoch, so it succeeds once per hold: a copy of a guard that is disposed after the original finds the
    /// flag clear or another hold's epoch, and throws instead of releasing a hold that is not its own. The epoch wraps after 2^32 write holds, so a
    /// stale copy would have to be disposed exactly then, while the lock is held in write mode, to go unnoticed.
    /// </remarks>
    internal void ReleaseWrite(uint epoch)
    {
        long held = ((long)epoch << EpochShift) | WriteFlag;

        // Held by this guard and nothing queued: nobody to grant the lock to.
        if (Interlocked.CompareExchange(ref _state, held & ~WriteFlag, held) == held)
            return;

        GrantList granted = default;

        lock (_sync)
        {
            if (!TryClearWriteFlagLocked(epoch))
                ThrowNotHeld("write");

            // The flag is cleared and the waiters are granted under one hold of _sync, so no caller on the slow path sees the lock free with waiters
            // that this release is about to grant.
            GrantLocked(ref granted);
            UpdateWaitersFlagLocked();
        }

        CompleteGranted(granted);
    }

    /// <summary>
    /// Clears <see cref="WriteFlag"/> if the lock is held in write mode by the hold with the specified epoch. Called under <see cref="_sync"/>.
    /// </summary>
    private bool TryClearWriteFlagLocked(uint epoch)
    {
        Debug.Assert(Monitor.IsEntered(_sync), "The lock's internal state is not locked.");

        long state = Volatile.Read(ref _state);

        while (true)
        {
            if ((state & WriteFlag) is 0 || GetEpoch(state) != epoch)
                return false;

            // Only WaitersFlag can change while WriteFlag is set, so this is retried at most a few times.
            long current = Interlocked.CompareExchange(ref _state, state & ~WriteFlag, state);

            if (current == state)
                return true;

            state = current;
        }
    }

    #endregion

    #region Slow path

    /// <summary>
    /// Enters the lock under <see cref="_sync"/> if the rules allow it now, and otherwise queues a waiter unless the time limit is zero.
    /// </summary>
    /// <returns>The waiter that the caller's task is built on, or <see langword="null"/> if the call ended without waiting, in which case
    /// <paramref name="entered"/> says how.</returns>
    [return: Borrowed]
    private Waiter? EnterSlow(bool isWriter, TimeSpan timeout, CancellationToken cancellationToken, out bool entered, out uint epoch)
    {
        Waiter waiter;
        GrantList granted = default;
        bool queued;

        lock (_sync)
        {
            if (TryEnterLocked(isWriter, out epoch))
            {
                entered = true;
                return null;
            }

            entered = false;

            if (timeout == TimeSpan.Zero)
                return null;

            waiter = new Waiter(this, isWriter, _nextSequence++, cancellationToken);
            EnqueueLocked(waiter);

            // The check above was made before WaitersFlag was set, so a release since then may not have seen the flag. Now that it is set, look again
            // (S4). This can grant this waiter, or the waiters ahead of it if a reader's release is on its way here.
            GrantLocked(ref granted);
            UpdateWaitersFlagLocked();
            queued = waiter.IsQueued;
        }

        // This completes waiters of other callers on this caller's thread. It does not throw (C1): an exception here would leave this caller's
        // waiter queued with no task to reach it, and the lock would later be granted to nobody.
        CompleteGranted(granted);

        // A hold that was given back just now can have been granted to this waiter since it was seen queued. ArmWaiter looks again.
        if (queued)
            ArmWaiter(waiter, timeout, cancellationToken);

        return waiter;
    }

    /// <summary>
    /// Enters the lock if the rules of the preference allow a new arrival to enter now. Called under <see cref="_sync"/>.
    /// </summary>
    private bool TryEnterLocked(bool isWriter, out uint epoch)
    {
        Debug.Assert(Monitor.IsEntered(_sync), "The lock's internal state is not locked.");

        long state = Volatile.Read(ref _state);
        epoch = 0;

        if (isWriter)
        {
            // A writer never passes a waiter. While the lock is free and something is queued, the release that freed it is on its way to grant the
            // lock to the head of the queue (S4).
            if (_writeWaiters.Head is not null || _readWaiters.Head is not null)
                return false;

            while ((state & HoldMask) is 0)
            {
                long entered = (state | WriteFlag) + EpochIncrement;
                long current = Interlocked.CompareExchange(ref _state, entered, state);

                if (current == state)
                {
                    epoch = GetEpoch(entered);
                    return true;
                }

                state = current;
            }

            return false;
        }

        // A reader passes waiting writers only if readers are preferred. When no writer waits and no writer holds the lock, no reader is queued
        // either (S4), so entering passes nobody.
        if (_preference is not AsyncReadWriteLockPreference.Readers && _writeWaiters.Head is not null)
            return false;

        while ((state & WriteFlag) is 0)
        {
            if ((state & ReadLimitFlag) is not 0)
                ThrowTooManyReaders();

            long current = Interlocked.CompareExchange(ref _state, state + 1, state);

            if (current == state)
                return true;

            state = current;
        }

        return false;
    }

    /// <summary>
    /// Grants the lock to every queued waiter that the rules of the preference allow to enter now, and removes those waiters from their queues. Called
    /// under <see cref="_sync"/>; the granted waiters are completed after it is released.
    /// </summary>
    /// <remarks>
    /// This is the only place where a waiter is granted the lock, and it runs after every change that can make a grant possible (S4). The lock is
    /// entered for the waiter here, by the same compare-and-swap a caller would make, and the waiter is removed from its queue under the same hold of
    /// <see cref="_sync"/>, so a time limit or a cancellation that arrives afterwards finds it gone and does nothing (S2).
    /// </remarks>
    private void GrantLocked(ref GrantList granted)
    {
        Debug.Assert(Monitor.IsEntered(_sync), "The lock's internal state is not locked.");

        long state = Volatile.Read(ref _state);

        while ((state & WriteFlag) is 0)
        {
            var writer = _writeWaiters.Head;
            var reader = _readWaiters.Head;

            // Who is next in line. A waiting writer is always the one that arrived first among the writers.
            bool writerIsNext = _preference switch {
                AsyncReadWriteLockPreference.Readers => reader is null,
                AsyncReadWriteLockPreference.Writers => writer is not null,
                _ => writer is not null && (reader is null || writer.Sequence < reader.Sequence),
            };

            Waiter next;
            long entered;

            if (writerIsNext)
            {
                // A writer that is next waits for the readers to leave, and everything behind it waits with it. The last reader out comes back here.
                if (writer is null || (state & ReadCountMask) is not 0)
                    return;

                next = writer;
                entered = (state | WriteFlag) + EpochIncrement;
            }
            else
            {
                if (reader is null)
                    return;

                // The limit on the read count is not checked: passing it here would take more queued waiters than memory can hold.
                next = reader;
                entered = state + 1;
            }

            long current = Interlocked.CompareExchange(ref _state, entered, state);

            if (current != state)
            {
                // A reader entered or left on the fast path. Decide again from the new state.
                state = current;
                continue;
            }

            DequeueLocked(next);
            next.IsGranted = true;
            next.Epoch = GetEpoch(entered);
            granted.Add(next);

            // After a writer the loop ends on WriteFlag. After a reader it goes on to the readers behind it.
            state = entered;
        }
    }

    /// <summary>
    /// Runs the grant for a release that was made without <see cref="_sync"/> and saw <see cref="WaitersFlag"/>.
    /// </summary>
    private void GrantAfterRelease()
    {
        GrantList granted = default;

        lock (_sync)
        {
            GrantLocked(ref granted);
            UpdateWaitersFlagLocked();
        }

        CompleteGranted(granted);
    }

    /// <summary>
    /// Ends a wait whose time limit passed or whose token was canceled, unless the waiter has already been resolved.
    /// </summary>
    private void FailWaiter(Waiter waiter, bool timedOut)
    {
        GrantList granted = default;

        lock (_sync)
        {
            // Already granted, or already failed by the other of the two: whoever removed it completes it (S2).
            if (!waiter.IsQueued)
                return;

            DequeueLocked(waiter);

            // A writer that leaves the queue may have been all that kept the readers behind it out.
            GrantLocked(ref granted);
            UpdateWaitersFlagLocked();
        }

        // Neither throws (C1). This runs on a timer's thread or inside CancellationTokenSource.Cancel, where an exception would end the process or
        // reach whoever canceled the token.
        waiter.Complete(timedOut ? WaitOutcome.TimedOut : WaitOutcome.Canceled);
        CompleteGranted(granted);
    }

    /// <summary>
    /// Gives a queued waiter its cancellation registration and its timer.
    /// </summary>
    /// <remarks>
    /// This runs after <see cref="_sync"/> is released, because registering can run the cancellation callback at once and the time provider is the
    /// caller's code. The waiter can therefore be resolved at any moment here, by a release, by the callback of the registration just made or by the
    /// timer just created. The two are handed over under <see cref="_sync"/>: if the waiter is still queued they are stored in it and whoever resolves
    /// it releases them; if not, nobody else knows them and they are released here. The method throws only what registering or the time provider
    /// throws, and then the waiter has been taken back (C3).
    /// </remarks>
    private void ArmWaiter(Waiter waiter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        CancellationTokenRegistration registration = default;
        ITimer? timer = null;

        try
        {
            if (cancellationToken.CanBeCanceled)
                registration = cancellationToken.UnsafeRegister(CancelCallback, waiter);

            if (timeout != Timeout.InfiniteTimeSpan)
                timer = _timeProvider.CreateTimer(TimeoutCallback, waiter, RoundUpToMilliseconds(timeout), Timeout.InfiniteTimeSpan);
        }
        catch
        {
            // The wait cannot be set up, so the caller gets this exception and never sees the waiter's task. The waiter is taken back first, so
            // that nothing can keep it from being taken back.
            AbandonWaiter(waiter);
            ReleaseWaitResources(registration, timer: null);
            throw;
        }

        lock (_sync)
        {
            if (waiter.IsQueued)
            {
                waiter.Registration = registration;
                waiter.Timer = timer;
                return;
            }
        }

        // The wait has its outcome already, which can be the lock. A timer that throws from its Dispose must not turn that into an exception for the
        // caller, who would then hold the lock with no guard to release it.
        ReleaseWaitResources(registration, timer);
    }

    /// <summary>
    /// Takes back a waiter whose task will never reach its caller, so that the lock is not left held for nobody.
    /// </summary>
    private void AbandonWaiter(Waiter waiter)
    {
        GrantList granted = default;
        bool wasGranted;

        lock (_sync)
        {
            wasGranted = waiter.IsGranted;

            if (waiter.IsQueued)
            {
                DequeueLocked(waiter);
                GrantLocked(ref granted);
                UpdateWaitersFlagLocked();
            }
        }

        // A release granted it the lock before it could be removed. Nobody will dispose a guard for that hold, so it is given back here.
        if (wasGranted)
            GiveBack(waiter, ref granted);

        CompleteGranted(granted);
    }

    /// <summary>
    /// Releases the hold of a waiter that was granted the lock and for which no guard can ever reach a caller, and grants the lock to whoever is
    /// next. The waiters it grants are added to <paramref name="granted"/> for the caller to complete.
    /// </summary>
    /// <remarks>
    /// Only the caller of this method can release the hold: either the waiter's task was never returned (<see cref="AbandonWaiter"/>), or its result
    /// was revoked before anyone took it (<see cref="Waiter.TryRevoke"/>). So the hold is still there, and a write hold still has the waiter's epoch.
    /// </remarks>
    private void GiveBack(Waiter waiter, ref GrantList granted)
    {
        lock (_sync)
        {
            if (waiter.IsWriter)
            {
                if (!TryClearWriteFlagLocked(waiter.Epoch))
                    Debug.Fail("The write hold of a waiter whose hold is given back is not there.");
            }
            else
            {
                long state = Volatile.Read(ref _state);

                while (true)
                {
                    Debug.Assert((state & ReadCountMask) is not 0, "The read hold of a waiter whose hold is given back is not there.");

                    // Readers enter and leave on the fast path meanwhile.
                    long current = Interlocked.CompareExchange(ref _state, state - 1, state);

                    if (current == state)
                        break;

                    state = current;
                }
            }

            GrantLocked(ref granted);
            UpdateWaitersFlagLocked();
        }
    }

    private void EnqueueLocked([Kept] Waiter waiter)
    {
        if (waiter.IsWriter)
        {
            _writeWaiters.Enqueue(waiter);
            Volatile.Write(ref _waitingWriteCount, _waitingWriteCount + 1);
        }
        else
        {
            _readWaiters.Enqueue(waiter);
            Volatile.Write(ref _waitingReadCount, _waitingReadCount + 1);
        }

        waiter.IsQueued = true;
        Interlocked.Or(ref _state, WaitersFlag);
    }

    private void DequeueLocked(Waiter waiter)
    {
        if (waiter.IsWriter)
        {
            _writeWaiters.Remove(waiter);
            Volatile.Write(ref _waitingWriteCount, _waitingWriteCount - 1);
        }
        else
        {
            _readWaiters.Remove(waiter);
            Volatile.Write(ref _waitingReadCount, _waitingReadCount - 1);
        }

        waiter.IsQueued = false;
    }

    /// <summary>
    /// Clears <see cref="WaitersFlag"/> if nothing is queued (S3). Called under <see cref="_sync"/> after every change to the queues.
    /// </summary>
    private void UpdateWaitersFlagLocked()
    {
        if (_readWaiters.Head is null && _writeWaiters.Head is null)
            Interlocked.And(ref _state, ~WaitersFlag);
    }

    /// <summary>
    /// Completes waiters that were granted the lock. Called after <see cref="_sync"/> is released. Never throws (C1).
    /// </summary>
    /// <remarks>
    /// Each of the waiters holds the lock already. One whose continuation could not be scheduled will never run the code that releases its hold, so
    /// its hold is given back (C2). That can grant the lock to further waiters, who are completed in turn: in a loop and not by recursion, so that a
    /// long row of waiters that cannot be resumed does not use up the stack. The loop ends when a grant goes to a waiter that can be resumed or to
    /// nobody, so it goes on, on the thread that happens to run it, for as long as waiters that cannot be resumed keep arriving.
    /// </remarks>
    private void CompleteGranted(GrantList granted)
    {
        var waiter = granted.Head;

        while (waiter is not null)
        {
            GrantList regranted = default;

            do
            {
                var next = waiter.Next;
                waiter.Next = null;

                if (!waiter.Complete(WaitOutcome.Granted))
                    GiveBack(waiter, ref regranted);

                waiter = next;
            }
            while (waiter is not null);

            waiter = regranted.Head;
        }
    }

    /// <summary>
    /// Removes the cancellation registration and the timer of a wait that has its outcome. Never throws (C1).
    /// </summary>
    /// <remarks>
    /// Neither call waits for a callback that is running: such a callback finds the waiter gone from its queue and does nothing. The same goes for a
    /// timer that throws from its <see cref="IDisposable.Dispose"/> and fires later after all.
    /// </remarks>
    private static void ReleaseWaitResources(CancellationTokenRegistration registration, ITimer? timer)
    {
        try
        {
            registration.Unregister();
        }
        catch (Exception ex)
        {
            OnCompletionFailed(ex);
        }

        try
        {
            timer?.Dispose();
        }
        catch (Exception ex)
        {
            OnCompletionFailed(ex);
        }
    }

    /// <summary>
    /// Decides what becomes of an exception that was thrown while a wait was completed: by the <see cref="IDisposable.Dispose"/> of its timer, or by
    /// the <see cref="SynchronizationContext"/> or <see cref="TaskScheduler"/> that was to take its continuation.
    /// </summary>
    /// <remarks>
    /// Every such exception comes here and goes nowhere else, so this method is the policy, and the policy is that the exception is dropped. The
    /// thread it was thrown on belongs to another caller, to a timer or to whoever canceled a token. None of them can do anything about a context
    /// that is not theirs, and on a timer's thread an exception ends the process. The waiter cannot be told either: telling it means running its
    /// continuation, which is what failed. The callers of this method keep the lock consistent; that does not depend on the exception going anywhere.
    /// </remarks>
    private static void OnCompletionFailed(Exception exception)
    {
        _ = exception;
    }

    #endregion

    private static uint GetEpoch(long state) => (uint)(state >> EpochShift);

    private static void CheckTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The value is negative and is not Timeout.InfiniteTimeSpan.");

        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, MaxTimeout, nameof(timeout));
    }

    // Timers truncate to whole milliseconds, which would end a wait before its time limit.
    private static TimeSpan RoundUpToMilliseconds(TimeSpan time)
    {
        long partialTicks = time.Ticks % TimeSpan.TicksPerMillisecond;
        return partialTicks is 0 ? time : TimeSpan.FromTicks(time.Ticks - partialTicks + TimeSpan.TicksPerMillisecond);
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowNotHeld(string mode) => throw new SynchronizationLockException(
        $"The {mode} lock is not held by this guard. This commonly occurs when a guard is copied and both the copy and the original are disposed.");

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowTooManyReaders() => throw new InvalidOperationException("The lock is held in read mode by too many callers.");

    private enum WaitOutcome
    {
        Granted,
        TimedOut,
        Canceled,
    }

    /// <summary>
    /// The waiters that one pass of <see cref="GrantLocked"/> granted, in the order they were granted. They are out of their queues, so the list
    /// reuses their queue link.
    /// </summary>
    private struct GrantList
    {
        private Waiter? _tail;

        public Waiter? Head { get; private set; }

        public void Add([Kept] Waiter waiter)
        {
            if (_tail is null)
                Head = waiter;
            else
                _tail.Next = waiter;

            _tail = waiter;
        }
    }

    /// <summary>
    /// A queue of waiters in arrival order, from which a waiter anywhere in it can be removed. Used under <see cref="_sync"/>.
    /// </summary>
    private struct WaiterQueue
    {
        private Waiter? _tail;

        public Waiter? Head { get; private set; }

        public void Enqueue([Kept] Waiter waiter)
        {
            waiter.Previous = _tail;

            if (_tail is null)
                Head = waiter;
            else
                _tail.Next = waiter;

            _tail = waiter;
        }

        public void Remove(Waiter waiter)
        {
            if (waiter.Previous is null)
                Head = waiter.Next;
            else
                waiter.Previous.Next = waiter.Next;

            if (waiter.Next is null)
                _tail = waiter.Previous;
            else
                waiter.Next.Previous = waiter.Previous;

            waiter.Previous = null;
            waiter.Next = null;
        }
    }

    /// <summary>
    /// One call that is waiting for the lock, and the source of the task that the call returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A waiter is allocated for each wait and never reused. A timer callback or a cancellation callback of a finished wait can still be on its way,
    /// and with one object per wait it finds a waiter that is no longer queued and does nothing; with reuse it would have to be told apart from the
    /// callbacks of the next wait. A wait is already the slow path, so the allocation is not what it costs.</para>
    /// <para>
    /// Continuations run asynchronously (<see cref="ManualResetValueTaskSourceCore{TResult}.RunContinuationsAsynchronously"/>): completing a waiter
    /// queues its continuation to the thread pool, or posts it to the context the <see langword="await"/> captured, and never runs it on the thread
    /// that released the lock, canceled the token or ran the timer. So when completing a waiter throws, its continuation was not scheduled, unless
    /// the context ran it by itself before it threw.</para>
    /// </remarks>
    private sealed class Waiter : IValueTaskSource<AsyncReadGuard>, IValueTaskSource<AsyncWriteGuard>
    {
        private readonly CancellationToken _cancellationToken;
        private ManualResetValueTaskSourceCore<bool> _source;
        private int _resultTaken;

        public Waiter(AsyncReadWriteLock @lock, bool isWriter, long sequence, CancellationToken cancellationToken)
        {
            Lock = @lock;
            IsWriter = isWriter;
            Sequence = sequence;
            _cancellationToken = cancellationToken;
            _source.RunContinuationsAsynchronously = true;
        }

        public AsyncReadWriteLock Lock { get; }

        public bool IsWriter { get; }

        /// <summary>
        /// Gets the position of the waiter in arrival order among the readers and the writers together.
        /// </summary>
        public long Sequence { get; }

        public short Version => _source.Version;

        // The fields below are read and written under the lock's _sync while the waiter is queued. Once it has been removed from its queue they
        // belong to the thread that removed it, which completes it.

        public Waiter? Previous { get; set; }

        public Waiter? Next { get; set; }

        public bool IsQueued { get; set; }

        public bool IsGranted { get; set; }

        /// <summary>
        /// Gets or sets the epoch of the write hold that the waiter was granted.
        /// </summary>
        public uint Epoch { get; set; }

        public CancellationTokenRegistration Registration { get; set; }

        public ITimer? Timer { get; set; }

        /// <summary>
        /// Completes the task of the wait. Called once, after the lock's <c>_sync</c> is released, by the thread that removed the waiter from its
        /// queue. Never throws (C1).
        /// </summary>
        /// <returns><see langword="false"/> if the wait was granted the lock and no guard for that hold can reach a caller any more, so that the
        /// caller of this method has to give the hold back (C2); otherwise <see langword="true"/>.</returns>
        public bool Complete(WaitOutcome outcome)
        {
            ReleaseWaitResources(Registration, Timer);

            try
            {
                if (outcome is WaitOutcome.Canceled)
                    _source.SetException(ExceptionDispatchInfo.SetCurrentStackTrace(new OperationCanceledException(_cancellationToken)));
                else
                    _source.SetResult(outcome is WaitOutcome.Granted);

                return true;
            }
            catch (Exception ex)
            {
                // The task has its result, and scheduling its continuation threw: the SynchronizationContext or TaskScheduler that the continuation
                // was captured on did not take it. The code that awaits the lock will not run.
                OnCompletionFailed(ex);

                // Unless the guard is out already: a context can run the continuation by itself and then throw, and a caller can poll the task
                // instead of awaiting it. Then the hold is theirs to release. Otherwise nobody will release it, and it must be given back.
                return outcome is not WaitOutcome.Granted || !TryRevoke();
            }
        }

        /// <summary>
        /// Makes the result of the wait unavailable, so that no guard can be made for the hold that the wait was granted.
        /// </summary>
        /// <returns><see langword="false"/> if the result has already been taken, in which case a guard for the hold exists.</returns>
        /// <remarks>
        /// Revoking the result and taking it (<see cref="TakeResult"/>) are the same exchange, so exactly one of the two succeeds: a hold is given
        /// back by the lock or released by a guard, never both and never neither.
        /// </remarks>
        public bool TryRevoke() => Interlocked.Exchange(ref _resultTaken, 1) is 0;

        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _source.OnCompleted(continuation, state, token, flags);

        AsyncReadGuard IValueTaskSource<AsyncReadGuard>.GetResult(short token) => new(Lock, TakeResult(token));

        AsyncWriteGuard IValueTaskSource<AsyncWriteGuard>.GetResult(short token)
        {
            // The epoch is only there once the wait has completed, which taking the result checks.
            bool entered = TakeResult(token);
            return new(Lock, Epoch, entered);
        }

        private bool TakeResult(short token)
        {
            // Throws if the wait has not completed or was canceled.
            bool entered = _source.GetResult(token);

            // A task that is awaited twice must not hand out two guards for one hold, and a task whose hold was given back must not hand out any.
            if (Interlocked.Exchange(ref _resultTaken, 1) is not 0)
            {
                throw new InvalidOperationException(
                    "The result of the task is not available. It has already been taken (a ValueTask can only be awaited once), or the continuation " +
                    "of the wait could not be scheduled and the lock took the hold back.");
            }

            return entered;
        }
    }
}
