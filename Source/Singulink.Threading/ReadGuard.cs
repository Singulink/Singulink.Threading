using Microsoft.CodeAnalysis;
using Singulink.Threading.Utilities;

namespace Singulink.Threading;

/// <summary>
/// Represents a disposable read guard over a <see cref="ReaderWriterLockSlim"/>.
/// </summary>
[NonCopyable]
public struct ReadGuard : IDisposable
{
    private ReaderWriterLockSlim? _rwLock;
    private bool _isDisposed;

    internal ReadGuard(ReaderWriterLockSlim rwLock)
    {
        _rwLock = rwLock;
        _rwLock.EnterReadLock();
    }

    /// <summary>
    /// Releases the read guard.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        Throw.NotInitializedIfNull(_rwLock).ExitReadLock();
        _rwLock = null;
    }
}
