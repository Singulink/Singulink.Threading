using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Singulink.Threading.Utilities;

namespace Singulink.Threading;

/// <summary>
/// Represents a disposable upgradeable read guard over a <see cref="ReadWriteLock"/>. The guard has read
/// access to the protected resource and can temporarily upgrade to scoped write access (see
/// <see cref="EnterUpgradedWriteGuard"/>) or permanently downgrade to a plain read lock (see
/// <see cref="DowngradeToReadLock"/>).
/// </summary>
/// <remarks>
/// <para>Guards returned from <c>TryEnter</c> methods may not have entered the lock — check
/// <see cref="IsEntered"/> after acquiring. Disposing a guard that did not enter the lock is a safe no-op,
/// so guards can always be assigned directly to a <see langword="using"/> declaration.</para>
/// <para>Guards are thread-affine: all operations on an entered guard must be performed on the thread that
/// entered the lock, which is enforced with an <see cref="InvalidOperationException"/> when violated.</para>
/// </remarks>
[NonCopyable]
public struct UpgradeableReadGuard : IDisposable
{
    private ReaderWriterLockSlim? _rwLock;
    private bool _isDowngraded;
    private bool _isDisposed;
    private readonly int _ownerThreadId;
    private readonly bool _isEntered;
    private readonly bool _isInitialized;

    /// <summary>
    /// Gets a value indicating whether the instance is in its default, uninitialized state.
    /// </summary>
    public readonly bool IsDefault => !_isInitialized;

