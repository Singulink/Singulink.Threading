# Singulink.Threading Version History

## Version 3.2

- Now targets .NET 10 and no longer supports .NET 8 and 9, which are out of support; version 3.1 remains the last version for them
- New `AsyncReadWriteLock`, a reader/writer lock that can be held across an `await`. Every wait takes a cancellation token and can take a time limit, and a preference chooses who enters first: arrival order, readers or writers. It is entered through the allocation-free `AsyncReadGuard` and `AsyncWriteGuard`
- The package now brings the `Singulink.CodeAnalysis.Analyzers` analyzer, which reports copies of the `[NonCopyable]` structs (the `SCA1000` rules) with no setup

## Version 3.1

- New `LeafLock`, a non-reentrant lock for short critical sections with `EnsureHeld` and `EnsureNotHeld` checks, entered through an allocation-free `LeafLockGuard`
- New `RetryTimer` to retry an operation after a delay until a time limit passes, measured with a `TimeProvider`

## Version 3.0

See the [API documentation](https://www.singulink.com/Docs/Singulink.Threading/api/Singulink.Threading.html) for the full API.
