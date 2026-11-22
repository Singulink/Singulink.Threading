using Singulink.CodeAnalysis;
using Singulink.Threading.Utilities;

namespace Singulink.Threading;

/// <summary>
/// Represents a hold on an <see cref="AsyncReadWriteLock"/> in write mode. Disposing the instance leaves the lock.
/// </summary>
/// <remarks>
/// <para>
/// Guards returned from <c>TryEnter</c> methods may not have entered the lock: check <see cref="IsEntered"/>. Disposing a guard that did not enter
/// the lock does nothing, so a guard can always be assigned to a <see langword="using"/> declaration.</para>
/// <para>
/// The guard can be disposed on any thread, so it can be held across an <see langword="await"/>. Disposing it never waits.</para>
/// <para>
/// Disposing the same guard twice does nothing the second time, also when two threads dispose it at the same time. A copy of a guard is a second
/// handle to the same hold, and only one of the two may be disposed: disposing the other throws a <see cref="SynchronizationLockException"/> and
/// leaves the lock as it is, whoever holds it by then.</para>
/// <para>
/// A copy is easily made without meaning to. A <see langword="using"/> statement over a variable that already holds the guard,
/// <c>using (guard) { }</c>, disposes a hidden copy and leaves the variable looking undisposed, so a later <c>guard.Dispose()</c> throws. Use
/// <c>using var guard = await …</c> or <c>using (await …) { }</c>, which dispose the guard itself. A guard kept in a <see langword="readonly"/>
/// field is copied by every call to <see cref="Dispose"/>, so only the first call releases and each later one throws: keep a guard in a local
/// variable or in a field that is not read-only. The guard is marked <c>[NonCopyable]</c>: the Singulink.CodeAnalysis.Analyzers analyzer, which
/// comes with this package, reports copies of it (SCA1001).</para>
/// </remarks>
[NonCopyable]
public struct AsyncWriteGuard : IDisposable
{
    private AsyncReadWriteLock? _lock;
    private int _isDisposed;
    private readonly uint _epoch;
    private readonly bool _isEntered;
    private readonly bool _isInitialized;

    /// <summary>
    /// Gets a value indicating whether the instance is in its default, uninitialized state.
    /// </summary>
    public readonly bool IsDefault => !_isInitialized;

    /// <summary>
    /// Gets a value indicating whether the guard entered the lock. Always <see langword="true"/> for guards obtained from
    /// <see cref="AsyncReadWriteLock.EnterWriteGuardAsync"/>; a guard obtained from a <c>TryEnter</c> method did not enter the lock if it could not be
    /// entered in time, in which case the protected resource must not be accessed.
    /// </summary>
    public readonly bool IsEntered
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            return _isEntered;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the object has been disposed.
    /// </summary>
    public readonly bool IsDisposed
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            return Volatile.Read(in _isDisposed) is not 0;
        }
    }

    internal AsyncWriteGuard(AsyncReadWriteLock @lock, uint epoch, bool entered)
    {
        _lock = @lock;
        _epoch = epoch;
        _isEntered = entered;
        _isInitialized = true;
    }

    /// <summary>
    /// Leaves the lock. Does nothing if the guard did not enter the lock or has already been disposed.
    /// </summary>
    /// <exception cref="SynchronizationLockException">The hold of this guard has already been released, because a copy of the guard has already been
    /// disposed.</exception>
    public void Dispose()
    {
        Throw.NotInitializedIf(!_isInitialized);

        // An exchange, so that two threads that dispose this instance at the same time release its hold once.
        if (Interlocked.Exchange(ref _isDisposed, 1) is not 0)
            return;

        var @lock = _lock!;
        _lock = null;

        if (_isEntered)
            @lock.ReleaseWrite(_epoch);
    }
}
