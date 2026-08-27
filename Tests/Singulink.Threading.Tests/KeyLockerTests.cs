using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

[PrefixTestClass]
public class KeyLockerTests
{
    private const string Key = "key";

    [TestMethod]
    public void Lock_AcquireAndDispose_RemovesEntry()
    {
        var locker = new KeyLocker<string>();

        var keyLock = locker.Lock(Key);
        locker.IsLocked(Key).ShouldBeTrue();

        keyLock.Dispose();
        locker.IsLocked(Key).ShouldBeFalse();
    }

    [TestMethod]
    public void Lock_DifferentKeys_AreIndependent()
    {
        var locker = new KeyLocker<string>();

        using var lock1 = locker.Lock("a");
        using var lock2 = locker.Lock("b", 0);

        locker.IsLocked("a").ShouldBeTrue();
        locker.IsLocked("b").ShouldBeTrue();
    }

    [TestMethod]
    public async Task LockAsync_Contention_WaitsForHolderToRelease()
    {
        var locker = new KeyLocker<string>();

        var holder = await locker.LockAsync(Key);
        var waiterTask = Task.Run(async () => (await locker.LockAsync(Key)).Dispose());

        await Task.Delay(100);
        waiterTask.IsCompleted.ShouldBeFalse();

        holder.Dispose();
        await waiterTask.WaitAsync(TimeSpan.FromSeconds(10));

        locker.IsLocked(Key).ShouldBeFalse();
    }

    [TestMethod]
    public void Lock_Timeout_DoesNotLeakEntry()
    {
        var locker = new KeyLocker<string>();

        var holder = locker.Lock(Key);

        Should.Throw<TimeoutException>(() => locker.Lock(Key, 0));
        Should.Throw<TimeoutException>(() => locker.Lock(Key, TimeSpan.Zero));

        // The failed attempts must not leave interested-party references behind: once the holder releases,
        // the entry must be removed and the key must be immediately lockable again.
        holder.Dispose();
        locker.IsLocked(Key).ShouldBeFalse();

        using (locker.Lock(Key, 0))
            locker.IsLocked(Key).ShouldBeTrue();

        locker.IsLocked(Key).ShouldBeFalse();
    }

    [TestMethod]
    public async Task LockAsync_Timeout_DoesNotLeakEntry()
    {
        var locker = new KeyLocker<string>();

        var holder = await locker.LockAsync(Key);

        await Should.ThrowAsync<TimeoutException>(async () => await locker.LockAsync(Key, 0));
        await Should.ThrowAsync<TimeoutException>(async () => await locker.LockAsync(Key, TimeSpan.Zero));

        holder.Dispose();
        locker.IsLocked(Key).ShouldBeFalse();

        (await locker.LockAsync(Key, 0)).Dispose();
        locker.IsLocked(Key).ShouldBeFalse();
    }

    [TestMethod]
    public async Task LockAsync_Cancellation_DoesNotLeakEntry()
    {
        var locker = new KeyLocker<string>();

        var holder = await locker.LockAsync(Key);

        using var cts = new CancellationTokenSource(50);
        await Should.ThrowAsync<OperationCanceledException>(async () => await locker.LockAsync(Key, cts.Token));

        holder.Dispose();
        locker.IsLocked(Key).ShouldBeFalse();
    }

    [TestMethod]
    public void Lock_PreCanceledToken_DoesNotLeakEntry()
    {
        var locker = new KeyLocker<string>();

        var holder = locker.Lock(Key);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Should.Throw<OperationCanceledException>(() => locker.Lock(Key, cts.Token));

        holder.Dispose();
        locker.IsLocked(Key).ShouldBeFalse();
    }

    [TestMethod]
    public async Task LockAsync_AfterFailedWaits_MutualExclusionStillHolds()
    {
        var locker = new KeyLocker<string>();
        var holder = await locker.LockAsync(Key);

        await Should.ThrowAsync<TimeoutException>(async () => await locker.LockAsync(Key, 0));

        // A failed wait must not corrupt the semaphore state: the key must still be exclusively held.
        int counter = 0;

        var waiter = Task.Run(async () =>
        {
            using (await locker.LockAsync(Key))
                Interlocked.Increment(ref counter);
        });

        await Task.Delay(100);
        Volatile.Read(ref counter).ShouldBe(0);

        holder.Dispose();
        await waiter.WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref counter).ShouldBe(1);
        locker.IsLocked(Key).ShouldBeFalse();
    }

    [TestMethod]
    public async Task LockAsync_ManyConcurrentAcquirers_SerializeAndCleanUp()
    {
        var locker = new KeyLocker<string>();
        int active = 0;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            using (await locker.LockAsync(Key))
            {
                Interlocked.Increment(ref active).ShouldBe(1);
                await Task.Delay(1);
                Interlocked.Decrement(ref active);
            }
        })));

        locker.IsLocked(Key).ShouldBeFalse();
    }
}
