using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

/// <summary>
/// Covers the owner-thread enforcement on <see cref="ReadWriteLock"/> guards: operations on an entered
/// guard from a different thread than the one that entered the lock throw immediately instead of
/// corrupting lock state or deadlocking.
/// </summary>
[PrefixTestClass]
public class GuardThreadAffinityTests
{
    [TestMethod]
    public void EnteredGuards_DisposeOnOtherThread_ThrowsAndGuardRemainsUsable()
    {
        using var rwl = new ReadWriteLock();

        var readGuard = rwl.EnterReadGuard();
        RunOnOtherThread(() => readGuard.Dispose()).ShouldBeOfType<InvalidOperationException>();

        // The failed attempt must not have mutated the guard - the owner thread can still dispose it.
        readGuard.IsDisposed.ShouldBeFalse();
        readGuard.Dispose();
        rwl.IsReadLockHeld.ShouldBeFalse();

        var writeGuard = rwl.EnterWriteGuard();
        RunOnOtherThread(() => writeGuard.Dispose()).ShouldBeOfType<InvalidOperationException>();
        writeGuard.Dispose();
        rwl.IsWriteLockHeld.ShouldBeFalse();

        var upgradeableGuard = rwl.EnterUpgradeableReadGuard();
        RunOnOtherThread(() => upgradeableGuard.Dispose()).ShouldBeOfType<InvalidOperationException>();
        upgradeableGuard.Dispose();
        rwl.IsUpgradeableReadLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void UpgradeableGuard_OperationsOnOtherThread_Throw()
    {
        using var rwl = new ReadWriteLock();
        using var guard = rwl.EnterUpgradeableReadGuard();

        RunOnOtherThread(() => guard.EnterUpgradedWriteGuard()).ShouldBeOfType<InvalidOperationException>();
        RunOnOtherThread(() => guard.TryEnterUpgradedWriteGuard(0)).ShouldBeOfType<InvalidOperationException>();
        RunOnOtherThread(() => guard.DowngradeToReadLock()).ShouldBeOfType<InvalidOperationException>();

        // The guard must still be fully functional on the owner thread.
        using (guard.EnterUpgradedWriteGuard())
            rwl.IsWriteLockHeld.ShouldBeTrue();

        guard.DowngradeToReadLock();
        rwl.IsReadLockHeld.ShouldBeTrue();
    }

    [TestMethod]
    public void UpgradedWriteGuard_DisposeOnOtherThread_Throws()
    {
        using var rwl = new ReadWriteLock();
        using var guard = rwl.EnterUpgradeableReadGuard();

        var writeGuard = guard.EnterUpgradedWriteGuard();

        RunOnOtherThread(() => writeGuard.Dispose()).ShouldBeOfType<InvalidOperationException>();

        rwl.IsWriteLockHeld.ShouldBeTrue();
        writeGuard.Dispose();
        rwl.IsWriteLockHeld.ShouldBeFalse();
    }

    [TestMethod]
    public void NotEnteredGuards_DisposeOnOtherThread_IsNoOp()
    {
        using var rwl = new ReadWriteLock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = new Thread(() =>
        {
            using (rwl.EnterWriteGuard())
            {
                entered.Set();
                release.Wait();
            }
        });

        holder.Start();
        entered.Wait();

        // Not-entered guards touch no lock state, so cross-thread disposal is exempt from the check.
        var guard = rwl.TryEnterReadGuard(0);
        guard.IsEntered.ShouldBeFalse();

        RunOnOtherThread(() => guard.Dispose()).ShouldBeNull();
        guard.IsDisposed.ShouldBeTrue();

        release.Set();
        holder.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }

    private static Exception? RunOnOtherThread(Action action)
    {
        Exception? exception = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        return exception;
    }
}
