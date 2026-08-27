using System.ComponentModel;

namespace Singulink.Threading;

/// <summary>
/// Provides legacy extension methods on <see cref="ReaderWriterLockSlim"/> to enter disposable guards.
/// </summary>
/// <remarks>
/// This class is retained only for binary compatibility with assemblies compiled against v2.x. Use
/// <see cref="ReadWriteLock"/> instead: it prevents guard-managed lock modes from being manipulated behind
/// the guards' backs and guarantees <see cref="LockRecursionPolicy.NoRecursion"/> semantics, which the
/// guards assume.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ReaderWriterLockSlimExtensions
{
    private const string ObsoleteMessage = "Use ReadWriteLock instead of guard extensions on ReaderWriterLockSlim. " +
        "This member is retained for binary compatibility with v2.x and will be removed in a future version.";

    /// <summary>
    /// Enters into a read lock and returns a guard that releases the lock upon disposal.
    /// </summary>
    [Obsolete(ObsoleteMessage)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static ReadGuard EnterReadGuard(this ReaderWriterLockSlim readerWriterLock)
    {
        readerWriterLock.EnterReadLock();
        return new ReadGuard(readerWriterLock, entered: true);
    }

    /// <summary>
    /// Enters into a write lock and returns a guard that releases the lock upon disposal.
    /// </summary>
    [Obsolete(ObsoleteMessage)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static WriteGuard EnterWriteGuard(this ReaderWriterLockSlim readerWriterLock)
    {
        readerWriterLock.EnterWriteLock();
        return new WriteGuard(readerWriterLock, entered: true);
    }

    /// <summary>
    /// Enters into an upgradeable read lock and returns a guard that releases the lock upon disposal.
    /// </summary>
    [Obsolete(ObsoleteMessage)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static UpgradeableReadGuard EnterUpgradeableReadGuard(this ReaderWriterLockSlim readerWriterLock)
    {
        readerWriterLock.EnterUpgradeableReadLock();
        return new UpgradeableReadGuard(readerWriterLock, entered: true);
    }
}
