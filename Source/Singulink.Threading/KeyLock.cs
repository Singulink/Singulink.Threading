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

    /// <summary>
    /// Gets the key associated with this lock.
    /// </summary>
    public readonly T Key
    {
        get {
            ObjectDisposedException.ThrowIf(_isDisposed, nameof(KeyLock<>));
            return Throw.NotInitializedIfNull(_key);
        }
    }

    /// <summary>
    /// Gets the parent <see cref="KeyLocker{T}"/> that this lock is associated with.
    /// </summary>
    public readonly KeyLocker<T> Parent
    {
        get {
            ObjectDisposedException.ThrowIf(_isDisposed, nameof(KeyLock<>));
            return Throw.NotInitializedIfNull(_parent);
        }
    }

    internal KeyLock(T key, KeyLocker<T> parent)
    {
        _key = key;
        _parent = parent;
    }

    /// <summary>
    /// Releases the lock associated with the key.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        Throw.NotInitializedIfNull(_parent).Release(_key!);

        _key = default;
        _parent = null;
    }
}
