using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Singulink.Threading.Utilities;

namespace Singulink.Threading;

/// <summary>
/// Represents a disposable upgradeable read guard over a <see cref="ReaderWriterLockSlim"/>.
/// </summary>
[NonCopyable]
public struct UpgradeableReadGuard : IDisposable
{
    private ReaderWriterLockSlim? _rwLock;
    private bool _isUpgraded;
    private bool _isDisposed;
    private readonly bool _isInitialized;

    /// <summary>
    /// Gets a value indicating whether the guard has been upgraded to a write guard.
    /// </summary>
    public bool IsUpgraded => _isUpgraded;

    /// <summary>
    /// Gets a value indicating whether the instance is in its default, uninitialized state.
    /// </summary>
    public readonly bool IsDefault => !_isInitialized;

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

    internal UpgradeableReadGuard(ReaderWriterLockSlim rwLock)
    {
        _isUpgraded = false;
        _rwLock = rwLock;
        _rwLock.EnterUpgradeableReadLock();
        _isInitialized = true;
    }

    /// <summary>
    /// Upgrades this guard to a write guard.
    /// </summary>
    public void UpgradeToWriteGuard()
    {
        Throw.NotInitializedIf(!_isInitialized);
        ObjectDisposedException.ThrowIf(IsDisposed, nameof(UpgradeableReadGuard));

        if (_isUpgraded)
            throw new InvalidOperationException("The guard is already upgraded to a write guard.");

        _rwLock.EnterWriteLock();
        _isUpgraded = true;
    }

    /// <summary>
    /// Releases the upgradeable read guard (or write guard if it was upgraded).
    /// </summary>
    public void Dispose()
    {
        Throw.NotInitializedIf(!_isInitialized);

        if (IsDisposed)
            return;

        _isDisposed = true;

        if (!_isUpgraded)
            _rwLock.ExitUpgradeableReadLock();
        else
            _rwLock.ExitWriteLock();

        _rwLock = null;
    }
}
