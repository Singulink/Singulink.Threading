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
}
