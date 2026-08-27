using System.Collections.Concurrent;

namespace Singulink.Threading;

/// <summary>
/// Provides a mechanism for locking resources based on a key.
/// </summary>
/// <typeparam name="T">The type of the key used to identify locks.</typeparam>
public class KeyLocker<T> where T : notnull
{
    private readonly record struct Entry(SemaphoreSlim Semaphore, int RefCount);

    private readonly ConcurrentDictionary<T, Entry> _lockEntryLookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyLocker{T}"/> class.
    /// </summary>
    public KeyLocker() : this(null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyLocker{T}"/> class with a specified equality comparer for the keys.
    /// </summary>
    public KeyLocker(IEqualityComparer<T>? comparer)
    {
        _lockEntryLookup = new ConcurrentDictionary<T, Entry>(comparer);
    }

    /// <summary>
    /// Checks if a lock is currently held for the specified key.
    /// </summary>
    public bool IsLocked(T key) => _lockEntryLookup.TryGetValue(key, out _);

    /// <summary>
    /// Acquires a lock for the specified key. If the lock is already held, it will wait until it can acquire the lock.
    /// </summary>
    /// <param name="key">The key to lock.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The acquired lock.</returns>
    public KeyLock<T> Lock(T key, CancellationToken cancellationToken = default) => Lock(key, Timeout.Infinite, cancellationToken);

    /// <summary>
    /// Acquires a lock for the specified key. If the lock is already held, it will wait until it can acquire the lock or the specified timeout expires.
    /// </summary>
    /// <param name="key">The key to lock.</param>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see cref="Timeout.Infinite"/>(<c>-1</c>) to wait indefinitely.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The acquired lock.</returns>
    /// <exception cref="TimeoutException">Lock could not be acquired within the specified timeout.</exception>
    public KeyLock<T> Lock(T key, int millisecondsTimeout, CancellationToken cancellationToken = default)
    {
        Entry entry = AddEntryRef(key);
        bool acquired = false;

        try
        {
            acquired = entry.Semaphore.Wait(millisecondsTimeout, cancellationToken);
        }
        finally
        {
            // Balance the reference taken above if the wait failed (timeout) or threw (cancellation) - the
            // caller gets no lock, so nothing else will ever release it.
            if (!acquired)
                ReleaseEntryRef(key);
        }

        if (!acquired)
            throw new TimeoutException("Failed to acquire lock within the specified timeout.");

        return new(key, this);
    }

    /// <summary>
    /// Acquires a lock for the specified key. If the lock is already held, it will wait until it can acquire the lock or the specified timeout expires.
    /// </summary>
    /// <param name="key">The key to lock.</param>
    /// <param name="timeout">The amount of time to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The acquired lock.</returns>
    /// <exception cref="TimeoutException">Lock could not be acquired within the specified timeout.</exception>
    public KeyLock<T> Lock(T key, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Entry entry = AddEntryRef(key);
        bool acquired = false;

        try
        {
            acquired = entry.Semaphore.Wait(timeout, cancellationToken);
        }
        finally
        {
            // Balance the reference taken above if the wait failed (timeout) or threw (cancellation) - the
            // caller gets no lock, so nothing else will ever release it.
            if (!acquired)
                ReleaseEntryRef(key);
        }

        if (!acquired)
            throw new TimeoutException("Failed to acquire lock within the specified timeout.");

        return new(key, this);
    }

    /// <summary>
    /// Asynchronously acquires a lock for the specified key. If the lock is already held, it will wait until it can acquire the lock.
    /// </summary>
    /// <param name="key">The key to lock.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task that contains the acquired lock when it completes.</returns>
    public ValueTask<KeyLock<T>> LockAsync(T key, CancellationToken cancellationToken = default) => LockAsync(key, Timeout.Infinite, cancellationToken);

    /// <summary>
    /// Asynchronously acquires a lock for the specified key. If the lock is already held, it will wait until it can acquire the lock or the specified timeout
    /// expires.
    /// </summary>
    /// <param name="key">The key to lock.</param>
    /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see cref="Timeout.Infinite"/> (<c>-1</c>) to wait indefinitely.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task that contains the acquired lock when it completes.</returns>
    /// <exception cref="TimeoutException">Lock could not be acquired within the specified timeout.</exception>
    public async ValueTask<KeyLock<T>> LockAsync(T key, int millisecondsTimeout, CancellationToken cancellationToken = default)
    {
        Entry entry = AddEntryRef(key);
        bool acquired = false;

        try
        {
            acquired = await entry.Semaphore.WaitAsync(millisecondsTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Balance the reference taken above if the wait failed (timeout) or threw (cancellation) - the
            // caller gets no lock, so nothing else will ever release it.
            if (!acquired)
                ReleaseEntryRef(key);
        }

        if (!acquired)
            throw new TimeoutException("Failed to acquire lock within the specified timeout.");

        return new(key, this);
    }

    /// <summary>
    /// Asynchronously acquires a lock for the specified key. If the lock is already held, it will wait until it can acquire the lock or the specified timeout
    /// expires.
    /// </summary>
    /// <param name="key">The key to lock.</param>
    /// <param name="timeout">The amount of time to wait, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A task that contains the acquired lock when it completes.</returns>
    /// <exception cref="TimeoutException">Lock could not be acquired within the specified timeout.</exception>
    public async ValueTask<KeyLock<T>> LockAsync(T key, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Entry entry = AddEntryRef(key);
        bool acquired = false;

        try
        {
            acquired = await entry.Semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Balance the reference taken above if the wait failed (timeout) or threw (cancellation) - the
            // caller gets no lock, so nothing else will ever release it.
            if (!acquired)
                ReleaseEntryRef(key);
        }

        if (!acquired)
            throw new TimeoutException("Failed to acquire lock within the specified timeout.");

        return new(key, this);
    }

    internal void Release(T key) => ReleaseEntryRef(key).Semaphore.Release();

    /// <summary>
    /// Takes an interested-party reference on the key's entry, creating the entry if it does not exist. Every
    /// reference must be balanced by a <see cref="ReleaseEntryRef"/> call (via lock disposal or wait failure)
    /// so the entry is removed when no references remain.
    /// </summary>
    private Entry AddEntryRef(T key)
    {
        return _lockEntryLookup.AddOrUpdate(key,
            static _ => new Entry(new SemaphoreSlim(1, 1), 1),
            static (_, entry) => entry with { RefCount = entry.RefCount + 1 });
    }

    /// <summary>
    /// Releases an interested-party reference on the key's entry, removing the entry when no references
    /// remain. Callers that hold the semaphore release it separately (see <see cref="Release"/>); failed
    /// waits never acquired it and must not release it.
    /// </summary>
    private Entry ReleaseEntryRef(T key)
    {
        while (true)
        {
            if (!_lockEntryLookup.TryGetValue(key, out Entry entry))
                throw new InvalidOperationException("Key not found.");

            if (entry.RefCount > 1)
            {
                if (_lockEntryLookup.TryUpdate(key, entry with { RefCount = entry.RefCount - 1 }, entry))
                    return entry;
            }
            else
            {
                if (_lockEntryLookup.TryRemove(KeyValuePair.Create(key, entry)))
                    return entry;
            }
        }
    }
}
