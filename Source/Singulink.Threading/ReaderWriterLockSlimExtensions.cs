namespace Singulink.Threading;

/// <summary>
/// Provides extension methods on <see cref="ReaderWriterLockSlim"/> to enter disposable guards.
/// </summary>
/// <remarks>
/// <para>This class greatly simplifies properly releasing locks - simply wrap the call that enters the guard in a using statement and it will automatically
/// release when existing the using block.</para>
/// </remarks>
public static class ReaderWriterLockSlimExtensions
{
    /// <summary>
    /// Enters into a read lock and returns a guard that releases the lock upon disposal.
    /// </summary>
    public static ReadGuard EnterReadGuard(this ReaderWriterLockSlim readerWriterLock) => new(readerWriterLock);

    /// <summary>
    /// Enters into a write lock and returns a guard that releases the lock upon disposal.
    /// </summary>
    public static WriteGuard EnterWriteGuard(this ReaderWriterLockSlim readerWriterLock) => new(readerWriterLock);

    /// <summary>
    /// Enters into an upgradeable read lock and returns a guard that releases the lock upon disposal.
    /// </summary>
    public static UpgradeableReadGuard EnterUpgradeableReadGuard(this ReaderWriterLockSlim readerWriterLock) => new(readerWriterLock);
}
