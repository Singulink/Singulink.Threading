using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Singulink.Threading.Utilities;

internal static class Throw
{
    [StackTraceHidden]
    public static void NotInitializedIf(bool condition)
    {
        if (condition)
        {
            [DoesNotReturn]
            static void Throw() => throw new InvalidOperationException("Instance has not been initialized.");
            Throw();
        }
    }

    [StackTraceHidden]
    public static void WrongGuardThreadIf(bool condition)
    {
        if (condition)
        {
            [DoesNotReturn]
            static void Throw() => throw new InvalidOperationException(
                "Guard operations must be performed on the thread that entered the lock. " +
                "This commonly occurs when a guard is used across an 'await' boundary.");
            Throw();
        }
    }
}
