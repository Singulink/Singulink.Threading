# Singulink.Threading Version History

## Version 3.1

- New `LeafLock`, a non-reentrant lock for short critical sections with `EnsureHeld` and `EnsureNotHeld` checks, entered through an allocation-free `LeafLockGuard`
- New `RetryTimer` to retry an operation after a delay until a time limit passes, measured with a `TimeProvider`

## Version 3.0

See the [API documentation](https://www.singulink.com/Docs/Singulink.Threading/api/Singulink.Threading.html) for the full API.
