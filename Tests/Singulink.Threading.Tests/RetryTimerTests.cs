using System.Diagnostics;
using PrefixClassName.MsTest;
using Shouldly;

namespace Singulink.Threading.Tests;

[PrefixTestClass]
public class RetryTimerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(50);

    [TestMethod]
    public void Start_ChecksArguments()
    {
        var time = new ManualTimeProvider();

        Should.Throw<ArgumentOutOfRangeException>(() => RetryTimer.Start(TimeSpan.FromTicks(-1), Delay, time)).ParamName.ShouldBe("timeLimit");
        Should.Throw<ArgumentOutOfRangeException>(() => RetryTimer.Start(TimeSpan.FromSeconds(-2), Delay, time)).ParamName.ShouldBe("timeLimit");
        Should.Throw<ArgumentOutOfRangeException>(() => RetryTimer.Start(TimeSpan.FromSeconds(1), TimeSpan.Zero, time)).ParamName.ShouldBe("delay");
        Should.Throw<ArgumentOutOfRangeException>(() => RetryTimer.Start(TimeSpan.FromSeconds(1), TimeSpan.FromDays(25), time)).ParamName.ShouldBe("delay");
        Should.Throw<ArgumentNullException>(() => RetryTimer.Start(TimeSpan.FromSeconds(1), Delay, null!)).ParamName.ShouldBe("timeProvider");

        RetryTimer.Start(TimeSpan.Zero, Delay, time).IsDefault.ShouldBeFalse();
        RetryTimer.Start(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(int.MaxValue), time).IsDefault.ShouldBeFalse();
        RetryTimer.Start(TimeSpan.FromSeconds(1), Delay).IsDefault.ShouldBeFalse();
    }

    [TestMethod]
    public void Default_Throws()
    {
        RetryTimer retry = default;

        retry.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => retry.Elapsed);
        Should.Throw<InvalidOperationException>(() => retry.CanRetry);
        Should.Throw<InvalidOperationException>(() => retry.TryWait());
        Should.Throw<InvalidOperationException>(() => retry.TryWaitAsync());
        Should.Throw<InvalidOperationException>(() => retry.Wait());
        Should.Throw<InvalidOperationException>(() => retry.WaitAsync());
    }

    [TestMethod]
    public void Elapsed_FollowsTimeProvider()
    {
        var time = new ManualTimeProvider();
        time.Advance(TimeSpan.FromHours(1));

        var retry = RetryTimer.Start(TimeSpan.FromSeconds(1), Delay, time);
        retry.Elapsed.ShouldBe(TimeSpan.Zero);

        time.Advance(TimeSpan.FromMilliseconds(300));
        retry.Elapsed.ShouldBe(TimeSpan.FromMilliseconds(300));
    }

    [TestMethod]
    public async Task ZeroTimeLimit_AllowsNoRetry()
    {
        var time = new ManualTimeProvider();
        var retry = RetryTimer.Start(TimeSpan.Zero, Delay, time);

        retry.CanRetry.ShouldBeFalse();
        retry.TryWait().ShouldBeFalse();

        var task = retry.TryWaitAsync();
        task.IsCompletedSuccessfully.ShouldBeTrue();
        (await task).ShouldBeFalse();

        retry.WaitAsync().IsCompletedSuccessfully.ShouldBeTrue();
        retry.Wait();

        time.PendingTimerCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task TryWaitAsync_WaitsForDelayUntilTimeLimit()
    {
        var time = new ManualTimeProvider();
        var retry = RetryTimer.Start(TimeSpan.FromMilliseconds(120), Delay, time);

        // Two full delays fit in the limit.
        for (int i = 0; i < 2; i++)
        {
            retry.CanRetry.ShouldBeTrue();
            var wait = retry.TryWaitAsync();

            time.Advance(Delay - TimeSpan.FromTicks(1));
            wait.IsCompleted.ShouldBeFalse();

            time.Advance(TimeSpan.FromTicks(1));
            (await wait).ShouldBeTrue();
        }

        // The last wait is cut short to the 20 ms left, so the final attempt is made at the limit.
        retry.CanRetry.ShouldBeTrue();
        var last = retry.TryWaitAsync();

        time.Advance(TimeSpan.FromMilliseconds(20) - TimeSpan.FromTicks(1));
        last.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromTicks(1));
        (await last).ShouldBeTrue();

        retry.Elapsed.ShouldBe(TimeSpan.FromMilliseconds(120));
        retry.CanRetry.ShouldBeFalse();
        (await retry.TryWaitAsync()).ShouldBeFalse();
    }

    [TestMethod]
    public async Task TryWaitAsync_RoundsWaitUpToWholeMilliseconds()
    {
        var time = new ManualTimeProvider();
        var retry = RetryTimer.Start(TimeSpan.FromMilliseconds(50.5), TimeSpan.FromMilliseconds(49.2), time);

        // The delay of 49.2 ms is waited as 50 ms.
        var wait = retry.TryWaitAsync();
        time.Advance(TimeSpan.FromMilliseconds(50) - TimeSpan.FromTicks(1));
        wait.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromTicks(1));
        (await wait).ShouldBeTrue();

        // The 0.5 ms left is waited as 1 ms rather than not at all.
        retry.CanRetry.ShouldBeTrue();
        wait = retry.TryWaitAsync();
        time.Advance(TimeSpan.FromMilliseconds(0.5));
        wait.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromMilliseconds(0.5));
        (await wait).ShouldBeTrue();

        retry.CanRetry.ShouldBeFalse();
        (await retry.TryWaitAsync()).ShouldBeFalse();
    }

    [TestMethod]
    public async Task WaitAsync_WaitsForDelayOrTimeLeft()
    {
        var time = new ManualTimeProvider();
        var retry = RetryTimer.Start(TimeSpan.FromMilliseconds(70), Delay, time);

        var wait = retry.WaitAsync();
        time.Advance(Delay - TimeSpan.FromTicks(1));
        wait.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromTicks(1));
        await wait;

        wait = retry.WaitAsync();
        time.Advance(TimeSpan.FromMilliseconds(20) - TimeSpan.FromTicks(1));
        wait.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromTicks(1));
        await wait;

        retry.CanRetry.ShouldBeFalse();
        retry.WaitAsync().IsCompletedSuccessfully.ShouldBeTrue();
    }

    [TestMethod]
    public async Task InfiniteTimeLimit_AlwaysAllowsRetry()
    {
        var time = new ManualTimeProvider();
        var retry = RetryTimer.Start(Timeout.InfiniteTimeSpan, Delay, time);

        time.Advance(TimeSpan.FromDays(1000));
        retry.CanRetry.ShouldBeTrue();

        var wait = retry.TryWaitAsync();
        time.Advance(Delay);
        wait.IsCompleted.ShouldBeTrue();
        (await wait).ShouldBeTrue();
    }

    [TestMethod]
    public async Task RetryLoop_StopsWhenOperationSucceeds()
    {
        var time = new ManualTimeProvider();
        int attempts = 0;

        var loop = RunAsync();

        while (!loop.IsCompleted)
        {
            time.Advance(Delay);
            await Task.Yield();
        }

        (await loop).ShouldBeTrue();
        attempts.ShouldBe(4);

        async Task<bool> RunAsync()
        {
            var retry = RetryTimer.Start(TimeSpan.FromSeconds(1), Delay, time);

            while (true)
            {
                if (++attempts == 4)
                    return true;

                if (!await retry.TryWaitAsync())
                    return false;
            }
        }
    }

    [TestMethod]
    public async Task RetryLoop_GivesUpAtTimeLimit()
    {
        var time = new ManualTimeProvider();
        int attempts = 0;

        var loop = RunAsync();

        while (!loop.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            await Task.Yield();
        }

        (await loop).ShouldBeFalse();

        // The first attempt at 0 ms, then one after each of the waits ending at 50, 100, 150, 200 and 230 ms.
        attempts.ShouldBe(6);

        async Task<bool> RunAsync()
        {
            var retry = RetryTimer.Start(TimeSpan.FromMilliseconds(230), Delay, time);

            do
            {
                attempts++;
            }
            while (await retry.TryWaitAsync());

            retry.Elapsed.ShouldBe(TimeSpan.FromMilliseconds(230));
            return false;
        }
    }

    [TestMethod]
    public async Task TryWaitAsync_Canceled_Throws()
    {
        var time = new ManualTimeProvider();
        var retry = RetryTimer.Start(TimeSpan.FromSeconds(1), Delay, time);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(retry.TryWaitAsync(canceled.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(retry.WaitAsync(canceled.Token).AsTask());

        using var cts = new CancellationTokenSource();
        var wait = retry.TryWaitAsync(cts.Token);
        wait.IsCompleted.ShouldBeFalse();

        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(wait.AsTask());

        time.PendingTimerCount.ShouldBe(0);
    }

    [TestMethod]
    public void TryWait_ManualTimeProvider_BlocksUntilTimeAdvances()
    {
        var time = new ManualTimeProvider();
        var retry = RetryTimer.Start(TimeSpan.FromMilliseconds(80), Delay, time);
        var results = new List<bool>();

        var waiter = new Thread(() => {
            while (retry.TryWait())
                results.Add(true);

            results.Add(false);
        });

        waiter.Start();

        time.WaitForPendingTimers(1);
        waiter.Join(TimeSpan.FromMilliseconds(50)).ShouldBeFalse();
        time.Advance(Delay);

        // The second wait is cut short to the 30 ms left.
        time.WaitForPendingTimers(1);
        time.Advance(TimeSpan.FromMilliseconds(30));

        waiter.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        results.ShouldBe([true, true, false]);
    }

    [TestMethod]
    public void TryWait_SystemTimeProvider_RetriesUntilTimeLimit()
    {
        var stopwatch = Stopwatch.StartNew();
        var retry = RetryTimer.Start(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));
        int attempts = 1;

        while (retry.TryWait())
            attempts++;

        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(190));
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));

        // 11 attempts, or one or two more if a wait ends a little early by the stopwatch. Never a run of attempts without a pause.
        attempts.ShouldBeGreaterThan(1);
        attempts.ShouldBeLessThanOrEqualTo(13);
    }

    [TestMethod]
    public void Wait_SystemTimeProvider_Canceled_Throws()
    {
        var retry = RetryTimer.Start(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Should.Throw<OperationCanceledException>(() => retry.TryWait(canceled.Token));
        Should.Throw<OperationCanceledException>(() => retry.Wait(canceled.Token));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        Should.Throw<OperationCanceledException>(() => retry.TryWait(cts.Token));
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public void TryWait_ManualTimeProvider_Canceled_Throws()
    {
        var time = new ManualTimeProvider();
        var retry = RetryTimer.Start(TimeSpan.FromSeconds(1), Delay, time);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Should.Throw<OperationCanceledException>(() => retry.TryWait(canceled.Token));
        Should.Throw<OperationCanceledException>(() => retry.Wait(canceled.Token));
    }
}
