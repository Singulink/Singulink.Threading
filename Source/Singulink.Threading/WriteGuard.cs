using Microsoft.CodeAnalysis;
using Singulink.Threading.Utilities;

namespace Singulink.Threading;

/// <summary>
/// Represents a disposable write guard over a <see cref="ReaderWriterLockSlim"/>.
/// </summary>
[NonCopyable]
public struct WriteGuard : IDisposable
{
    private ReaderWriterLockSlim? _rwLock;
    private bool _isDisposed;

    internal WriteGuard(ReaderWriterLockSlim readerWriterLock)
    {
        _rwLock = readerWriterLock;
        _rwLock.EnterWriteLock();
    }

    /// <summary>
    /// Releases the write guard.
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
