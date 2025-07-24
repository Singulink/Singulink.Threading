using System.Diagnostics.CodeAnalysis;

namespace Singulink.Threading.Utilities;

internal static class Throw
{
    [return: NotNull]
    public static T NotInitializedIfNull<T>([NotNull] T value)
    {
        return value ?? throw new InvalidOperationException("Instance has not been properly initialized.");
    }
}
