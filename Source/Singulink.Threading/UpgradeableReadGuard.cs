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

    /// <summary>
    /// Gets a value indicating whether the guard has been upgraded to a write guard.
    /// </summary>
    public bool IsUpgraded => _isUpgraded;

    internal UpgradeableReadGuard(ReaderWriterLockSlim rwLock)
    {
        _isUpgraded = false;
        _rwLock = rwLock;
        _rwLock.EnterUpgradeableReadLock();
    }

    /// <summary>
    /// Upgrades this guard to a write guard.
    /// </summary>
    public void UpgradeToWriteGuard()
    {
        Throw.NotInitializedIfNull(_rwLock);

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
        if (_isDisposed)
            return;

        _isDisposed = true;

        Throw.NotInitializedIfNull(_rwLock);

        if (!_isUpgraded)
            _rwLock.ExitUpgradeableReadLock();
        else
            _rwLock.ExitWriteLock();

        _rwLock = null;
    }
}
