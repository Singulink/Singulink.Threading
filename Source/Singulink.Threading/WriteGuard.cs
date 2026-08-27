using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Singulink.Threading.Utilities;

namespace Singulink.Threading;

/// <summary>
/// Represents a disposable write guard over a <see cref="ReadWriteLock"/>.
/// </summary>
/// <remarks>
/// <para>Guards returned from <c>TryEnter</c> methods may not have entered the lock — check
/// <see cref="IsEntered"/> after acquiring. Disposing a guard that did not enter the lock is a safe no-op,
/// so guards can always be assigned directly to a <see langword="using"/> declaration.</para>
/// <para>Disposal exits write mode. For guards obtained from
/// <see cref="UpgradeableReadGuard.EnterUpgradedWriteGuard"/>, this returns the thread to upgradeable read
/// mode, and the thread can upgrade again — write access can be scoped and repeated within one upgradeable
/// read session. Write locks entered directly cannot be downgraded to read locks; enter the lock in
/// upgradeable read mode instead if downgrading may be needed.</para>
/// <para>Guards are thread-affine: an entered guard must be disposed on the thread that entered the lock,
/// which is enforced with an <see cref="InvalidOperationException"/> when violated.</para>
/// </remarks>
[NonCopyable]
public struct WriteGuard : IDisposable
{
    private ReaderWriterLockSlim? _rwLock;
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
    /// obtained from unconditional <c>Enter</c> methods; guards obtained from <c>TryEnter</c> methods did
    /// not enter the lock if the timeout expired, in which case the protected resource must not be accessed.
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
    [MemberNotNullWhen(false, nameof(_rwLock))]
    public readonly bool IsDisposed
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            return _isDisposed;
        }
    }

    internal WriteGuard(ReaderWriterLockSlim rwLock, bool entered)
    {
        _rwLock = rwLock;
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _isEntered = entered;
        _isInitialized = true;
    }

    /// <summary>
    /// Releases the write guard, exiting write mode. No-op if the guard did not enter the lock. If the
    /// guard was obtained by upgrading an upgradeable read guard, the thread returns to upgradeable read
    /// mode.
    /// </summary>
    /// <exception cref="InvalidOperationException">The guard entered the lock and disposal was attempted on
    /// a different thread than the one that entered it.</exception>
    public void Dispose()
    {
        Throw.NotInitializedIf(!_isInitialized);

        if (IsDisposed)
            return;

        Throw.WrongGuardThreadIf(_isEntered && Environment.CurrentManagedThreadId != _ownerThreadId);

        _isDisposed = true;

        if (_isEntered)
            _rwLock.ExitWriteLock();

        _rwLock = null;
    }
}
