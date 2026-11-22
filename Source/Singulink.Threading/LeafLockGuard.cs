using System.Diagnostics.CodeAnalysis;
using Singulink.CodeAnalysis;
using Singulink.Threading.Utilities;

namespace Singulink.Threading;

/// <summary>
/// Represents a hold on a <see cref="LeafLock"/>. Disposing the instance leaves the lock.
/// </summary>
/// <remarks>
/// <para>The guard must be disposed on the thread that entered the lock. Disposing it on another thread throws an
/// <see cref="InvalidOperationException"/> and leaves the lock held, so the guard can still be disposed on the right thread.</para>
/// </remarks>
[NonCopyable]
public struct LeafLockGuard : IDisposable
{
    private LeafLock? _lock;
    private bool _isDisposed;
    private readonly bool _isInitialized;

    /// <summary>
    /// Gets a value indicating whether the instance is in its default, uninitialized state.
    /// </summary>
    public readonly bool IsDefault => !_isInitialized;

    /// <summary>
    /// Gets a value indicating whether the object has been disposed.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_lock))]
    public readonly bool IsDisposed
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            return _isDisposed;
        }
    }

    internal LeafLockGuard(LeafLock @lock)
    {
        _lock = @lock;
        _isInitialized = true;
    }

    /// <summary>
    /// Leaves the lock. Does nothing if the guard has already been disposed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The guard is disposed on a different thread than the one that entered the lock.</exception>
    public void Dispose()
    {
        Throw.NotInitializedIf(!_isInitialized);

        if (IsDisposed)
            return;

        Throw.WrongGuardThreadIf(!_lock.IsHeldByCurrentThread);

        _isDisposed = true;
        _lock.Exit();
        _lock = null;
    }
}
