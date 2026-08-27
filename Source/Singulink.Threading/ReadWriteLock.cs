namespace Singulink.Threading;

/// <summary>
/// A reader/writer lock that manages access to a resource via disposable guards, allowing multiple threads
/// for reading or exclusive access for writing.
/// </summary>
/// <remarks>
/// <para>Wraps a <see cref="ReaderWriterLockSlim"/> with <see cref="LockRecursionPolicy.NoRecursion"/>,
/// exposing only guard-based entry so lock modes cannot be manipulated behind the guards' backs and
/// recursive entry (which has no guard representation) is excluded by construction. Guard geometry follows
/// the underlying lock: any number of threads can hold read guards, one thread can hold an upgradeable read
/// guard (which can temporarily upgrade to scoped write access or permanently downgrade to a read lock),
/// and write guards are exclusive.</para>
/// <para>The lock has managed thread affinity: each guard must be entered and disposed on the same
/// thread, and guards must not be used across <see langword="await"/> boundaries.</para>
/// <para>Naming convention: <c>Enter…Guard</c> methods return a guard that entered the lock;
/// <c>TryEnter…Guard</c> methods return a guard that may not have entered the lock (check
/// <c>IsEntered</c> — disposal of a non-entered guard is a safe no-op, so results can be assigned directly
/// to <see langword="using"/> declarations); methods ending in <c>…Lock</c> operate on the current guard in
/// place.</para>
/// </remarks>
public sealed class ReadWriteLock : IDisposable
{
    private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);

    /// <summary>
    /// Gets a value indicating whether the current thread has entered the lock in read mode.
    /// </summary>
    public bool IsReadLockHeld => _rwLock.IsReadLockHeld;

    /// <summary>
    /// Gets a value indicating whether the current thread has entered the lock in upgradeable read mode.
    /// </summary>
    public bool IsUpgradeableReadLockHeld => _rwLock.IsUpgradeableReadLockHeld;

    /// <summary>
    /// Gets a value indicating whether the current thread has entered the lock in write mode.
    /// </summary>
    public bool IsWriteLockHeld => _rwLock.IsWriteLockHeld;

    /// <summary>
    /// Enters the lock in read mode and returns a guard that releases the lock upon disposal.
    /// </summary>
    public ReadGuard EnterReadGuard()
    {
        _rwLock.EnterReadLock();
        return new ReadGuard(_rwLock, entered: true);
    }

    /// <summary>
    /// Tries to enter the lock in read mode within the given timeout, returning a guard whose
    /// <see cref="ReadGuard.IsEntered"/> property indicates whether the lock was entered. Disposal releases
    /// the lock, or is a no-op if the lock was not entered.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or
    /// <see cref="Timeout.Infinite"/> (<c>-1</c>) to wait indefinitely.</param>
    public ReadGuard TryEnterReadGuard(int millisecondsTimeout) =>
        new(_rwLock, _rwLock.TryEnterReadLock(millisecondsTimeout));

    /// <summary>
    /// Tries to enter the lock in read mode within the given timeout, returning a guard whose
    /// <see cref="ReadGuard.IsEntered"/> property indicates whether the lock was entered. Disposal releases
    /// the lock, or is a no-op if the lock was not entered.
    /// </summary>
    /// <param name="timeout">The amount of time to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait
    /// indefinitely.</param>
    public ReadGuard TryEnterReadGuard(TimeSpan timeout) =>
        new(_rwLock, _rwLock.TryEnterReadLock(timeout));

    /// <summary>
    /// Enters the lock in write mode and returns a guard that releases the lock upon disposal.
    /// </summary>
    /// <remarks>
    /// Write locks entered directly cannot be downgraded to read locks; enter the lock in upgradeable read
    /// mode instead if downgrading may be needed.
    /// </remarks>
    public WriteGuard EnterWriteGuard()
    {
        _rwLock.EnterWriteLock();
        return new WriteGuard(_rwLock, entered: true);
    }

    /// <summary>
    /// Tries to enter the lock in write mode within the given timeout, returning a guard whose
    /// <see cref="WriteGuard.IsEntered"/> property indicates whether the lock was entered. Disposal releases
    /// the lock, or is a no-op if the lock was not entered.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or
    /// <see cref="Timeout.Infinite"/> (<c>-1</c>) to wait indefinitely.</param>
    public WriteGuard TryEnterWriteGuard(int millisecondsTimeout) =>
        new(_rwLock, _rwLock.TryEnterWriteLock(millisecondsTimeout));

    /// <summary>
    /// Tries to enter the lock in write mode within the given timeout, returning a guard whose
    /// <see cref="WriteGuard.IsEntered"/> property indicates whether the lock was entered. Disposal releases
    /// the lock, or is a no-op if the lock was not entered.
    /// </summary>
    /// <param name="timeout">The amount of time to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait
    /// indefinitely.</param>
    public WriteGuard TryEnterWriteGuard(TimeSpan timeout) =>
        new(_rwLock, _rwLock.TryEnterWriteLock(timeout));

    /// <summary>
    /// Enters the lock in upgradeable read mode and returns a guard that releases the lock upon disposal.
    /// The guard can temporarily upgrade to scoped write access or permanently downgrade to a plain read
    /// lock. Only one thread can hold the lock in upgradeable read mode at a time.
    /// </summary>
    public UpgradeableReadGuard EnterUpgradeableReadGuard()
    {
        _rwLock.EnterUpgradeableReadLock();
        return new UpgradeableReadGuard(_rwLock, entered: true);
    }

    /// <summary>
    /// Tries to enter the lock in upgradeable read mode within the given timeout, returning a guard whose
    /// <see cref="UpgradeableReadGuard.IsEntered"/> property indicates whether the lock was entered.
    /// Disposal releases the lock, or is a no-op if the lock was not entered.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or
    /// <see cref="Timeout.Infinite"/> (<c>-1</c>) to wait indefinitely.</param>
    public UpgradeableReadGuard TryEnterUpgradeableReadGuard(int millisecondsTimeout) =>
        new(_rwLock, _rwLock.TryEnterUpgradeableReadLock(millisecondsTimeout));

    /// <summary>
    /// Tries to enter the lock in upgradeable read mode within the given timeout, returning a guard whose
    /// <see cref="UpgradeableReadGuard.IsEntered"/> property indicates whether the lock was entered.
    /// Disposal releases the lock, or is a no-op if the lock was not entered.
    /// </summary>
    /// <param name="timeout">The amount of time to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait
    /// indefinitely.</param>
    public UpgradeableReadGuard TryEnterUpgradeableReadGuard(TimeSpan timeout) =>
        new(_rwLock, _rwLock.TryEnterUpgradeableReadLock(timeout));

    /// <summary>
    /// Releases all resources used by the lock. The lock must not be held by (or waited on by) any threads
    /// when it is disposed.
    /// </summary>
    public void Dispose() => _rwLock.Dispose();
}
