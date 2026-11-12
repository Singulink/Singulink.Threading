using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Singulink.Threading;

/// <summary>
/// A non-reentrant lock for short critical sections that never nest and are never held across other work.
/// </summary>
/// <remarks>
/// <para>
/// The lock is entered with <see cref="EnterGuard"/>, which returns a guard that leaves the lock when it is disposed. The guard is a struct, so entering
/// and leaving the lock in a <see langword="using"/> statement allocates nothing.</para>
/// <para>
/// The lock is a leaf: code that holds it takes no other lock and does no other work that can block, such as I/O or waiting on another thread. It is
/// not reentrant: entering it on a thread that already holds it throws a <see cref="LockRecursionException"/> instead of deadlocking or entering it a
/// second time. Code that requires the lock can call <see cref="EnsureHeld"/>, and code that must not run under it, such as a call that may take another
/// lock or a wait, can call <see cref="EnsureNotHeld"/>. These checks are always on, in release builds as well as debug builds; each costs about as much
/// as reading <see cref="IsHeldByCurrentThread"/>.</para>
/// <para>
/// The lock has thread affinity: a guard must be disposed on the thread that entered the lock, so it must not be held across an <see langword="await"/>.
/// The lock is built on <see cref="Monitor"/> over a private object, so <see langword="lock"/> statements in other code cannot enter it.</para>
/// </remarks>
public sealed class LeafLock
{
    private readonly object _sync = new();

    /// <summary>
    /// Gets a value indicating whether the current thread holds the lock.
    /// </summary>
    public bool IsHeldByCurrentThread => Monitor.IsEntered(_sync);

    /// <summary>
    /// Enters the lock and returns a guard that leaves it when it is disposed.
    /// </summary>
    /// <remarks>
    /// If another thread holds the lock, the call waits until it is free.
    /// </remarks>
    /// <exception cref="LockRecursionException">The current thread already holds the lock.</exception>
    public LeafLockGuard EnterGuard()
    {
        if (Monitor.IsEntered(_sync))
            ThrowRecursion();

        Monitor.Enter(_sync);
        return new LeafLockGuard(this);
    }

    /// <summary>
    /// Throws an exception if the current thread does not hold the lock.
    /// </summary>
    /// <exception cref="InvalidOperationException">The current thread does not hold the lock.</exception>
    [StackTraceHidden]
    public void EnsureHeld()
    {
        if (!Monitor.IsEntered(_sync))
        {
            [DoesNotReturn]
            static void Throw() => throw new InvalidOperationException("The lock is not held by the current thread.");
            Throw();
        }
    }

    /// <summary>
    /// Throws an exception if the current thread holds the lock.
    /// </summary>
    /// <exception cref="InvalidOperationException">The current thread holds the lock.</exception>
    [StackTraceHidden]
    public void EnsureNotHeld()
    {
        if (Monitor.IsEntered(_sync))
        {
            [DoesNotReturn]
            static void Throw() => throw new InvalidOperationException(
                "The lock is held by the current thread. It is a leaf lock and is not held across other locks, waits or calls that may take them.");
            Throw();
        }
    }

    internal void Exit() => Monitor.Exit(_sync);

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowRecursion() =>
        throw new LockRecursionException("The lock is already held by the current thread. It is a leaf lock and is not entered again while it is held.");
}
