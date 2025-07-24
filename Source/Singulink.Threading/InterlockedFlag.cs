using Microsoft.CodeAnalysis;

namespace Singulink.Threading;

/// <summary>
/// Provides a lock-free atomic flag that can be set and cleared using interlocked operations.
/// </summary>
[NonCopyable]
public struct InterlockedFlag
{
    private int _flag;

    /// <summary>
    /// Initializes a new instance of the <see cref="InterlockedFlag"/> struct with the specified initial state.
    /// </summary>
    public InterlockedFlag(bool isSet) => _flag = isSet ? 1 : 0;

    /// <summary>
    /// Gets a value indicating whether the flag has been set by performing a volatile read to get the most up to date value.
    /// </summary>
    public bool IsSet => Volatile.Read(ref _flag) == 1;

    /// <summary>
    /// Tries to set the flag and returns true if the operation was successful, or false if the flag was already set.
    /// </summary>
    public bool TrySet() => Interlocked.CompareExchange(ref _flag, 1, 0) == 0;

    /// <summary>
    /// Tries to clear the flag and returns true if the operation was successful, or false if the flag was already cleared.
    /// </summary>
    public bool TryClear() => Interlocked.CompareExchange(ref _flag, 0, 1) == 1;
}
