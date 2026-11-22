# Singulink.Threading

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)
[![View nuget packages](https://img.shields.io/nuget/v/Singulink.Threading.svg)](https://www.nuget.org/packages/Singulink.Threading/)
[![Build and Test](https://github.com/Singulink/Singulink.Threading/workflows/build%20and%20test/badge.svg)](https://github.com/Singulink/Singulink.Threading/actions?query=workflow%3A%22build+and+test%22)
[![View documentation](https://img.shields.io/badge/View-Documentation-blue)](https://www.singulink.com/Docs/Singulink.Threading/)

**Singulink.Threading** is a small utility library of locks and lock-free helpers for multithreaded and async code:

| Type | What it is |
| --- | --- |
| [`InterlockedFlag`](#interlockedflag) | A lock-free flag that is set and cleared atomically |
| [`InterlockedSpin`](#interlockedspin) | Lock-free updates of a value that retry when another thread changes it first |
| [`KeyLocker`](#keylocker) | A lock for each key, entered synchronously or asynchronously |
| [`ReadWriteLock`](#readwritelock) | A reader/writer lock for synchronous code, with an upgradeable read mode |
| [`AsyncReadWriteLock`](#asyncreadwritelock) | A reader/writer lock for async code that can be held across an `await` |
| [`LeafLock`](#leaflock) | A non-reentrant lock for short critical sections |
| [`RetryTimer`](#retrytimer) | A timer that paces the retries of an operation until a time limit passes |

**Documentation**: the fully documented API is on the [project documentation site](https://www.singulink.com/Docs/Singulink.Threading/api/Singulink.Threading.html).

### About Singulink

We are a small team of engineers and designers dedicated to building beautiful, functional, and well-engineered software solutions. We offer very competitive rates as well as fixed-price contracts and welcome inquiries to discuss any custom development / project support needs you may have.

This package is part of our **Singulink Libraries** collection. Visit https://github.com/Singulink to see our full list of publicly available libraries and other open-source projects.

## Installation

The package is available on NuGet - simply install the `Singulink.Threading` package.

**Supported Runtimes**: .NET 10.0+

## Usage

This library makes use of mutable structs as a low-level performance optimization. These structs are marked `[NonCopyable]` because copying one can lead to misuse. The package brings the [Singulink.CodeAnalysis.Analyzers](https://www.nuget.org/packages/Singulink.CodeAnalysis.Analyzers/) analyzer with it, so copies of these structs in your code get warnings (the `SCA1000` rules) with no setup. If those warnings are new to you, [Why Am I Seeing This?](https://www.singulink.com/Docs/Singulink.CodeAnalysis/articles/start/why-am-i-seeing-this.html) explains them, and [Fixing Common Warnings](https://www.singulink.com/Docs/Singulink.CodeAnalysis/articles/start/fixing-common-warnings.html) shows the usual fixes.

### Choosing a Lock

| Lock | Use it for | Can be held across an `await` | Entering it again while you hold it | Time limit on entering |
| --- | --- | --- | --- | --- |
| [`KeyLocker`](#keylocker) | One lock per key, such as an item ID | Yes | Deadlocks | Yes |
| [`ReadWriteLock`](#readwritelock) | Many readers or one writer in synchronous code, with an upgradeable read mode | No | Throws | Yes |
| [`AsyncReadWriteLock`](#asyncreadwritelock) | Many readers or one writer in code that awaits while it holds the lock | Yes | Can deadlock | Yes |
| [`LeafLock`](#leaflock) | A short critical section that never nests | No | Throws | No |

The two locks that cannot be held across an `await` have thread affinity: the thread that entered the lock has to release it.

### InterlockedFlag

```c#
public class ExecuteOnce
{
    private InterlockedFlag _executedFlag;

    public bool DidExecute => _executedFlag.IsSet;

    public void Execute()
    {
        if (_executedFlag.TrySet())
        {
            // Run code that should only execute once
        }
    }

    // Returns true if another run was allowed,
    // or false if it was already allowed before.

    public bool AllowOneMoreRun()
    {
        return _executedFlag.TryClear();
    }
}
```

### InterlockedSpin

```c#
const int MaxClients = 10;

int _clientCount;

void OnClientConnect()
{
    if (!InterlockedSpin.TryIncrementToMax(ref _clientCount, MaxClients))
        RefuseConnection();
}

void OnClientDisconnect()
{
    Interlocked.Decrement(ref _clientCount);
}
```

```c#
int[] _items = [1, 2, 3];

// Returns [1, 2, 3, 4] on the first call, [1, 2, 3, 4, 5] on the second call, etc.
int[] AddNextItem()
{
    return InterlockedSpin.Exchange(ref _items, items => [..items, items[^1] + 1]);
}
```

### KeyLocker

```c#
KeyLocker<string> _locker = new(StringComparer.OrdinalIgnoreCase);

void ProcessItem(string itemId)
{
    using (_locker.Lock(itemId))
    {
        // Safe to process the item here without concurrent access
        DoProcessing(itemId);
    }
}

async Task ProcessItemAsync(string itemId)
{
    using (await _locker.LockAsync(itemId))
    {
        // Safe to process the item here without concurrent access
        await DoProcessingAsync(itemId);
    }
}
```

### ReadWriteLock

`ReadWriteLock` is a reader/writer lock (wrapping a `ReaderWriterLockSlim` with `LockRecursionPolicy.NoRecursion`) that manages all lock entry through disposable guards, so lock modes can't be manipulated behind the guards' backs and locks can't be accidentally left unreleased.

Every method that enters the lock returns a guard, and disposing the guard leaves the lock. `Enter…Guard` methods wait until they enter. `TryEnter…Guard` methods take a time limit and return a guard whose `IsEntered` property says whether they entered; disposing a guard that did not enter does nothing, so you can always assign the result to a `using` declaration. Methods ending in `…Lock` change the mode of a guard you already have.

```c#
ReadWriteLock rwLock = new();

// Locks are acquired inside the using blocks and released at the end:

using (rwLock.EnterReadGuard())
{
    // Safe to read here
    ReadData();
}

using (rwLock.EnterWriteGuard())
{
    // Safe to write here
    WriteData();
}
```

Timeout-based acquisition uses the `TryEnter…Guard` methods:

```c#
using var guard = rwLock.TryEnterWriteGuard(TimeSpan.FromSeconds(5));

if (!guard.IsEntered)
    return false; // could not acquire the lock within the timeout

WriteData();
return true;
```

Upgradeable read guards support scoped, repeatable upgrades to write access, and one-way downgrades to a plain read lock:

```c#
using var guard = rwLock.EnterUpgradeableReadGuard();

if (NeedsUpdate())
{
    using (guard.EnterUpgradedWriteGuard())
    {
        // Safe to write here
        ApplyUpdate();
    }

    // Back in upgradeable read mode here - other readers can run again, and the
    // guard can be upgraded again if needed.
}

// Optionally downgrade to a plain read lock so another thread can enter
// upgradeable (or write) mode while this thread continues reading:
guard.DowngradeToReadLock();
ReadData();
```

Write locks entered directly cannot be downgraded: enter the lock in upgradeable read mode if downgrading may be needed. The lock has managed thread affinity: each guard must be entered and disposed on the same thread, and guards must not be used across `await` boundaries. This is enforced: guard operations attempted on a different thread than the one that entered the lock throw `InvalidOperationException` immediately instead of corrupting lock state or deadlocking.

### AsyncReadWriteLock

`AsyncReadWriteLock` is a reader/writer lock for async code: many callers can read together, or one caller can write, and either can hold the lock across an `await`. Entering the lock returns a guard, which is one hold on the lock in read or write mode. Disposing the guard releases the hold, and it can be disposed on any thread.

```c#
AsyncReadWriteLock rwLock = new();

async Task<Data> ReadAsync(CancellationToken cancellationToken)
{
    using (await rwLock.EnterReadGuardAsync(cancellationToken))
    {
        // Safe to read here, together with other readers
        return await ReadDataAsync();
    }
}

async Task<bool> TryWriteAsync(Data data, CancellationToken cancellationToken)
{
    using var guard = await rwLock.TryEnterWriteGuardAsync(TimeSpan.FromSeconds(5), cancellationToken);

    if (!guard.IsEntered)
        return false; // could not enter the lock within the time limit

    // Safe to write here
    await WriteDataAsync(data);
    return true;
}
```

The methods come in three forms, named like those of `ReadWriteLock`:

| Method | Waits | Returns |
| --- | --- | --- |
| `Enter…GuardAsync` | Until it enters, or until the token is canceled | A guard that entered the lock |
| `TryEnter…GuardAsync` | Until it enters, the time limit passes, or the token is canceled | A guard; check `IsEntered` |
| `TryEnter…Guard` | Never | A guard; check `IsEntered` |

Disposing a guard that did not enter the lock does nothing, so you can always assign the result to a `using` declaration.

> [!WARNING]
> **The lock is not reentrant.** Entering it again while you hold it can wait forever.

The lock has no owner, so it cannot tell that a caller already holds it, and `IsHeldForRead`, `IsHeldForWrite` and `CurrentReadCount` describe all callers, not the current one. A caller that holds the lock in write mode and enters it again, or holds it in read mode and enters it in write mode, waits for itself. A caller that holds it in read mode and enters read mode again waits behind a writer that is waiting, and that writer waits for the caller's first read. Only the `Readers` preference, described below, lets that second read in at once.

> [!WARNING]
> **Give the time limit to the lock, not to the task.** Pass a time limit to `TryEnter…GuardAsync`, or a cancellation token to either async method. Do not wrap the returned task in `WaitAsync(timeout)` or race it against `Task.Delay` with `Task.WhenAny`. That ends your await but not the wait: the lock later grants the hold to a task that nobody reads, and the hold is never released.

The preference passed to the constructor decides who enters first when readers and writers both want the lock, and with that who can be kept waiting:

| Preference | A reader that arrives while a writer is waiting | Who can be kept waiting forever |
| --- | --- | --- |
| `Fair` (the default) | waits behind the writer; readers and writers enter in arrival order | nobody |
| `Readers` | enters ahead of the writer | writers, while read holds keep overlapping |
| `Writers` | waits behind the writer, and behind writers that arrive later | readers, while writers keep arriving |

Under `Readers`, a writer can wait forever, so enter write mode with a time limit and check `IsEntered`, as `TryWriteAsync` does above:

```c#
// Readers never wait behind a waiting writer.
AsyncReadWriteLock rwLock = new(AsyncReadWriteLockPreference.Readers);
```

Dispose a guard through the variable that received it, as `using var guard = await …` and `using (await …) { }` do. Two other patterns dispose a hidden copy instead: `using (guard) { }` over an existing variable, and a `Dispose` call on a guard kept in a `readonly` field. Either way the variable or field still looks undisposed, so a later `Dispose` releases a second time: a write guard throws, and a read guard releases another reader's hold.

The guards are `[NonCopyable]`, so the analyzer described under [Usage](#usage) warns about these copies: an assignment from a variable (`SCA1001`), a `using (guard)` statement (`SCA1003`) and a call through a `readonly` field (`SCA1006`).

Entering and leaving the lock when no wait is needed allocates nothing and costs one interlocked operation each. Code that waited for the lock never continues on the thread that released it. Time limits are measured with a `TimeProvider` that you can pass to the constructor, so tests can control them. There is no upgradeable read mode, no way to turn a write hold into a read hold, and no method that blocks a thread until the lock is entered.

### LeafLock

`LeafLock` is a non-reentrant lock for short critical sections that never nest and are never held across other work. Entering it returns a struct guard, so a `using` statement allocates nothing. Entering it again on the thread that holds it throws `LockRecursionException` instead of deadlocking, and `EnsureHeld` and `EnsureNotHeld` check, in release builds too, that code runs under the lock or outside it.

```c#
LeafLock _lock = new();
Dictionary<string, Entry> _entries = [];

void Add(string key, Entry entry)
{
    using (_lock.EnterGuard())
    {
        AddLocked(key, entry);
    }
}

void AddLocked(string key, Entry entry)
{
    _lock.EnsureHeld();
    _entries.Add(key, entry);
}

async Task FlushAsync()
{
    // Throws if this code is ever reached while the lock is held.
    _lock.EnsureNotHeld();
    await WriteToDiskAsync();
}
```

Like the `ReadWriteLock` guards, a `LeafLock` guard must be disposed on the thread that entered the lock, so it must not be held across an `await`.

### RetryTimer

`RetryTimer` paces the attempts of an operation that fails briefly, such as a file that another process has open for a moment: it waits a delay between attempts until a time limit has passed. The time is measured with a `TimeProvider`, so tests can control it. The timer is a struct that the caller loops over, so the operation needs no delegate or closure, and an operation that succeeds on its first attempt allocates nothing.

```c#
async Task<bool> TryMoveAsync(string source, string destination, CancellationToken cancellationToken)
{
    // Retries for up to 5 seconds, 50 ms apart. An overload takes a TimeProvider to measure the time with.
    var retry = RetryTimer.Start(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));

    while (true)
    {
        try
        {
            File.Move(source, destination);
            return true;
        }
        catch (IOException)
        {
            // Something may have the file open for a moment.
        }

        if (!await retry.TryWaitAsync(cancellationToken))
            return false;
    }
}
```

`TryWait` and `TryWaitAsync` wait and return `true` while the time limit has not passed, and return `false` without waiting once it has. `CanRetry` with `Wait` or `WaitAsync` splits the decision from the wait, for code that decides while it holds a lock.

## Further Reading

You can view the fully documented API on the [project documentation site](https://www.singulink.com/Docs/Singulink.Threading/api/Singulink.Threading.html).
