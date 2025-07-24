# Singulink.Threading

[![Chat on Discord](https://img.shields.io/discord/906246067773923490)](https://discord.gg/EkQhJFsBu6)
[![View nuget packages](https://img.shields.io/nuget/v/Singulink.Threading.svg)](https://www.nuget.org/packages/Singulink.Threading/)
[![Build](https://github.com/Singulink/Singulink.Threading/workflows/build/badge.svg)](https://github.com/Singulink/Singulink.Threading/actions?query=workflow%3A%22build%22)

**Singulink.Threading** is a small utility library used to support other Singulink projects with some common multi-threading related functionality. It has a key-based asynchronous-capable locking mechanism, common interlocked spin operation helpers, reader/writer lock extensions and an interlocked flag implementation.

### About Singulink

We are a small team of engineers and designers dedicated to building beautiful, functional, and well-engineered software solutions. We offer very competitive rates as well as fixed-price contracts and welcome inquiries to discuss any custom development / project support needs you may have.

This package is part of our **Singulink Libraries** collection. Visit https://github.com/Singulink to see our full list of publicly available libraries and other open-source projects.

## Installation

The package is available on NuGet - simply install the `Singulink.Threading` package.

**Supported Runtimes**: .NET 8.0+

## API

You can view the fully documented API on the [project documentation site](https://www.singulink.com/Docs/Singulink.Threading/api/Singulink.Threading.html).

## Usage

This library makes use of mutable structs as a low-level performance optimization. These structs have been annotated with a `[NonCopyable]` attribute to allow a non-copyable struct analyzer to detect misuse. When using this library, it is recommend that either [NonCopyableAnalyzer](https://github.com/ufcpp/NonCopyableAnalyzer) or [Roslyn.Diagnostics.Analyzers](https://www.nuget.org/packages/Roslyn.Diagnostics.Analyzers/) is added to the project to warn on potentially unintended copying of these structs.

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

    public void AllowOneMoreRun()
    {
        _executedFlag.TryClear();
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
        DoProcessing(itemId);
    }
}
```

### ReaderWriterLockSlimExtensions

When you import the `Singulink.Threading` namespace, 3 new extension methods appear on `ReaderWriterLockSlim` instances:
- `EnterReadGuard()`
- `EnterWriteGuard()`
- `EnterUpgradeableReadGuard()`

```c#
using Singulink.Threading;

ReaderWriterLockSlim lock = new();

// Locks are acquired inside the using blocks and released at the end:

using (lock.EnterReadGuard())
{
    // Safe to read here
    ReadData();
}

using (lock.EnterWriteGuard())
{
    // Safe to write here
    WriteData();
}

using (var upgradeableGuard = lock.EnterUpgradeableReadGuard())
{
    // Safe to read here
    ReadData();

    if (someCondition)
    {
        upgradeableGuard.UpgradeToWriteGuard();
        // Safe to write here
        WriteData();
    }
}
```
