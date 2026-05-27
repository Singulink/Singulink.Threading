using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Singulink.Threading.Utilities;

namespace Singulink.Threading;

/// <summary>
/// Represents an acquired lock for a specific key in a <see cref="KeyLocker{T}"/>. Disposing the instance releases the lock.
/// </summary>
[NonCopyable]
public struct KeyLock<T> : IDisposable where T : notnull
{
    private T? _key;
    private KeyLocker<T>? _parent;
    private bool _isDisposed;
    private readonly bool _isInitialized;

    /// <summary>
    /// Gets the key associated with this lock.
    /// </summary>
    public readonly T Key
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            ObjectDisposedException.ThrowIf(IsDisposed, nameof(KeyLock<>));
            return _key;
        }
    }

    /// <summary>
    /// Gets the parent <see cref="KeyLocker{T}"/> that this lock is associated with.
    /// </summary>
    public readonly KeyLocker<T> Parent
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            ObjectDisposedException.ThrowIf(IsDisposed, nameof(KeyLock<>));
            return _parent;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the instance is in its default, uninitialized state.
    /// </summary>
    public readonly bool IsDefault => !_isInitialized;

    /// <summary>
    /// Gets a value indicating whether the object has been disposed.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_key), nameof(_parent))]
    public readonly bool IsDisposed
    {
        get {
            Throw.NotInitializedIf(!_isInitialized);
            return _isDisposed;
        }
    }

    internal KeyLock(T key, KeyLocker<T> parent)
    {
        _key = key;
        _parent = parent;
        _isInitialized = true;
    }

    /// <summary>
    /// Releases the lock associated with the key.
    /// </summary>
    public void Dispose()
    {
        Throw.NotInitializedIf(!_isInitialized);

        if (IsDisposed)
            return;

        _isDisposed = true;
        _parent.Release(_key);

        _key = default;
        _parent = null;
    }
}
