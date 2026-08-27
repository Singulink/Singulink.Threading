using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

[PrefixTestClass]
public class UpgradeableReadGuardTests
{
    [TestMethod]
    public void EnterUpgradedWriteGuard_UpgradesAndDowngradesBack()
    {
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterUpgradeableReadGuard();

        guard.IsUpgraded.ShouldBeFalse();
        guard.IsDowngraded.ShouldBeFalse();

        var writeGuard = guard.EnterUpgradedWriteGuard();

        writeGuard.IsEntered.ShouldBeTrue();
        guard.IsUpgraded.ShouldBeTrue();
        rwl.IsWriteLockHeld.ShouldBeTrue();
        rwl.IsUpgradeableReadLockHeld.ShouldBeTrue();

        writeGuard.Dispose();

        // Disposing the write guard downgrades back to upgradeable read mode.
        guard.IsUpgraded.ShouldBeFalse();
        rwl.IsWriteLockHeld.ShouldBeFalse();
        rwl.IsUpgradeableReadLockHeld.ShouldBeTrue();

        guard.Dispose();
        rwl.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void EnterUpgradedWriteGuard_CanUpgradeRepeatedly()
    {
        using var rwl = new ReadWriteLock();
        using var guard = rwl.EnterUpgradeableReadGuard();

        for (int i = 0; i < 3; i++)
        {
            using (guard.EnterUpgradedWriteGuard())
                rwl.IsWriteLockHeld.ShouldBeTrue();

            rwl.IsWriteLockHeld.ShouldBeFalse();
            rwl.IsUpgradeableReadLockHeld.ShouldBeTrue();
        }
    }

    [TestMethod]
    public void EnterUpgradedWriteGuard_WhileUpgraded_Throws()
    {
        using var rwl = new ReadWriteLock();
        using var guard = rwl.EnterUpgradeableReadGuard();

        using (guard.EnterUpgradedWriteGuard())
            Should.Throw<InvalidOperationException>(() => guard.EnterUpgradedWriteGuard());
    }

    [TestMethod]
    public void EnterUpgradedWriteGuard_AbandonedGuard_IsReleasedByParentDispose()
    {
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterUpgradeableReadGuard();

        _ = guard.EnterUpgradedWriteGuard(); // abandoned without disposal

        rwl.IsWriteLockHeld.ShouldBeTrue();

        guard.Dispose();

        rwl.IsWriteLockHeld.ShouldBeFalse();
        rwl.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void TryEnterUpgradedWriteGuard_Uncontended_Enters()
    {
        using var rwl = new ReadWriteLock();
        using var guard = rwl.EnterUpgradeableReadGuard();

        using (var writeGuard = guard.TryEnterUpgradedWriteGuard(0))
        {
            writeGuard.IsEntered.ShouldBeTrue();
            rwl.IsWriteLockHeld.ShouldBeTrue();
        }

        rwl.IsWriteLockHeld.ShouldBeFalse();
        rwl.IsUpgradeableReadLockHeld.ShouldBeTrue();
    }

    [TestMethod]
    public void TryEnterUpgradedWriteGuard_BlockedByReader_ReturnsNotEnteredGuard()
    {
        using var rwl = new ReadWriteLock();
        using var readerEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var reader = Task.Run(() =>
        {
            using (rwl.EnterReadGuard())
            {
                readerEntered.Set();
                release.Wait();
            }
        });

        readerEntered.Wait();

        using var guard = rwl.EnterUpgradeableReadGuard();

        using (var writeGuard = guard.TryEnterUpgradedWriteGuard(TimeSpan.Zero))
            writeGuard.IsEntered.ShouldBeFalse();

        // The failed upgrade must leave the upgradeable session intact.
        guard.IsUpgraded.ShouldBeFalse();
        rwl.IsUpgradeableReadLockHeld.ShouldBeTrue();

        release.Set();
        reader.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        using (var writeGuard = guard.TryEnterUpgradedWriteGuard(TimeSpan.FromSeconds(10)))
            writeGuard.IsEntered.ShouldBeTrue();
    }

    [TestMethod]
    public void DowngradeToReadLock_TransitionsToReadLock()
    {
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterUpgradeableReadGuard();

        guard.DowngradeToReadLock();

        guard.IsDowngraded.ShouldBeTrue();
        guard.IsUpgraded.ShouldBeFalse();
        rwl.IsReadLockHeld.ShouldBeTrue();
        rwl.IsUpgradeableReadLockHeld.ShouldBeFalse();

        guard.Dispose();

        rwl.IsReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void DowngradeToReadLock_AllowsAnotherUpgradeableReader()
    {
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterUpgradeableReadGuard();

        guard.DowngradeToReadLock();

        // Only one thread may hold upgradeable mode; after downgrading, another thread can enter it while
        // this thread retains read access. A dedicated thread is used because block-waiting on an
        // unstarted Task can inline it onto this thread, which holds the read lock (thread-affine).
        bool otherEntered = false;

        var otherUpgrader = new Thread(() =>
        {
            using var otherGuard = rwl.TryEnterUpgradeableReadGuard(TimeSpan.FromSeconds(10));
            otherEntered = otherGuard.IsEntered;
        });

        otherUpgrader.Start();
        otherUpgrader.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        otherEntered.ShouldBeTrue();
        rwl.IsReadLockHeld.ShouldBeTrue();

        guard.Dispose();
    }

    [TestMethod]
    public void DowngradeToReadLock_InvalidStates_Throw()
    {
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterUpgradeableReadGuard();

        // While upgraded.
        var writeGuard = guard.EnterUpgradedWriteGuard();
        Should.Throw<InvalidOperationException>(() => guard.DowngradeToReadLock());
        writeGuard.Dispose();

        // Twice.
        guard.DowngradeToReadLock();
        Should.Throw<InvalidOperationException>(() => guard.DowngradeToReadLock());

        // Upgrading after downgrading.
        Should.Throw<InvalidOperationException>(() => guard.EnterUpgradedWriteGuard());
        Should.Throw<InvalidOperationException>(() => guard.TryEnterUpgradedWriteGuard(0));

        guard.Dispose();
    }

    [TestMethod]
    public void NotEnteredGuard_ActiveMembersThrow()
    {
        using var rwl = new ReadWriteLock();
        using var writerEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            using (rwl.EnterWriteGuard())
            {
                writerEntered.Set();
                release.Wait();
            }
        });

        writerEntered.Wait();

        using (var guard = rwl.TryEnterUpgradeableReadGuard(0))
        {
            guard.IsEntered.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => guard.EnterUpgradedWriteGuard());
            Should.Throw<InvalidOperationException>(() => guard.TryEnterUpgradedWriteGuard(0));
            Should.Throw<InvalidOperationException>(() => guard.DowngradeToReadLock());
        }

        release.Set();
        writer.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }

    [TestMethod]
    public void DisposedGuard_ActiveMembersThrow()
    {
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterUpgradeableReadGuard();
        guard.Dispose();

        Should.Throw<ObjectDisposedException>(() => guard.EnterUpgradedWriteGuard());
        Should.Throw<ObjectDisposedException>(() => guard.TryEnterUpgradedWriteGuard(0));
        Should.Throw<ObjectDisposedException>(() => guard.DowngradeToReadLock());
    }
}
