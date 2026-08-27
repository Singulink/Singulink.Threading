#pragma warning disable CS0618 // Type or member is obsolete - these tests cover the legacy binary-compat surface.

using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

/// <summary>
/// Covers the obsolete members retained for binary compatibility with assemblies compiled against v2.x.
/// </summary>
[PrefixTestClass]
public class LegacyCompatTests
{
    [TestMethod]
    public void Extensions_EnterGuards_EnterAndExit()
    {
        using var rwLock = new ReaderWriterLockSlim();

        using (var guard = rwLock.EnterReadGuard())
        {
            guard.IsEntered.ShouldBeTrue();
            rwLock.IsReadLockHeld.ShouldBeTrue();
        }

        rwLock.IsReadLockHeld.ShouldBeFalse();

        using (var guard = rwLock.EnterWriteGuard())
        {
            guard.IsEntered.ShouldBeTrue();
            rwLock.IsWriteLockHeld.ShouldBeTrue();
        }

        rwLock.IsWriteLockHeld.ShouldBeFalse();

        using (var guard = rwLock.EnterUpgradeableReadGuard())
        {
            guard.IsEntered.ShouldBeTrue();
            rwLock.IsUpgradeableReadLockHeld.ShouldBeTrue();
        }

        rwLock.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void UpgradeToWriteGuard_UpgradesAndReleasesOnGuardDispose()
    {
        // The v2.x pattern: upgrade with no returned guard; disposing the upgradeable guard releases both
        // the write lock and the upgradeable read lock.
        using var rwLock = new ReaderWriterLockSlim();
        var guard = rwLock.EnterUpgradeableReadGuard();

        guard.UpgradeToWriteGuard();

        guard.IsUpgraded.ShouldBeTrue();
        rwLock.IsWriteLockHeld.ShouldBeTrue();

        Should.Throw<InvalidOperationException>(() => guard.UpgradeToWriteGuard());

        guard.Dispose();

        rwLock.IsWriteLockHeld.ShouldBeFalse();
        rwLock.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }
}
