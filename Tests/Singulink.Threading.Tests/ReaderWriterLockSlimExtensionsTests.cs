using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

[PrefixTestClass]
public class ReaderWriterLockSlimExtensionsTests
{
    [TestMethod]
    public void ReadGuard_EntersAndExitsReadLock()
    {
        using var rwLock = new ReaderWriterLockSlim();
        var guard = rwLock.EnterReadGuard();

        guard.IsDefault.ShouldBeFalse();
        guard.IsDisposed.ShouldBeFalse();
        rwLock.IsReadLockHeld.ShouldBeTrue();

        guard.Dispose();

        guard.IsDisposed.ShouldBeTrue();
        rwLock.IsReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void WriteGuard_EntersAndExitsWriteLock()
    {
        using var rwLock = new ReaderWriterLockSlim();
        var guard = rwLock.EnterWriteGuard();

        rwLock.IsWriteLockHeld.ShouldBeTrue();

        guard.Dispose();

        rwLock.IsWriteLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void UpgradeableReadGuard_EntersAndExitsUpgradeableLock()
    {
        using var rwLock = new ReaderWriterLockSlim();
        var guard = rwLock.EnterUpgradeableReadGuard();

        guard.IsUpgraded.ShouldBeFalse();
        rwLock.IsUpgradeableReadLockHeld.ShouldBeTrue();
        rwLock.IsWriteLockHeld.ShouldBeFalse();

        guard.Dispose();

        rwLock.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void UpgradeableReadGuard_Upgrade_EntersWriteLockAndReleasesBothOnDispose()
    {
        using var rwLock = new ReaderWriterLockSlim();
        var guard = rwLock.EnterUpgradeableReadGuard();

        guard.UpgradeToWriteGuard();

        guard.IsUpgraded.ShouldBeTrue();
        rwLock.IsWriteLockHeld.ShouldBeTrue();

        guard.Dispose();

        rwLock.IsWriteLockHeld.ShouldBeFalse();
        rwLock.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void UpgradeableReadGuard_DoubleUpgrade_Throws()
    {
        using var rwLock = new ReaderWriterLockSlim();
        var guard = rwLock.EnterUpgradeableReadGuard();

        guard.UpgradeToWriteGuard();
        Should.Throw<InvalidOperationException>(() => guard.UpgradeToWriteGuard());

        guard.Dispose();
    }

    [TestMethod]
    public void UpgradeableReadGuard_UpgradeAfterDispose_Throws()
    {
        using var rwLock = new ReaderWriterLockSlim();
        var guard = rwLock.EnterUpgradeableReadGuard();
        guard.Dispose();

        Should.Throw<ObjectDisposedException>(() => guard.UpgradeToWriteGuard());
    }

    [TestMethod]
    public void Guards_DoubleDispose_IsNoOp()
    {
        using var rwLock = new ReaderWriterLockSlim();

        var readGuard = rwLock.EnterReadGuard();
        readGuard.Dispose();
        readGuard.Dispose();
        rwLock.IsReadLockHeld.ShouldBeFalse();

        var writeGuard = rwLock.EnterWriteGuard();
        writeGuard.Dispose();
        writeGuard.Dispose();
        rwLock.IsWriteLockHeld.ShouldBeFalse();

        var upgradeableGuard = rwLock.EnterUpgradeableReadGuard();
        upgradeableGuard.Dispose();
        upgradeableGuard.Dispose();
        rwLock.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void Guards_Default_AreDefaultAndThrowNotInitialized()
    {
        var readGuard = default(ReadGuard);
        readGuard.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => readGuard.Dispose());

        var writeGuard = default(WriteGuard);
        writeGuard.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => writeGuard.Dispose());

        var upgradeableGuard = default(UpgradeableReadGuard);
        upgradeableGuard.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => upgradeableGuard.Dispose());
        Should.Throw<InvalidOperationException>(() => upgradeableGuard.UpgradeToWriteGuard());
    }

    [TestMethod]
    public void WriteGuard_BlocksOtherWriters()
    {
        // ReaderWriterLockSlim is thread-affine, so the guard must be entered and disposed on the same
        // thread — waits here are synchronous to keep the test method on its original thread.
        using var rwLock = new ReaderWriterLockSlim();
        var guard = rwLock.EnterWriteGuard();

        var waiterTask = Task.Run(() =>
        {
            using (rwLock.EnterWriteGuard()) { }
        });

        waiterTask.Wait(100).ShouldBeFalse();

        guard.Dispose();
        waiterTask.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }
}
