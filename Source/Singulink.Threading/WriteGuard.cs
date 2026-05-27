using System.Diagnostics.CodeAnalysis;
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
    private readonly bool _isInitialized;

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

    internal WriteGuard(ReaderWriterLockSlim readerWriterLock)
    {
        _rwLock = readerWriterLock;
        _rwLock.EnterWriteLock();
        _isInitialized = true;
    }

    /// <summary>
    /// Releases the write guard.
    /// </summary>
    public void Dispose()
    {
        Throw.NotInitializedIf(!_isInitialized);

        if (IsDisposed)
            return;

        _isDisposed = true;

        _rwLock.ExitWriteLock();
        _rwLock = null;
    }
}
