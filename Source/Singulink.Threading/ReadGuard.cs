using System.Diagnostics.CodeAnalysis;
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

    internal ReadGuard(ReaderWriterLockSlim rwLock)
    {
        _rwLock = rwLock;
        _rwLock.EnterReadLock();
        _isInitialized = true;
    }

    /// <summary>
    /// Releases the read guard.
    /// </summary>
    public void Dispose()
    {
        Throw.NotInitializedIf(!_isInitialized);

        if (IsDisposed)
            return;

        _isDisposed = true;
        _rwLock.ExitReadLock();
        _rwLock = null;
    }
}
