using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

[PrefixTestClass]
public class ReadWriteLockTests
{
    [TestMethod]
    public void EnterReadGuard_EntersAndExitsReadLock()
    {
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterReadGuard();

        guard.IsDefault.ShouldBeFalse();
        guard.IsEntered.ShouldBeTrue();
        guard.IsDisposed.ShouldBeFalse();
        rwl.IsReadLockHeld.ShouldBeTrue();

        guard.Dispose();

        guard.IsDisposed.ShouldBeTrue();
        rwl.IsReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void EnterWriteGuard_EntersAndExitsWriteLock()
    {
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterWriteGuard();

        guard.IsEntered.ShouldBeTrue();
        rwl.IsWriteLockHeld.ShouldBeTrue();

        guard.Dispose();

        rwl.IsWriteLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void EnterUpgradeableReadGuard_EntersAndExitsUpgradeableLock()
    {
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterUpgradeableReadGuard();

        guard.IsEntered.ShouldBeTrue();
        rwl.IsUpgradeableReadLockHeld.ShouldBeTrue();

        guard.Dispose();

        rwl.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void TryEnterGuards_Uncontended_Enter()
    {
        using var rwl = new ReadWriteLock();

        using (var guard = rwl.TryEnterReadGuard(0))
        {
            guard.IsEntered.ShouldBeTrue();
            rwl.IsReadLockHeld.ShouldBeTrue();
        }

        using (var guard = rwl.TryEnterWriteGuard(TimeSpan.Zero))
        {
            guard.IsEntered.ShouldBeTrue();
            rwl.IsWriteLockHeld.ShouldBeTrue();
        }

        using (var guard = rwl.TryEnterUpgradeableReadGuard(0))
        {
            guard.IsEntered.ShouldBeTrue();
            rwl.IsUpgradeableReadLockHeld.ShouldBeTrue();
        }

        rwl.IsReadLockHeld.ShouldBeFalse();
        rwl.IsWriteLockHeld.ShouldBeFalse();
        rwl.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void TryEnterGuards_Contended_ReturnNotEnteredGuards()
    {
        using var rwl = new ReadWriteLock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = Task.Run(() =>
        {
            using (rwl.EnterWriteGuard())
            {
                entered.Set();
                release.Wait();
            }
        });

        entered.Wait();

        // A not-entered guard must report IsEntered = false and dispose as a safe no-op.
        using (var readGuard = rwl.TryEnterReadGuard(0))
        using (var writeGuard = rwl.TryEnterWriteGuard(0))
        using (var upgradeableGuard = rwl.TryEnterUpgradeableReadGuard(TimeSpan.Zero))
        {
            readGuard.IsEntered.ShouldBeFalse();
            writeGuard.IsEntered.ShouldBeFalse();
            upgradeableGuard.IsEntered.ShouldBeFalse();
        }

        release.Set();
        holder.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        // The failed attempts and their disposal must not have perturbed the lock state.
        using (var guard = rwl.TryEnterWriteGuard(0))
            guard.IsEntered.ShouldBeTrue();
    }

    [TestMethod]
    public void WriteGuard_BlocksOtherWriters()
    {
        // A dedicated thread is used for the competing writer because block-waiting on an unstarted Task
        // can inline it onto this thread, which already holds the write lock (thread-affine).
        using var rwl = new ReadWriteLock();
        var guard = rwl.EnterWriteGuard();

        var waiter = new Thread(() =>
        {
            using (rwl.EnterWriteGuard()) { }
        });

        waiter.Start();
        waiter.Join(100).ShouldBeFalse();

        guard.Dispose();
        waiter.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }

    [TestMethod]
    public void Guards_DoubleDispose_IsNoOp()
    {
        using var rwl = new ReadWriteLock();

        var readGuard = rwl.EnterReadGuard();
        readGuard.Dispose();
        readGuard.Dispose();
        rwl.IsReadLockHeld.ShouldBeFalse();

        var writeGuard = rwl.EnterWriteGuard();
        writeGuard.Dispose();
        writeGuard.Dispose();
        rwl.IsWriteLockHeld.ShouldBeFalse();

        var upgradeableGuard = rwl.EnterUpgradeableReadGuard();
        upgradeableGuard.Dispose();
        upgradeableGuard.Dispose();
        rwl.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void Guards_Default_AreDefaultAndThrowNotInitialized()
    {
        var readGuard = default(ReadGuard);
        readGuard.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => _ = readGuard.IsEntered);
        Should.Throw<InvalidOperationException>(() => readGuard.Dispose());

        var writeGuard = default(WriteGuard);
        writeGuard.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => _ = writeGuard.IsEntered);
        Should.Throw<InvalidOperationException>(() => writeGuard.Dispose());

        var upgradeableGuard = default(UpgradeableReadGuard);
        upgradeableGuard.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => _ = upgradeableGuard.IsEntered);
        Should.Throw<InvalidOperationException>(() => upgradeableGuard.Dispose());
        Should.Throw<InvalidOperationException>(() => upgradeableGuard.EnterUpgradedWriteGuard());
        Should.Throw<InvalidOperationException>(() => upgradeableGuard.DowngradeToReadLock());
    }

    [TestMethod]
    public void MultipleReaders_AreAllowedConcurrently()
    {
        using var rwl = new ReadWriteLock();
        using var otherEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var otherReader = Task.Run(() =>
        {
            using (rwl.EnterReadGuard())
            {
                otherEntered.Set();
                release.Wait();
            }
        });

        otherEntered.Wait();

        using (var guard = rwl.TryEnterReadGuard(0))
            guard.IsEntered.ShouldBeTrue();

        release.Set();
        otherReader.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }
}
