using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

[PrefixTestClass]
public class LeafLockTests
{
    [TestMethod]
    public void EnterGuard_EntersAndLeavesLock()
    {
        var leafLock = new LeafLock();
        leafLock.IsHeldByCurrentThread.ShouldBeFalse();

        var guard = leafLock.EnterGuard();

        guard.IsDefault.ShouldBeFalse();
        guard.IsDisposed.ShouldBeFalse();
        leafLock.IsHeldByCurrentThread.ShouldBeTrue();

        guard.Dispose();

        guard.IsDisposed.ShouldBeTrue();
        leafLock.IsHeldByCurrentThread.ShouldBeFalse();
    }

    [TestMethod]
    public void EnterGuard_WhileHeld_ThrowsAndLockStaysHeld()
    {
        var leafLock = new LeafLock();

        using (leafLock.EnterGuard())
        {
            Should.Throw<LockRecursionException>(() => { using var unexpected = leafLock.EnterGuard(); });
            leafLock.IsHeldByCurrentThread.ShouldBeTrue();
        }

        leafLock.IsHeldByCurrentThread.ShouldBeFalse();

        // The lock is usable again after the failed entry.
        using (leafLock.EnterGuard())
            leafLock.IsHeldByCurrentThread.ShouldBeTrue();
    }

    [TestMethod]
    public void EnsureHeld_ThrowsOnlyWhenNotHeld()
    {
        var leafLock = new LeafLock();

        Should.Throw<InvalidOperationException>(leafLock.EnsureHeld);

        using (leafLock.EnterGuard())
            leafLock.EnsureHeld();
    }

    [TestMethod]
    public void EnsureNotHeld_ThrowsOnlyWhenHeld()
    {
        var leafLock = new LeafLock();

        leafLock.EnsureNotHeld();

        using (leafLock.EnterGuard())
            Should.Throw<InvalidOperationException>(leafLock.EnsureNotHeld);
    }

    [TestMethod]
    public void Checks_OnOtherThreadWhileHeld_SeeLockAsNotHeld()
    {
        var leafLock = new LeafLock();

        using (leafLock.EnterGuard())
        {
            RunOnOtherThread(() => {
                leafLock.IsHeldByCurrentThread.ShouldBeFalse();
                leafLock.EnsureNotHeld();
                Should.Throw<InvalidOperationException>(leafLock.EnsureHeld);
            }).ShouldBeNull();
        }
    }

    [TestMethod]
    public void Dispose_Twice_IsNoOp()
    {
        var leafLock = new LeafLock();
        var guard = leafLock.EnterGuard();

        guard.Dispose();
        guard.Dispose();

        guard.IsDisposed.ShouldBeTrue();
        leafLock.IsHeldByCurrentThread.ShouldBeFalse();
    }

    [TestMethod]
    public void Dispose_OnOtherThread_ThrowsAndGuardRemainsUsable()
    {
        var leafLock = new LeafLock();
        var guard = leafLock.EnterGuard();

        RunOnOtherThread(() => guard.Dispose()).ShouldBeOfType<InvalidOperationException>();

        guard.IsDisposed.ShouldBeFalse();
        leafLock.IsHeldByCurrentThread.ShouldBeTrue();

        guard.Dispose();
        leafLock.IsHeldByCurrentThread.ShouldBeFalse();
    }

    [TestMethod]
    public void DefaultGuard_Throws()
    {
        LeafLockGuard guard = default;

        guard.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => guard.IsDisposed);
        Should.Throw<InvalidOperationException>(() => guard.Dispose());
    }

    [TestMethod]
    public void EnterGuard_Contended_WaitsForHolder()
    {
        var leafLock = new LeafLock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = new Thread(() => {
            using (leafLock.EnterGuard())
            {
                entered.Set();
                release.Wait();
            }
        });

        holder.Start();
        entered.Wait();

        bool waiterEntered = false;

        var waiter = new Thread(() => {
            using (leafLock.EnterGuard())
                Volatile.Write(ref waiterEntered, true);
        });

        waiter.Start();
        waiter.Join(TimeSpan.FromMilliseconds(100)).ShouldBeFalse();
        Volatile.Read(ref waiterEntered).ShouldBeFalse();

        release.Set();
        holder.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        waiter.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        Volatile.Read(ref waiterEntered).ShouldBeTrue();
    }

    [TestMethod]
    public void EnterGuard_ManyThreads_ProvidesMutualExclusion()
    {
        const int ThreadCount = 8;
        const int Iterations = 20_000;

        var leafLock = new LeafLock();
        int inside = 0;
        int overlaps = 0;
        long counter = 0;

        using var start = new ManualResetEventSlim();

        var threads = Enumerable.Range(0, ThreadCount).Select(_ => new Thread(() => {
            start.Wait();

            for (int i = 0; i < Iterations; i++)
            {
                using (leafLock.EnterGuard())
                {
                    if (Interlocked.Increment(ref inside) != 1)
                        Interlocked.Increment(ref overlaps);

                    // Not atomic on purpose: the lock alone keeps the count right.
                    counter++;

                    Interlocked.Decrement(ref inside);
                }
            }
        })).ToList();

        foreach (var thread in threads)
            thread.Start();

        start.Set();

        foreach (var thread in threads)
            thread.Join(TimeSpan.FromSeconds(60)).ShouldBeTrue();

        overlaps.ShouldBe(0);
        counter.ShouldBe(ThreadCount * Iterations);
        leafLock.IsHeldByCurrentThread.ShouldBeFalse();
    }

    private static Exception? RunOnOtherThread(Action action)
    {
        Exception? exception = null;

        var thread = new Thread(() => {
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