    /// <summary>
    /// Gets a value indicating whether the guard entered the lock. Always <see langword="true"/> for guards
    /// obtained from <see cref="ReadWriteLock.EnterUpgradeableReadGuard"/>; guards obtained from
    /// <c>TryEnter</c> methods did not enter the lock if the timeout expired, in which case the protected
    /// resource must not be accessed.
    /// </summary>
    public readonly bool IsEntered
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            return _isEntered;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the guard is currently upgraded to write access (i.e. a guard
    /// returned from <see cref="EnterUpgradedWriteGuard"/> or a successful
    /// <see cref="TryEnterUpgradedWriteGuard(int)"/> has not been disposed yet).
    /// </summary>
    public readonly bool IsUpgraded
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            return !_isDisposed && _isEntered && !_isDowngraded && _rwLock!.IsWriteLockHeld;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the guard has been downgraded to a plain read lock via
    /// <see cref="DowngradeToReadLock"/>.
    /// </summary>
    public readonly bool IsDowngraded
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            return _isDowngraded;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the object has been disposed.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_rwLock))]
    public readonly bool IsDisposed
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            return _isDisposed;
        }
    }

    internal UpgradeableReadGuard(ReaderWriterLockSlim rwLock, bool entered)
    {
        _rwLock = rwLock;
        _isDowngraded = false;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _isEntered = entered;
        _isInitialized = true;
    }

    /// <summary>
    /// Upgrades to write access, returning a write guard whose disposal exits write mode and returns this
    /// thread to upgradeable read mode. The guard can be upgraded again after the returned write guard is
    /// disposed, so scoped write access can be repeated within one upgradeable read session.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The guard has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The guard did not enter the lock, has been downgraded to
    /// a read lock, is already upgraded, or the operation was attempted on a different thread than the one
    /// that entered the lock.</exception>
    public WriteGuard EnterUpgradedWriteGuard()
    {
        EnsureCanUpgrade();
        _rwLock.EnterWriteLock();
        return new WriteGuard(_rwLock, entered: true);
    }

    /// <summary>
    /// Tries to upgrade to write access within the given timeout, returning a write guard whose
    /// <see cref="WriteGuard.IsEntered"/> property indicates whether write mode was entered. Disposing the
    /// returned guard exits write mode (returning this thread to upgradeable read mode), or is a no-op if
    /// write mode was not entered.
    /// </summary>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or
    /// <see cref="Timeout.Infinite"/> (<c>-1</c>) to wait indefinitely.</param>
    /// <exception cref="ObjectDisposedException">The guard has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The guard did not enter the lock, has been downgraded to
    /// a read lock, is already upgraded, or the operation was attempted on a different thread than the one
    /// that entered the lock.</exception>
    public WriteGuard TryEnterUpgradedWriteGuard(int millisecondsTimeout)
    {
        EnsureCanUpgrade();
        return new WriteGuard(_rwLock, _rwLock.TryEnterWriteLock(millisecondsTimeout));
    }

    /// <summary>
    /// Tries to upgrade to write access within the given timeout, returning a write guard whose
    /// <see cref="WriteGuard.IsEntered"/> property indicates whether write mode was entered. Disposing the
    /// returned guard exits write mode (returning this thread to upgradeable read mode), or is a no-op if
    /// write mode was not entered.
    /// </summary>
    /// <param name="timeout">The amount of time to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait
    /// indefinitely.</param>
    /// <exception cref="ObjectDisposedException">The guard has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The guard did not enter the lock, has been downgraded to
    /// a read lock, is already upgraded, or the operation was attempted on a different thread than the one
    /// that entered the lock.</exception>
    public WriteGuard TryEnterUpgradedWriteGuard(TimeSpan timeout)
    {
        EnsureCanUpgrade();
        return new WriteGuard(_rwLock, _rwLock.TryEnterWriteLock(timeout));
    }

    /// <summary>
    /// Upgrades to write access. The write lock is released when this guard is disposed.
    /// </summary>
    /// <remarks>
    /// Retained for binary compatibility with assemblies compiled against v2.x. Equivalent to calling
    /// <see cref="EnterUpgradedWriteGuard"/> and abandoning the returned guard — disposal of this guard
    /// releases the write lock.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The guard has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The guard did not enter the lock, has been downgraded to
    /// a read lock, is already upgraded, or the operation was attempted on a different thread than the one
    /// that entered the lock.</exception>
    [Obsolete("Use EnterUpgradedWriteGuard() instead, which returns a write guard that releases the write lock " +
        "(returning the thread to upgradeable read mode) upon disposal. This member is retained for binary " +
        "compatibility with v2.x and will be removed in a future version.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public void UpgradeToWriteGuard() => _ = EnterUpgradedWriteGuard();

    /// <summary>
    /// Downgrades the guard to a plain read lock, allowing another thread to enter upgradeable read (or
    /// write) mode while this thread retains read access. The downgrade is gapless — no other thread can
    /// enter write mode during the transition — and never blocks. Downgrading is one-way: the guard cannot
    /// be upgraded afterward, and disposal releases the read lock.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The guard has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The guard did not enter the lock, is currently upgraded
    /// (dispose the write guard first), has already been downgraded, or the operation was attempted on a
    /// different thread than the one that entered the lock.</exception>
    public void DowngradeToReadLock()
    {
        Throw.NotInitializedIf(!_isInitialized);
        ObjectDisposedException.ThrowIf(IsDisposed, nameof(UpgradeableReadGuard));

        if (!_isEntered)
            throw new InvalidOperationException("The guard did not enter the lock.");

        Throw.WrongGuardThreadIf(Environment.CurrentManagedThreadId != _ownerThreadId);

        if (_isDowngraded)
            throw new InvalidOperationException("The guard has already been downgraded to a read lock.");

        if (_rwLock.IsWriteLockHeld)
            throw new InvalidOperationException("The guard cannot be downgraded while upgraded to a write lock. Dispose the write guard first.");

        // Entering read mode from upgradeable read mode never blocks, so the downgrade is gapless.
        _rwLock.EnterReadLock();
        _rwLock.ExitUpgradeableReadLock();
        _isDowngraded = true;
    }

    /// <summary>
    /// Releases whatever the guard currently holds: the upgradeable read lock (and the write lock first, if
    /// an upgraded write guard was abandoned without being disposed), or the read lock if the guard was
    /// downgraded. No-op if the guard did not enter the lock.
    /// </summary>
    public void Dispose()
    {
        Throw.NotInitializedIf(!_isInitialized);

        if (IsDisposed)
            return;

        Throw.WrongGuardThreadIf(_isEntered && Environment.CurrentManagedThreadId != _ownerThreadId);

        _isDisposed = true;

        if (_isEntered)
        {
            if (_isDowngraded)
            {
                _rwLock.ExitReadLock();
            }
            else
            {
                // An upgraded write guard that was abandoned without being disposed still holds write mode;
                // release it so the guard's disposal always fully releases the session.
                if (_rwLock.IsWriteLockHeld)
                    _rwLock.ExitWriteLock();

                _rwLock.ExitUpgradeableReadLock();
            }
        }

        _rwLock = null;
    }

    [MemberNotNull(nameof(_rwLock))]
    private readonly void EnsureCanUpgrade()
    {
        Throw.NotInitializedIf(!_isInitialized);
        ObjectDisposedException.ThrowIf(IsDisposed, nameof(UpgradeableReadGuard));

        if (!_isEntered)
            throw new InvalidOperationException("The guard did not enter the lock.");

        Throw.WrongGuardThreadIf(Environment.CurrentManagedThreadId != _ownerThreadId);

        if (_isDowngraded)
            throw new InvalidOperationException("The guard has been downgraded to a read lock and cannot be upgraded.");

        if (_rwLock.IsWriteLockHeld)
            throw new InvalidOperationException("The guard is already upgraded to a write lock.");
    }
}
