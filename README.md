# Singulink.Threading

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)
[![View nuget packages](https://img.shields.io/nuget/v/Singulink.Threading.svg)](https://www.nuget.org/packages/Singulink.Threading/)
[![Build and Test](https://github.com/Singulink/Singulink.Threading/workflows/build%20and%20test/badge.svg)](https://github.com/Singulink/Singulink.Threading/actions?query=workflow%3A%22build+and+test%22)

**Singulink.Threading** is a small utility library used to support other Singulink projects with some common multi-threading related functionality. It has a key-based asynchronous-capable locking mechanism, common interlocked spin operation helpers, a guard-based reader/writer lock, a non-reentrant leaf lock, a timer that paces retries of an operation and an interlocked flag implementation.

### About Singulink

We are a small team of engineers and designers dedicated to building beautiful, functional, and well-engineered software solutions. We offer very competitive rates as well as fixed-price contracts and welcome inquiries to discuss any custom development / project support needs you may have.

This package is part of our **Singulink Libraries** collection. Visit https://github.com/Singulink to see our full list of publicly available libraries and other open-source projects.

## Installation

The package is available on NuGet - simply install the `Singulink.Threading` package.

**Supported Runtimes**: .NET 8.0+

## API

You can view the fully documented API on the [project documentation site](https://www.singulink.com/Docs/Singulink.Threading/api/Singulink.Threading.html).

## Usage

This library makes use of mutable structs as a low-level performance optimization. These structs have been annotated with a `[NonCopyable]` attribute to allow a non-copyable struct analyzer to detect misuse, so if you add [Roslyn.Diagnostics.Analyzers](https://www.nuget.org/packages/Roslyn.Diagnostics.Analyzers/) to your project then it will warn on potentially unintended copying of these structs. Note, there are some usability issues with [NonCopyableAnalyzer](https://github.com/ufcpp/NonCopyableAnalyzer) and it does not appear to be maintained anymore, so we recommend using the Roslyn analyzer instead.

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
KeyLocker<string> _locker = new(StringComparer.IgnoreCase);

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

The naming convention across the API: `Enter…Guard` methods return a guard that entered the lock, `TryEnter…Guard` methods return a guard that may not have entered the lock (check `IsEntered` — disposing a non-entered guard is a safe no-op, so results can be assigned directly to `using` declarations), and methods ending in `…Lock` operate on the current guard in place.

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

Write locks entered directly cannot be downgraded - enter the lock in upgradeable read mode if downgrading may be needed. The lock has managed thread affinity: each guard must be entered and disposed on the same thread, and guards must not be used across `await` boundaries. This is enforced - guard operations attempted on a different thread than the one that entered the lock throw `InvalidOperationException` immediately instead of corrupting lock state or deadlocking.

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
