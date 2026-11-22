using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using PrefixClassName.MsTest;
using Shouldly;
using Singulink.CodeAnalysis;

namespace Singulink.Threading.Tests;

// The waits in these tests are observed through the ValueTask itself: its IsCompleted reads the state of the wait directly, so a grant, a time limit
// or a cancellation is visible as soon as the call that caused it returns, with no continuation and no sleep in between.
[PrefixTestClass]
public class AsyncReadWriteLockTests
{
    private const AsyncReadWriteLockPreference Fair = AsyncReadWriteLockPreference.Fair;
    private const AsyncReadWriteLockPreference Readers = AsyncReadWriteLockPreference.Readers;
    private const AsyncReadWriteLockPreference Writers = AsyncReadWriteLockPreference.Writers;

    // Each race is repeated this many times, or for as long as RaceTimeBudget lasts if that is shorter, but never fewer than MinRaceIterations
    // times: on a machine that is busy or has few cores
    // the threads of a race are handed over slowly, and the tests would otherwise run for minutes.
    private const int RaceIterations = 40_000;
    private const int MinRaceIterations = 2_000;

    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RaceTimeBudget = TimeSpan.FromSeconds(4);

    [ThreadStatic]
    private static bool _resolvingOnThisThread;

    #region Construction and state

    [TestMethod]
    public void Constructor_ChecksArguments()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new AsyncReadWriteLock((AsyncReadWriteLockPreference)3)).ParamName.ShouldBe("preference");
        Should.Throw<ArgumentOutOfRangeException>(() => new AsyncReadWriteLock((AsyncReadWriteLockPreference)(-1), TimeProvider.System))
            .ParamName.ShouldBe("preference");
        Should.Throw<ArgumentNullException>(() => new AsyncReadWriteLock(Fair, null!)).ParamName.ShouldBe("timeProvider");

        new AsyncReadWriteLock().Preference.ShouldBe(Fair);
        new AsyncReadWriteLock(Readers).Preference.ShouldBe(Readers);
        new AsyncReadWriteLock(Writers, new ManualTimeProvider()).Preference.ShouldBe(Writers);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void NewLock_IsFree(AsyncReadWriteLockPreference preference)
    {
        var rwLock = new AsyncReadWriteLock(preference);
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void EnterReadGuardAsync_Free_EntersWithoutWaiting(AsyncReadWriteLockPreference preference)
    {
        var rwLock = new AsyncReadWriteLock(preference);

        var entering = rwLock.EnterReadGuardAsync();
        entering.IsCompletedSuccessfully.ShouldBeTrue();
        var guard = entering.Result;

        guard.IsDefault.ShouldBeFalse();
        guard.IsEntered.ShouldBeTrue();
        guard.IsDisposed.ShouldBeFalse();
        rwLock.IsHeldForRead.ShouldBeTrue();
        rwLock.IsHeldForWrite.ShouldBeFalse();
        rwLock.CurrentReadCount.ShouldBe(1);

        guard.Dispose();

        guard.IsDisposed.ShouldBeTrue();
        guard.IsEntered.ShouldBeTrue();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void EnterWriteGuardAsync_Free_EntersWithoutWaiting(AsyncReadWriteLockPreference preference)
    {
        var rwLock = new AsyncReadWriteLock(preference);

        var entering = rwLock.EnterWriteGuardAsync();
        entering.IsCompletedSuccessfully.ShouldBeTrue();
        var guard = entering.Result;

        guard.IsDefault.ShouldBeFalse();
        guard.IsEntered.ShouldBeTrue();
        guard.IsDisposed.ShouldBeFalse();
        rwLock.IsHeldForWrite.ShouldBeTrue();
        rwLock.IsHeldForRead.ShouldBeFalse();
        rwLock.CurrentReadCount.ShouldBe(0);

        guard.Dispose();

        guard.IsDisposed.ShouldBeTrue();
        guard.IsEntered.ShouldBeTrue();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void ReadMode_IsSharedAndExcludesWriters(AsyncReadWriteLockPreference preference)
    {
        var rwLock = new AsyncReadWriteLock(preference);

        var first = rwLock.EnterReadGuardAsync().Result;
        var second = rwLock.TryEnterReadGuardAsync(Limit).Result;
        var third = rwLock.TryEnterReadGuard();

        second.IsEntered.ShouldBeTrue();
        third.IsEntered.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(3);

        rwLock.TryEnterWriteGuard().IsEntered.ShouldBeFalse();
        var noWait = rwLock.TryEnterWriteGuardAsync(TimeSpan.Zero);
        noWait.IsCompletedSuccessfully.ShouldBeTrue();
        noWait.Result.IsEntered.ShouldBeFalse();

        var writer = rwLock.EnterWriteGuardAsync();
        writer.IsCompleted.ShouldBeFalse();
        rwLock.WaitingWriteCount.ShouldBe(1);

        first.Dispose();
        second.Dispose();
        writer.IsCompleted.ShouldBeFalse();
        rwLock.CurrentReadCount.ShouldBe(1);

        third.Dispose();
        writer.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.IsHeldForWrite.ShouldBeTrue();
        rwLock.WaitingWriteCount.ShouldBe(0);

        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void WriteMode_ExcludesReadersAndWriters(AsyncReadWriteLockPreference preference)
    {
        var rwLock = new AsyncReadWriteLock(preference);
        var holder = rwLock.TryEnterWriteGuard();
        holder.IsEntered.ShouldBeTrue();

        rwLock.TryEnterReadGuard().IsEntered.ShouldBeFalse();
        rwLock.TryEnterWriteGuard().IsEntered.ShouldBeFalse();
        rwLock.TryEnterReadGuardAsync(TimeSpan.Zero).Result.IsEntered.ShouldBeFalse();
        rwLock.TryEnterWriteGuardAsync(TimeSpan.Zero).Result.IsEntered.ShouldBeFalse();
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(0);

        var reader1 = rwLock.EnterReadGuardAsync();
        var reader2 = rwLock.TryEnterReadGuardAsync(Timeout.InfiniteTimeSpan);
        reader1.IsCompleted.ShouldBeFalse();
        reader2.IsCompleted.ShouldBeFalse();
        rwLock.WaitingReadCount.ShouldBe(2);
        rwLock.IsHeldForRead.ShouldBeFalse();

        holder.Dispose();

        reader1.IsCompletedSuccessfully.ShouldBeTrue();
        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(2);
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.IsHeldForWrite.ShouldBeFalse();

        reader1.Result.Dispose();
        var guard2 = reader2.Result;
        guard2.IsEntered.ShouldBeTrue();
        guard2.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void Writers_EnterInArrivalOrder(AsyncReadWriteLockPreference preference)
    {
        var rwLock = new AsyncReadWriteLock(preference);
        var holder = rwLock.TryEnterWriteGuard();

        var writers = EnterMany(5, () => rwLock.EnterWriteGuardAsync());
        rwLock.WaitingWriteCount.ShouldBe(5);

        // A task whose guard was moved out is a default task, which is complete, so the count includes the writers that were let in before.
        for (int i = 0; i < writers.Length; i++)
        {
            Completed(writers).Count(c => c).ShouldBe(i);
            holder.Dispose();

            writers[i].IsCompletedSuccessfully.ShouldBeTrue();
            Completed(writers).Count(c => c).ShouldBe(i + 1);
            holder = NonCopyable.Move(ref writers[i]).Result;
        }

        holder.Dispose();
        ShouldBeFree(rwLock);
    }

    #endregion

    #region Preference: fair

    [TestMethod]
    public void Fair_GrantsInArrivalOrder_WithNeighboringReadersTogether()
    {
        var rwLock = new AsyncReadWriteLock(Fair);
        var reader1 = rwLock.TryEnterReadGuard();

        var writer1 = rwLock.EnterWriteGuardAsync();
        var reader2 = rwLock.EnterReadGuardAsync();
        var reader3 = rwLock.EnterReadGuardAsync();
        var writer2 = rwLock.EnterWriteGuardAsync();
        var reader4 = rwLock.EnterReadGuardAsync();

        // Nothing passes the first writer, although the lock is held in read mode.
        new[] { reader2.IsCompleted, reader3.IsCompleted, reader4.IsCompleted, writer1.IsCompleted, writer2.IsCompleted }.ShouldAllBe(c => !c);
        rwLock.TryEnterReadGuard().IsEntered.ShouldBeFalse();
        rwLock.TryEnterWriteGuard().IsEntered.ShouldBeFalse();
        rwLock.CurrentReadCount.ShouldBe(1);
        rwLock.WaitingReadCount.ShouldBe(3);
        rwLock.WaitingWriteCount.ShouldBe(2);

        reader1.Dispose();
        writer1.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.IsHeldForWrite.ShouldBeTrue();
        new[] { reader2.IsCompleted, reader3.IsCompleted, reader4.IsCompleted, writer2.IsCompleted }.ShouldAllBe(c => !c);

        // The two readers between the writers enter together; the reader behind the second writer does not.
        writer1.Result.Dispose();
        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        reader3.IsCompletedSuccessfully.ShouldBeTrue();
        reader4.IsCompleted.ShouldBeFalse();
        writer2.IsCompleted.ShouldBeFalse();
        rwLock.CurrentReadCount.ShouldBe(2);

        reader2.Result.Dispose();
        writer2.IsCompleted.ShouldBeFalse();
        reader3.Result.Dispose();
        writer2.IsCompletedSuccessfully.ShouldBeTrue();
        reader4.IsCompleted.ShouldBeFalse();

        writer2.Result.Dispose();
        reader4.IsCompletedSuccessfully.ShouldBeTrue();
        reader4.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Fair_ReaderEntersAtOnce_WhileNoWriterWaits()
    {
        var rwLock = new AsyncReadWriteLock(Fair);
        var reader1 = rwLock.TryEnterReadGuard();

        var writer = rwLock.EnterWriteGuardAsync();
        rwLock.TryEnterReadGuard().IsEntered.ShouldBeFalse();

        // Once the writer has come and gone, readers enter without waiting again.
        reader1.Dispose();
        writer.Result.Dispose();

        var reader2 = rwLock.TryEnterReadGuard();
        var reader3 = rwLock.EnterReadGuardAsync();
        reader2.IsEntered.ShouldBeTrue();
        reader3.IsCompletedSuccessfully.ShouldBeTrue();

        reader2.Dispose();
        reader3.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Fair_NeitherSideIsKeptWaiting()
    {
        var rwLock = new AsyncReadWriteLock(Fair);

        // A writer is not kept out by read holds that overlap: readers that arrive after it wait behind it.
        var reader = rwLock.TryEnterReadGuard();
        var writer = rwLock.EnterWriteGuardAsync();
        var lateReaders = EnterMany(10, () => rwLock.EnterReadGuardAsync());

        Completed(lateReaders).ShouldAllBe(c => !c);
        reader.Dispose();
        writer.IsCompletedSuccessfully.ShouldBeTrue();
        Completed(lateReaders).ShouldAllBe(c => !c);

        // A reader is not kept out by writers that keep arriving: writers that arrive after it wait behind it.
        var lateWriter = rwLock.EnterWriteGuardAsync();
        writer.Result.Dispose();

        CompletedSuccessfully(lateReaders).ShouldAllBe(c => c);
        lateWriter.IsCompleted.ShouldBeFalse();

        for (int i = 0; i < lateReaders.Length; i++)
            NonCopyable.Move(ref lateReaders[i]).Result.Dispose();

        lateWriter.IsCompletedSuccessfully.ShouldBeTrue();
        lateWriter.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Fair_ReentrantRead_WhileWriterWaits_DoesNotEnter()
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);

        var outer = rwLock.TryEnterReadGuard();
        var writer = rwLock.EnterWriteGuardAsync();

        var inner = rwLock.TryEnterReadGuardAsync(Limit);
        inner.IsCompleted.ShouldBeFalse();

        time.Advance(Limit);
        inner.IsCompletedSuccessfully.ShouldBeTrue();
        inner.Result.IsEntered.ShouldBeFalse();

        outer.Dispose();
        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    #endregion

    #region Preference: readers

    [TestMethod]
    public void Readers_ReaderPassesWaitingWriters()
    {
        var rwLock = new AsyncReadWriteLock(Readers);
        var reader1 = rwLock.TryEnterReadGuard();

        var writer1 = rwLock.EnterWriteGuardAsync();
        var writer2 = rwLock.EnterWriteGuardAsync();
        rwLock.WaitingWriteCount.ShouldBe(2);

        // A waiting writer holds no reader up, whichever way the reader enters.
        var reader2 = rwLock.TryEnterReadGuard();
        var reader3 = rwLock.EnterReadGuardAsync();
        var reader4 = rwLock.TryEnterReadGuardAsync(TimeSpan.Zero);

        var guard4 = reader4.Result;
        reader2.IsEntered.ShouldBeTrue();
        reader3.IsCompletedSuccessfully.ShouldBeTrue();
        guard4.IsEntered.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(4);
        rwLock.WaitingReadCount.ShouldBe(0);

        reader1.Dispose();
        reader2.Dispose();
        reader3.Result.Dispose();
        writer1.IsCompleted.ShouldBeFalse();

        rwLock.CurrentReadCount.ShouldBe(1);
        rwLock.TryEnterReadGuard().Dispose();
        rwLock.CurrentReadCount.ShouldBe(1);
        writer1.IsCompleted.ShouldBeFalse();

        guard4.Dispose();
        writer1.IsCompletedSuccessfully.ShouldBeTrue();
        writer2.IsCompleted.ShouldBeFalse();

        writer1.Result.Dispose();
        writer2.IsCompletedSuccessfully.ShouldBeTrue();
        writer2.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Readers_ReentrantRead_WhileWriterWaits_Enters()
    {
        var rwLock = new AsyncReadWriteLock(Readers);

        var outer = rwLock.TryEnterReadGuard();
        var writer = rwLock.EnterWriteGuardAsync();

        // The second hold of a caller that already holds the lock in read mode never waits, so it cannot deadlock on the waiting writer.
        var inner = rwLock.EnterReadGuardAsync();
        inner.IsCompletedSuccessfully.ShouldBeTrue();

        inner.Result.Dispose();
        writer.IsCompleted.ShouldBeFalse();
        outer.Dispose();

        writer.IsCompletedSuccessfully.ShouldBeTrue();
        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Readers_WaitingReadersEnterBeforeWaitingWriters_WhenWriterLeaves()
    {
        var rwLock = new AsyncReadWriteLock(Readers);
        var holder = rwLock.TryEnterWriteGuard();

        // The second writer arrived before the readers, and still enters after them: a reader waits for at most the rest of one write hold.
        var writer = rwLock.EnterWriteGuardAsync();
        var reader1 = rwLock.EnterReadGuardAsync();
        var reader2 = rwLock.EnterReadGuardAsync();

        holder.Dispose();

        reader1.IsCompletedSuccessfully.ShouldBeTrue();
        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        writer.IsCompleted.ShouldBeFalse();
        rwLock.CurrentReadCount.ShouldBe(2);

        reader1.Result.Dispose();
        reader2.Result.Dispose();
        writer.IsCompletedSuccessfully.ShouldBeTrue();
        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Readers_WriterIsKeptWaiting_WhileReadHoldsOverlap()
    {
        var rwLock = new AsyncReadWriteLock(Readers);

        var guards = new AsyncReadGuard[2];
        guards[0] = rwLock.TryEnterReadGuard();
        var writer = rwLock.EnterWriteGuardAsync();

        // Each read hold begins before the one before it ends, so the count never reaches zero and the writer never enters.
        for (int i = 0; i < 1000; i++)
        {
            ref var current = ref guards[i % 2];
            ref var next = ref guards[(i + 1) % 2];

            next = (i % 2) is 0 ? rwLock.TryEnterReadGuard() : rwLock.EnterReadGuardAsync().Result;
            next.IsEntered.ShouldBeTrue();

            current.Dispose();
            writer.IsCompleted.ShouldBeFalse();
        }

        rwLock.WaitingWriteCount.ShouldBe(1);
        guards[1000 % 2].Dispose();

        writer.IsCompletedSuccessfully.ShouldBeTrue();
        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    #endregion

    #region Preference: writers

    [TestMethod]
    public void Writers_ReaderWaitsBehindWaitingWriter()
    {
        var rwLock = new AsyncReadWriteLock(Writers);
        var reader1 = rwLock.TryEnterReadGuard();

        var writer = rwLock.EnterWriteGuardAsync();

        rwLock.TryEnterReadGuard().IsEntered.ShouldBeFalse();
        rwLock.TryEnterReadGuardAsync(TimeSpan.Zero).Result.IsEntered.ShouldBeFalse();
        var reader2 = rwLock.EnterReadGuardAsync();
        reader2.IsCompleted.ShouldBeFalse();
        rwLock.CurrentReadCount.ShouldBe(1);

        reader1.Dispose();
        writer.IsCompletedSuccessfully.ShouldBeTrue();
        reader2.IsCompleted.ShouldBeFalse();

        writer.Result.Dispose();
        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        reader2.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Writers_WaitingWritersEnterBeforeWaitingReaders()
    {
        var rwLock = new AsyncReadWriteLock(Writers);
        var holder = rwLock.TryEnterWriteGuard();

        // The readers arrived before the second writer, and still enter after it.
        var reader1 = rwLock.EnterReadGuardAsync();
        var reader2 = rwLock.EnterReadGuardAsync();
        var writer = rwLock.EnterWriteGuardAsync();

        holder.Dispose();

        writer.IsCompletedSuccessfully.ShouldBeTrue();
        reader1.IsCompleted.ShouldBeFalse();
        reader2.IsCompleted.ShouldBeFalse();

        writer.Result.Dispose();
        reader1.IsCompletedSuccessfully.ShouldBeTrue();
        reader2.IsCompletedSuccessfully.ShouldBeTrue();

        reader1.Result.Dispose();
        reader2.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Writers_ReaderIsKeptWaiting_WhileWritersKeepArriving()
    {
        var rwLock = new AsyncReadWriteLock(Writers);

        var current = rwLock.TryEnterWriteGuard();
        var reader = rwLock.EnterReadGuardAsync();

        // Each writer arrives before the one before it leaves, so a writer is always waiting and the reader never enters.
        for (int i = 0; i < 1000; i++)
        {
            var next = rwLock.EnterWriteGuardAsync();
            current.Dispose();

            next.IsCompletedSuccessfully.ShouldBeTrue();
            reader.IsCompleted.ShouldBeFalse();
            current = next.Result;
        }

        rwLock.WaitingReadCount.ShouldBe(1);
        current.Dispose();

        reader.IsCompletedSuccessfully.ShouldBeTrue();
        reader.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Writers_ReentrantRead_WhileWriterWaits_DoesNotEnter()
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Writers, time);

        var outer = rwLock.TryEnterReadGuard();
        var writer = rwLock.EnterWriteGuardAsync();

        var inner = rwLock.TryEnterReadGuardAsync(Limit);
        inner.IsCompleted.ShouldBeFalse();

        time.Advance(Limit);
        inner.IsCompletedSuccessfully.ShouldBeTrue();
        inner.Result.IsEntered.ShouldBeFalse();

        outer.Dispose();
        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    #endregion

    #region Reentrancy that waits for itself

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void ReentrantEntry_ThatWaitsForItself_EndsAtTimeLimit(AsyncReadWriteLockPreference preference)
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(preference, time);

        // Write, then read or write.
        var writeHold = rwLock.TryEnterWriteGuard();
        var innerRead = rwLock.TryEnterReadGuardAsync(Limit);
        var innerWrite = rwLock.TryEnterWriteGuardAsync(Limit);

        time.Advance(Limit);
        innerRead.Result.IsEntered.ShouldBeFalse();
        innerWrite.Result.IsEntered.ShouldBeFalse();
        writeHold.Dispose();

        // Read, then write.
        var readHold = rwLock.TryEnterReadGuard();
        var upgrade = rwLock.TryEnterWriteGuardAsync(Limit);

        time.Advance(Limit);
        upgrade.Result.IsEntered.ShouldBeFalse();
        readHold.Dispose();

        ShouldBeFree(rwLock);
        time.PendingTimerCount.ShouldBe(0);
    }

    #endregion

    #region Time limits

    [TestMethod]
    public void TryEnterGuardAsync_ChecksTimeout()
    {
        var rwLock = new AsyncReadWriteLock();
        var tooLong = TimeSpan.FromMilliseconds(int.MaxValue) + Tick;

        Should.Throw<ArgumentOutOfRangeException>(() => { _ = rwLock.TryEnterReadGuardAsync(-Tick); }).ParamName.ShouldBe("timeout");
        Should.Throw<ArgumentOutOfRangeException>(() => { _ = rwLock.TryEnterReadGuardAsync(TimeSpan.FromSeconds(-2)); }).ParamName.ShouldBe("timeout");
        Should.Throw<ArgumentOutOfRangeException>(() => { _ = rwLock.TryEnterReadGuardAsync(tooLong); }).ParamName.ShouldBe("timeout");
        Should.Throw<ArgumentOutOfRangeException>(() => { _ = rwLock.TryEnterWriteGuardAsync(-Tick); }).ParamName.ShouldBe("timeout");
        Should.Throw<ArgumentOutOfRangeException>(() => { _ = rwLock.TryEnterWriteGuardAsync(TimeSpan.FromSeconds(-2)); }).ParamName.ShouldBe("timeout");
        Should.Throw<ArgumentOutOfRangeException>(() => { _ = rwLock.TryEnterWriteGuardAsync(tooLong); }).ParamName.ShouldBe("timeout");

        ShouldBeFree(rwLock);

        rwLock.TryEnterReadGuardAsync(TimeSpan.FromMilliseconds(int.MaxValue)).Result.Dispose();
        rwLock.TryEnterWriteGuardAsync(TimeSpan.FromMilliseconds(int.MaxValue)).Result.Dispose();
        rwLock.TryEnterReadGuardAsync(Timeout.InfiniteTimeSpan).Result.Dispose();
        rwLock.TryEnterWriteGuardAsync(Timeout.InfiniteTimeSpan).Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void TryEnterGuardAsync_NotEnteredAtTimeLimit(AsyncReadWriteLockPreference preference)
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(preference, time);
        var holder = rwLock.TryEnterWriteGuard();

        var reader = rwLock.TryEnterReadGuardAsync(Limit);
        var writer = rwLock.TryEnterWriteGuardAsync(Limit + Limit);
        time.PendingTimerCount.ShouldBe(2);

        time.Advance(Limit - Tick);
        reader.IsCompleted.ShouldBeFalse();

        time.Advance(Tick);
        reader.IsCompletedSuccessfully.ShouldBeTrue();
        writer.IsCompleted.ShouldBeFalse();
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(1);

        var readGuard = reader.Result;
        readGuard.IsDefault.ShouldBeFalse();
        readGuard.IsEntered.ShouldBeFalse();
        readGuard.IsDisposed.ShouldBeFalse();

        time.Advance(Limit);
        writer.IsCompletedSuccessfully.ShouldBeTrue();
        var writeGuard = writer.Result;
        writeGuard.IsEntered.ShouldBeFalse();
        time.PendingTimerCount.ShouldBe(0);

        // Disposing guards that did not enter the lock leaves it with its holder.
        readGuard.Dispose();
        writeGuard.Dispose();
        readGuard.IsDisposed.ShouldBeTrue();
        writeGuard.IsDisposed.ShouldBeTrue();
        rwLock.IsHeldForWrite.ShouldBeTrue();

        holder.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TryEnterGuardAsync_EnteredBeforeTimeLimit_RemovesTimer()
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);
        var holder = rwLock.TryEnterWriteGuard();

        var reader = rwLock.TryEnterReadGuardAsync(Limit);
        time.PendingTimerCount.ShouldBe(1);
        time.Advance(Limit - Tick);

        holder.Dispose();
        reader.IsCompletedSuccessfully.ShouldBeTrue();
        time.PendingTimerCount.ShouldBe(0);

        // The time limit passing afterwards changes nothing.
        time.Advance(Limit);
        var guard = reader.Result;
        guard.IsEntered.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(1);

        guard.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TryEnterGuardAsync_ZeroTimeLimit_DoesNotWait()
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);
        var holder = rwLock.TryEnterWriteGuard();

        var reader = rwLock.TryEnterReadGuardAsync(TimeSpan.Zero);
        var writer = rwLock.TryEnterWriteGuardAsync(TimeSpan.Zero);

        reader.IsCompletedSuccessfully.ShouldBeTrue();
        writer.IsCompletedSuccessfully.ShouldBeTrue();
        reader.Result.IsEntered.ShouldBeFalse();
        writer.Result.IsEntered.ShouldBeFalse();
        time.PendingTimerCount.ShouldBe(0);
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(0);

        holder.Dispose();

        var readGuard = rwLock.TryEnterReadGuardAsync(TimeSpan.Zero).Result;
        readGuard.IsEntered.ShouldBeTrue();
        rwLock.TryEnterWriteGuardAsync(TimeSpan.Zero).Result.IsEntered.ShouldBeFalse();
        readGuard.Dispose();

        var entered = rwLock.TryEnterWriteGuardAsync(TimeSpan.Zero).Result;
        entered.IsEntered.ShouldBeTrue();
        entered.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TryEnterGuardAsync_InfiniteTimeLimit_WaitsWithoutTimer()
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);
        var holder = rwLock.TryEnterWriteGuard();

        var reader = rwLock.TryEnterReadGuardAsync(Timeout.InfiniteTimeSpan);
        var writer = rwLock.EnterWriteGuardAsync();
        time.PendingTimerCount.ShouldBe(0);

        time.Advance(TimeSpan.FromDays(1000));
        reader.IsCompleted.ShouldBeFalse();
        writer.IsCompleted.ShouldBeFalse();

        holder.Dispose();
        reader.Result.Dispose();
        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TryEnterGuardAsync_RoundsTimeLimitUpToWholeMilliseconds()
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);
        var holder = rwLock.TryEnterWriteGuard();

        // A limit of 0.5 ms is waited as 1 ms rather than cut to nothing, and 2.2 ms as 3 ms.
        var reader = rwLock.TryEnterReadGuardAsync(TimeSpan.FromMilliseconds(0.5));
        var writer = rwLock.TryEnterWriteGuardAsync(TimeSpan.FromMilliseconds(2.2));

        time.Advance(TimeSpan.FromMilliseconds(1) - Tick);
        reader.IsCompleted.ShouldBeFalse();
        time.Advance(Tick);
        reader.Result.IsEntered.ShouldBeFalse();

        time.Advance(TimeSpan.FromMilliseconds(2) - Tick);
        writer.IsCompleted.ShouldBeFalse();
        time.Advance(Tick);
        writer.Result.IsEntered.ShouldBeFalse();

        holder.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Writers)]
    public void WriterThatGivesUp_LetsReadersBehindItEnter(AsyncReadWriteLockPreference preference)
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(preference, time);
        var reader1 = rwLock.TryEnterReadGuard();

        using var cts = new CancellationTokenSource();
        var timedWriter = rwLock.TryEnterWriteGuardAsync(Limit);
        var canceledWriter = rwLock.EnterWriteGuardAsync(cts.Token);
        var reader2 = rwLock.EnterReadGuardAsync();
        var reader3 = rwLock.EnterReadGuardAsync();

        // One writer leaves the queue and another still waits: the readers stay behind it.
        time.Advance(Limit);
        timedWriter.Result.IsEntered.ShouldBeFalse();
        reader2.IsCompleted.ShouldBeFalse();

        // The last waiting writer leaves: nothing is ahead of the readers, and the lock is held in read mode.
        cts.Cancel();
        canceledWriter.IsCanceled.ShouldBeTrue();
        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        reader3.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(3);

        // With nothing waiting, readers enter without waiting again.
        var reader4 = rwLock.TryEnterReadGuard();
        reader4.IsEntered.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(4);

        reader4.Dispose();
        reader1.Dispose();
        reader2.Result.Dispose();
        reader3.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public async Task TryEnterGuardAsync_SystemTimeProvider_GivesUpAfterTimeLimit()
    {
        var rwLock = new AsyncReadWriteLock(Readers);
        var readHold = rwLock.TryEnterReadGuard();

        var stopwatch = Stopwatch.StartNew();
        var writeGuard = await rwLock.TryEnterWriteGuardAsync(TimeSpan.FromMilliseconds(100));

        writeGuard.IsEntered.ShouldBeFalse();
        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90));
        stopwatch.Elapsed.ShouldBeLessThan(TestTimeout);
        readHold.Dispose();

        var writeHold = rwLock.TryEnterWriteGuard();
        var readGuard = await rwLock.TryEnterReadGuardAsync(TimeSpan.FromMilliseconds(20));
        readGuard.IsEntered.ShouldBeFalse();

        var pending = rwLock.TryEnterReadGuardAsync(TestTimeout).AsTask();
        writeHold.Dispose();

        using var entered = await pending.WaitAsync(TestTimeout);
        entered.IsEntered.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(1);
    }

    #endregion

    #region Cancellation

    [TestMethod]
    public void EnterGuardAsync_CanceledBeforeCall_IsCanceledEvenIfFree()
    {
        var rwLock = new AsyncReadWriteLock();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var calls = new Func<Task>[] {
            () => rwLock.EnterReadGuardAsync(cts.Token).AsTask(),
            () => rwLock.TryEnterReadGuardAsync(Limit, cts.Token).AsTask(),
            () => rwLock.TryEnterReadGuardAsync(TimeSpan.Zero, cts.Token).AsTask(),
            () => rwLock.EnterWriteGuardAsync(cts.Token).AsTask(),
            () => rwLock.TryEnterWriteGuardAsync(Limit, cts.Token).AsTask(),
            () => rwLock.TryEnterWriteGuardAsync(TimeSpan.Zero, cts.Token).AsTask(),
        };

        foreach (var call in calls)
        {
            var task = call();
            task.IsCanceled.ShouldBeTrue();
            Should.Throw<OperationCanceledException>(() => task.GetAwaiter().GetResult()).CancellationToken.ShouldBe(cts.Token);
        }

        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void EnterGuardAsync_CanceledWhileWaiting_ThrowsAndLeavesQueue(AsyncReadWriteLockPreference preference)
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(preference, time);
        var holder = rwLock.TryEnterWriteGuard();

        using var cts = new CancellationTokenSource();
        var reader = rwLock.EnterReadGuardAsync(cts.Token);
        var writer = rwLock.TryEnterWriteGuardAsync(Limit, cts.Token);
        var other = rwLock.EnterReadGuardAsync();

        reader.IsCompleted.ShouldBeFalse();
        writer.IsCompleted.ShouldBeFalse();
        time.PendingTimerCount.ShouldBe(1);

        cts.Cancel();

        reader.IsCanceled.ShouldBeTrue();
        writer.IsCanceled.ShouldBeTrue();
        other.IsCompleted.ShouldBeFalse();
        Should.Throw<OperationCanceledException>(() => NonCopyable.Move(ref reader).Result).CancellationToken.ShouldBe(cts.Token);
        Should.Throw<OperationCanceledException>(() => NonCopyable.Move(ref writer).Result).CancellationToken.ShouldBe(cts.Token);

        rwLock.WaitingReadCount.ShouldBe(1);
        rwLock.WaitingWriteCount.ShouldBe(0);
        time.PendingTimerCount.ShouldBe(0);
        rwLock.IsHeldForWrite.ShouldBeTrue();

        holder.Dispose();
        other.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void EnterGuardAsync_CanceledAfterEntering_ChangesNothing()
    {
        var rwLock = new AsyncReadWriteLock();
        var holder = rwLock.TryEnterWriteGuard();

        using var cts = new CancellationTokenSource();
        var reader = rwLock.EnterReadGuardAsync(cts.Token);

        holder.Dispose();
        reader.IsCompletedSuccessfully.ShouldBeTrue();

        cts.Cancel();
        var guard = reader.Result;
        guard.IsEntered.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(1);

        guard.Dispose();
        ShouldBeFree(rwLock);
    }

    #endregion

    #region Guards

    [TestMethod]
    public void DefaultGuards_Throw()
    {
        AsyncReadGuard readGuard = default;
        AsyncWriteGuard writeGuard = default;

        readGuard.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => readGuard.IsEntered);
        Should.Throw<InvalidOperationException>(() => readGuard.IsDisposed);
        Should.Throw<InvalidOperationException>(() => readGuard.Dispose());

        writeGuard.IsDefault.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => writeGuard.IsEntered);
        Should.Throw<InvalidOperationException>(() => writeGuard.IsDisposed);
        Should.Throw<InvalidOperationException>(() => writeGuard.Dispose());
    }

    [TestMethod]
    public void Dispose_Twice_ReleasesOnce()
    {
        var rwLock = new AsyncReadWriteLock();

        var reader1 = rwLock.TryEnterReadGuard();
        var reader2 = rwLock.TryEnterReadGuard();

        reader1.Dispose();
        reader1.Dispose();
        rwLock.CurrentReadCount.ShouldBe(1);
        reader2.Dispose();

        var writer = rwLock.TryEnterWriteGuard();
        writer.Dispose();

        var nextWriter = rwLock.TryEnterWriteGuard();
        writer.Dispose();
        rwLock.IsHeldForWrite.ShouldBeTrue();

        nextWriter.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void WriteGuardCopy_DisposedAfterOriginal_ThrowsAndLeavesLockAlone()
    {
        var rwLock = new AsyncReadWriteLock();

        var original = rwLock.TryEnterWriteGuard();
#pragma warning disable SCA1001 // Copy by assignment: the test disposes copies of a guard on purpose
        var copy = original;
        var secondCopy = original;
#pragma warning restore SCA1001

        original.Dispose();

        // While the lock is free.
        Should.Throw<SynchronizationLockException>(() => copy.Dispose());
        copy.IsDisposed.ShouldBeTrue();
        copy.Dispose();
        ShouldBeFree(rwLock);

        // While another writer holds it, with and without waiters.
        var nextWriter = rwLock.TryEnterWriteGuard();
#pragma warning disable SCA1001 // Copy by assignment: the test disposes a copy of a guard on purpose
        var thirdCopy = secondCopy;
#pragma warning restore SCA1001
        Should.Throw<SynchronizationLockException>(() => secondCopy.Dispose());
        rwLock.IsHeldForWrite.ShouldBeTrue();

        var waiter = rwLock.EnterReadGuardAsync();
        Should.Throw<SynchronizationLockException>(() => thirdCopy.Dispose());
        rwLock.IsHeldForWrite.ShouldBeTrue();
        waiter.IsCompleted.ShouldBeFalse();

        nextWriter.Dispose();
        waiter.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void WriteGuardCopy_DisposedWhileReadersHold_Throws()
    {
        var rwLock = new AsyncReadWriteLock();

        var original = rwLock.TryEnterWriteGuard();
#pragma warning disable SCA1001 // Copy by assignment: the test disposes a copy of a guard on purpose
        var copy = original;
#pragma warning restore SCA1001
        original.Dispose();

        var reader = rwLock.TryEnterReadGuard();
        Should.Throw<SynchronizationLockException>(() => copy.Dispose());
        rwLock.CurrentReadCount.ShouldBe(1);

        reader.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void ReadGuardCopy_DisposedWhenNoReadHoldIsLeft_ThrowsAndLeavesLockAlone()
    {
        var rwLock = new AsyncReadWriteLock();

        var original = rwLock.TryEnterReadGuard();
#pragma warning disable SCA1001 // Copy by assignment: the test disposes copies of a guard on purpose
        var copy = original;
        var secondCopy = original;
#pragma warning restore SCA1001
        original.Dispose();

        // While the lock is free.
        Should.Throw<SynchronizationLockException>(() => copy.Dispose());
        copy.IsDisposed.ShouldBeTrue();
        ShouldBeFree(rwLock);

        // While a writer holds it.
        var writer = rwLock.TryEnterWriteGuard();
        Should.Throw<SynchronizationLockException>(() => secondCopy.Dispose());
        rwLock.IsHeldForWrite.ShouldBeTrue();

        writer.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void ReadGuardCopy_DisposedWhileOtherReadersHold_TakesOneOfTheirHolds()
    {
        var rwLock = new AsyncReadWriteLock();

        var other = rwLock.TryEnterReadGuard();
        var original = rwLock.TryEnterReadGuard();
#pragma warning disable SCA1001 // Copy by assignment: the test disposes a copy of a guard on purpose
        var copy = original;
#pragma warning restore SCA1001
        original.Dispose();

        // This is the misuse that a read guard cannot detect: nothing tells the second release of one hold from the release of the other.
        copy.Dispose();
        rwLock.CurrentReadCount.ShouldBe(0);

        Should.Throw<SynchronizationLockException>(() => other.Dispose());
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void UsingStatementOverGuardVariable_DisposesHiddenCopy()
    {
        var rwLock = new AsyncReadWriteLock();
        var guard = rwLock.TryEnterWriteGuard();

        // This is the trap that the documentation warns of: the statement disposes a copy of the variable, not the variable.
#pragma warning disable SCA1003 // Stored value used where it may be copied: the test shows the copy that 'using' makes
        using (guard)
        {
            rwLock.IsHeldForWrite.ShouldBeTrue();
        }
#pragma warning restore SCA1003

        rwLock.IsHeldForWrite.ShouldBeFalse();
        guard.IsDisposed.ShouldBeFalse();

        // So a second release follows easily. A write guard notices it; a read guard would take the hold of another reader.
        Should.Throw<SynchronizationLockException>(() => guard.Dispose());
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void NotEnteredGuardCopies_DisposeDoesNothing()
    {
        var rwLock = new AsyncReadWriteLock();
        var holder = rwLock.TryEnterWriteGuard();

        var readGuard = rwLock.TryEnterReadGuard();
        var writeGuard = rwLock.TryEnterWriteGuard();
#pragma warning disable SCA1001 // Copy by assignment: the test disposes copies of guards on purpose
        var readCopy = readGuard;
        var writeCopy = writeGuard;
#pragma warning restore SCA1001

        readGuard.Dispose();
        writeGuard.Dispose();
        readCopy.Dispose();
        writeCopy.Dispose();
        rwLock.IsHeldForWrite.ShouldBeTrue();

        holder.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void WaitingTask_ResultTakenTwice_Throws()
    {
        var rwLock = new AsyncReadWriteLock();
        var holder = rwLock.TryEnterWriteGuard();

        var reader = rwLock.EnterReadGuardAsync();

        // The test reads the result of one task three times on purpose: before it completes, once to take the guard, and once more.
#pragma warning disable SCA1030 // By-value read from a stored container
        Should.Throw<InvalidOperationException>(() => reader.Result);

        holder.Dispose();
        var guard = reader.Result;

        // A second guard for the one hold would let it be released twice.
        Should.Throw<InvalidOperationException>(() => reader.Result);
#pragma warning restore SCA1030
        rwLock.CurrentReadCount.ShouldBe(1);

        guard.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TooManyReadHolds_Throws()
    {
        const long ReadLimit = 0x2000_0000;

        var rwLock = new AsyncReadWriteLock(Readers);
        var first = rwLock.TryEnterReadGuard();

        // Getting to the limit one hold at a time would take minutes, so the count is set directly.
        var stateField = typeof(AsyncReadWriteLock).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
        stateField.SetValue(rwLock, ReadLimit);
        rwLock.CurrentReadCount.ShouldBe((int)ReadLimit);

        // The call that returns a guard throws; the calls that return a task fault it.
        Should.Throw<InvalidOperationException>(() => { using var unexpected = rwLock.TryEnterReadGuard(); });
        ShouldBeFaulted<InvalidOperationException>(rwLock.EnterReadGuardAsync().AsTask());
        ShouldBeFaulted<InvalidOperationException>(rwLock.TryEnterReadGuardAsync(Limit).AsTask());
        ShouldBeFaulted<InvalidOperationException>(rwLock.TryEnterReadGuardAsync(TimeSpan.Zero).AsTask());
        rwLock.CurrentReadCount.ShouldBe((int)ReadLimit);
        rwLock.WaitingReadCount.ShouldBe(0);

        first.Dispose();
        rwLock.CurrentReadCount.ShouldBe((int)ReadLimit - 1);

        // Below the limit again.
        rwLock.TryEnterReadGuard().IsEntered.ShouldBeTrue();
    }

    #endregion

    #region No thread affinity, and where continuations run

    [TestMethod]
    public async Task Guards_CanBeDisposedOnAnotherThread()
    {
        var rwLock = new AsyncReadWriteLock();

        var readGuard = await rwLock.EnterReadGuardAsync();
        RunOnOtherThread(() => readGuard.Dispose()).ShouldBeNull();
        ShouldBeFree(rwLock);

        var writeGuard = await rwLock.EnterWriteGuardAsync();
        RunOnOtherThread(() => writeGuard.Dispose()).ShouldBeNull();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public async Task Guards_CanBeHeldAcrossAwaits()
    {
        var rwLock = new AsyncReadWriteLock();
        int value = 0;

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () => {
            for (int j = 0; j < 20; j++)
            {
                if ((i + j) % 5 is 0)
                {
                    using var guard = await rwLock.EnterWriteGuardAsync();
                    int read = value;
                    await Task.Yield();
                    value = read + 1;
                }
                else
                {
                    using var guard = await rwLock.TryEnterReadGuardAsync(TestTimeout);
                    guard.IsEntered.ShouldBeTrue();
                    await Task.Yield();
                    rwLock.IsHeldForWrite.ShouldBeFalse();
                }
            }
        }));

        await Task.WhenAll(tasks).WaitAsync(TestTimeout);

        value.ShouldBe(200);
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public async Task Continuation_DoesNotRunOnThreadThatReleases()
    {
        var rwLock = new AsyncReadWriteLock();
        var holder = rwLock.TryEnterWriteGuard();

        var readers = Enumerable.Range(0, 4).Select(_ => WaitAndObserveAsync(rwLock.EnterReadGuardAsync())).ToArray();
        rwLock.WaitingReadCount.ShouldBe(4);

        RunOnOtherThread(() => {
            _resolvingOnThisThread = true;
            holder.Dispose();
            _resolvingOnThisThread = false;
        }).ShouldBeNull();

        foreach (bool ranInline in await Task.WhenAll(readers).WaitAsync(TestTimeout))
            ranInline.ShouldBeFalse();

        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public async Task Continuation_DoesNotRunOnThreadThatCancelsOrRunsTimer()
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);
        var holder = rwLock.TryEnterWriteGuard();

        using var cts = new CancellationTokenSource();
        var canceled = WaitAndObserveAsync(rwLock.EnterReadGuardAsync(cts.Token));
        var timedOut = WaitAndObserveAsync(rwLock.TryEnterWriteGuardAsync(Limit));

        RunOnOtherThread(() => {
            _resolvingOnThisThread = true;
            cts.Cancel();
            time.Advance(Limit);
            _resolvingOnThisThread = false;
        }).ShouldBeNull();

        (await canceled.WaitAsync(TestTimeout)).ShouldBeFalse();
        (await timedOut.WaitAsync(TestTimeout)).ShouldBeFalse();

        holder.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Continuation_IsPostedToCapturedSynchronizationContext()
    {
        var rwLock = new AsyncReadWriteLock();
        var context = new QueueingSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            var holder = rwLock.TryEnterWriteGuard();
            bool resumed = false;
            SynchronizationContext? resumedOn = null;

            var waiting = WaitAsync();
            context.Posted.Count.ShouldBe(0);

            // The release posts the continuation and returns; it does not run it.
            holder.Dispose();
            resumed.ShouldBeFalse();
            context.Posted.Count.ShouldBe(1);
            rwLock.CurrentReadCount.ShouldBe(1);

            context.RunPosted();
            resumed.ShouldBeTrue();
            resumedOn.ShouldBeSameAs(context);
            waiting.IsCompletedSuccessfully.ShouldBeTrue();
            ShouldBeFree(rwLock);

            async Task WaitAsync()
            {
                using var guard = await rwLock.EnterReadGuardAsync();
                resumed = true;
                resumedOn = SynchronizationContext.Current;
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [TestMethod]
    public void Continuation_IsQueuedToCapturedTaskScheduler()
    {
        var rwLock = new AsyncReadWriteLock();
        var scheduler = new QueueingTaskScheduler();
        var holder = rwLock.TryEnterWriteGuard();
        TaskScheduler? resumedOn = null;

        var waiting = Task.Factory.StartNew(
            async () => {
                using var guard = await rwLock.EnterReadGuardAsync();
                resumedOn = TaskScheduler.Current;
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler).Unwrap();

        // Runs the method up to its await, which captures the scheduler.
        scheduler.RunQueued();
        rwLock.WaitingReadCount.ShouldBe(1);
        scheduler.QueuedCount.ShouldBe(0);

        // The release queues the continuation to the scheduler and returns; it does not run it.
        holder.Dispose();
        resumedOn.ShouldBeNull();
        scheduler.QueuedCount.ShouldBe(1);
        rwLock.CurrentReadCount.ShouldBe(1);

        scheduler.RunQueued();
        waiting.Wait(TestTimeout).ShouldBeTrue();
        resumedOn.ShouldBeSameAs(scheduler);
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public async Task ExecutionContext_FlowsAcrossWait()
    {
        var rwLock = new AsyncReadWriteLock();
        var local = new AsyncLocal<int>();
        var holder = rwLock.TryEnterWriteGuard();

        local.Value = 42;
        var waiting = WaitAsync();
        local.Value = 7;

        holder.Dispose();
        (await waiting.WaitAsync(TestTimeout)).ShouldBe(42);

        async Task<int> WaitAsync()
        {
            using var guard = await rwLock.EnterReadGuardAsync().ConfigureAwait(false);
            return local.Value;
        }
    }

    #endregion

    #region A wait that cannot be set up

    [TestMethod]
    public void TimeProviderThatThrows_FailsCallAndLeavesNoWaiter()
    {
        var rwLock = new AsyncReadWriteLock(Fair, new ThrowingTimeProvider());
        var holder = rwLock.TryEnterWriteGuard();

        // The failure is the task's, not an exception from the call.
        using var cts = new CancellationTokenSource();
        ShouldBeFaulted<NotSupportedException>(rwLock.TryEnterReadGuardAsync(Limit, cts.Token).AsTask()).Message.ShouldBe("No timers.");
        ShouldBeFaulted<NotSupportedException>(rwLock.TryEnterWriteGuardAsync(Limit, cts.Token).AsTask()).Message.ShouldBe("No timers.");

        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(0);
        rwLock.IsHeldForWrite.ShouldBeTrue();
        cts.Cancel();

        // Waits that need no timer are not affected.
        var reader = rwLock.EnterReadGuardAsync();
        holder.Dispose();
        reader.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TimeProviderThatThrows_AfterLockWasGranted_GivesLockBack(bool write)
    {
        AsyncWriteGuard holder = default;

        // The holder leaves while the wait is being set up, so the waiter is granted the lock before the time provider throws.
        var rwLock = new AsyncReadWriteLock(Fair, new ThrowingTimeProvider(() => holder.Dispose()));
        holder = rwLock.TryEnterWriteGuard();

        if (write)
            ShouldBeFaulted<NotSupportedException>(rwLock.TryEnterWriteGuardAsync(Limit).AsTask());
        else
            ShouldBeFaulted<NotSupportedException>(rwLock.TryEnterReadGuardAsync(Limit).AsTask());

        holder.IsDisposed.ShouldBeTrue();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TimeProviderThatThrows_AfterWaitWasCanceled_LeavesLockWithHolder()
    {
        using var cts = new CancellationTokenSource();
        var rwLock = new AsyncReadWriteLock(Fair, new ThrowingTimeProvider(cts.Cancel));
        var holder = rwLock.TryEnterWriteGuard();

        ShouldBeFaulted<NotSupportedException>(rwLock.TryEnterReadGuardAsync(Limit, cts.Token).AsTask());

        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.IsHeldForWrite.ShouldBeTrue();

        holder.Dispose();
        ShouldBeFree(rwLock);
    }

    #endregion

    #region Failures in completing a waiter

    // The rule under test: no member of the lock, no timer callback and no cancellation callback throws because of another caller's waiter, and no
    // failure leaves a waiter queued without an owner or a hold that nobody can release.

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void WriteRelease_ReaderThatCannotBeResumed_DoesNotThrowAndLosesItsHold(AsyncReadWriteLockPreference preference)
    {
        var rwLock = new AsyncReadWriteLock(preference);
        var holder = rwLock.TryEnterWriteGuard();

        var stuck = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.EnterReadGuardAsync());
        var reader2 = rwLock.EnterReadGuardAsync();
        var reader3 = rwLock.EnterReadGuardAsync();
        rwLock.WaitingReadCount.ShouldBe(3);

        // The first reader's context refuses its continuation. That is nothing to the writer that leaves.
        holder.Dispose();

        holder.IsDisposed.ShouldBeTrue();
        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        reader3.IsCompletedSuccessfully.ShouldBeTrue();

        // The reader that will never run does not keep a hold.
        rwLock.CurrentReadCount.ShouldBe(2);
        rwLock.WaitingReadCount.ShouldBe(0);
        stuck.IsCompleted.ShouldBeFalse();

        reader2.Result.Dispose();
        reader3.Result.Dispose();
        ShouldBeFree(rwLock);
        rwLock.TryEnterWriteGuard().IsEntered.ShouldBeTrue();
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void WriteRelease_WritersThatCannotBeResumed_PassTheLockOn(AsyncReadWriteLockPreference preference)
    {
        var rwLock = new AsyncReadWriteLock(preference);
        var holder = rwLock.TryEnterWriteGuard();

        // Two writers in a row that cannot be resumed, then one that can.
        var stuck1 = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.EnterWriteGuardAsync());
        var stuck2 = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.TryEnterWriteGuardAsync(Timeout.InfiniteTimeSpan));
        var writer = rwLock.EnterWriteGuardAsync();
        rwLock.WaitingWriteCount.ShouldBe(3);

        holder.Dispose();

        writer.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.IsHeldForWrite.ShouldBeTrue();
        rwLock.WaitingWriteCount.ShouldBe(0);
        stuck1.IsCompleted.ShouldBeFalse();
        stuck2.IsCompleted.ShouldBeFalse();

        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public void ReadRelease_WriterThatCannotBeResumed_DoesNotThrowAndPassesTheLockOn(AsyncReadWriteLockPreference preference)
    {
        var rwLock = new AsyncReadWriteLock(preference);
        var reader = rwLock.TryEnterReadGuard();

        var stuck = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.EnterWriteGuardAsync());
        var writer = rwLock.EnterWriteGuardAsync();
        rwLock.WaitingWriteCount.ShouldBe(2);

        reader.Dispose();

        writer.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.IsHeldForWrite.ShouldBeTrue();
        rwLock.WaitingWriteCount.ShouldBe(0);
        stuck.IsCompleted.ShouldBeFalse();

        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void ManyWaitersThatCannotBeResumed_AreAllPassedOver()
    {
        const int StuckCount = 20_000;

        var rwLock = new AsyncReadWriteLock();
        var holder = rwLock.TryEnterWriteGuard();
        var context = new ThrowingSynchronizationContext();

        // Each one is granted the lock in turn and gives it back. That must not nest a call for each of them.
        for (int i = 0; i < StuckCount; i++)
            _ = AwaitOn(context, () => rwLock.EnterWriteGuardAsync());

        var writer = rwLock.EnterWriteGuardAsync();
        rwLock.WaitingWriteCount.ShouldBe(StuckCount + 1);

        holder.Dispose();

        writer.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.WaitingWriteCount.ShouldBe(0);
        writer.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TimeLimit_WaiterBehindCannotBeResumed_TimerCallbackDoesNotThrow()
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);
        var reader1 = rwLock.TryEnterReadGuard();

        var writer = rwLock.TryEnterWriteGuardAsync(Limit);
        var stuck = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.EnterReadGuardAsync());
        var reader2 = rwLock.EnterReadGuardAsync();
        rwLock.WaitingReadCount.ShouldBe(2);

        // The writer gives up, which lets the readers behind it in. This thread is the timer's: an exception here ends the process when the timer
        // is a real one.
        time.Advance(Limit);

        writer.Result.IsEntered.ShouldBeFalse();
        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(2);
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(0);
        stuck.IsCompleted.ShouldBeFalse();

        reader1.Dispose();
        reader2.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Cancellation_WaiterBehindCannotBeResumed_CancelDoesNotThrow()
    {
        var rwLock = new AsyncReadWriteLock(Fair);
        var reader1 = rwLock.TryEnterReadGuard();

        using var cts = new CancellationTokenSource();
        var writer = rwLock.EnterWriteGuardAsync(cts.Token);
        var stuck = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.EnterReadGuardAsync());
        var reader2 = rwLock.EnterReadGuardAsync();

        // An exception from the callback would come out of Cancel as an AggregateException, to code that has nothing to do with the reader.
        cts.Cancel();

        writer.IsCanceled.ShouldBeTrue();
        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(2);
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(0);
        stuck.IsCompleted.ShouldBeFalse();

        reader1.Dispose();
        reader2.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TimeLimitOrCancellation_OfWaitThatCannotBeResumed_DoesNotThrow()
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);
        var holder = rwLock.TryEnterWriteGuard();

        // Here it is the wait that fails whose own continuation is refused.
        using var cts = new CancellationTokenSource();
        var timedOut = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.TryEnterReadGuardAsync(Limit));
        var canceled = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.EnterWriteGuardAsync(cts.Token));
        var reader = rwLock.EnterReadGuardAsync();

        time.Advance(Limit);
        cts.Cancel();

        rwLock.WaitingReadCount.ShouldBe(1);
        rwLock.WaitingWriteCount.ShouldBe(0);
        rwLock.IsHeldForWrite.ShouldBeTrue();
        time.PendingTimerCount.ShouldBe(0);
        timedOut.IsCompleted.ShouldBeFalse();
        canceled.IsCompleted.ShouldBeFalse();

        holder.Dispose();
        reader.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Enter_WhileCompletingWaiterAheadFails_KeepsItsOwnWait()
    {
        // The timer of the writer's wait throws when it is disposed, which is when the writer is granted the lock.
        var time = new DisposeThrowsTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);

        var reader1 = rwLock.TryEnterReadGuard();
        var writer = rwLock.TryEnterWriteGuardAsync(TimeSpan.FromMinutes(1));
        ValueTask<AsyncReadGuard> reader2 = default;

        // The last reader has left and has not yet handed the lock to the writer. A reader that arrives now does that for it, on its own thread,
        // and is then queued behind the writer.
        InWindowAfterLastReadRelease(rwLock, () => reader1.Dispose(), () => reader2 = rwLock.EnterReadGuardAsync());

        // The arriving reader was not thrown at, has its task, and is not lost: it is next.
        writer.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.IsHeldForWrite.ShouldBeTrue();
        reader2.IsCompleted.ShouldBeFalse();
        rwLock.WaitingReadCount.ShouldBe(1);
        time.Inner.PendingTimerCount.ShouldBe(0);

        writer.Result.Dispose();

        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(1);
        rwLock.WaitingReadCount.ShouldBe(0);

        NonCopyable.Move(ref reader2).Result.Dispose();
        ShouldBeFree(rwLock);
        rwLock.TryEnterWriteGuard().IsEntered.ShouldBeTrue();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Enter_WhileWaiterAheadCannotBeResumed_GetsTheLockInItsPlace(bool withTimeLimit)
    {
        var time = new ManualTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);

        var reader1 = rwLock.TryEnterReadGuard();
        var stuck = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.EnterWriteGuardAsync());
        ValueTask<AsyncReadGuard> reader2 = default;

        // As above, but here the writer cannot be resumed at all. Its hold is taken back by the arriving reader's call, which grants the lock to
        // the reader itself, after it was seen queued and before its timer and registration are made.
        using var cts = new CancellationTokenSource();

        InWindowAfterLastReadRelease(
            rwLock,
            () => reader1.Dispose(),
            () => reader2 = withTimeLimit ? rwLock.TryEnterReadGuardAsync(Limit, cts.Token) : rwLock.EnterReadGuardAsync());

        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(1);
        rwLock.IsHeldForWrite.ShouldBeFalse();
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(0);
        time.PendingTimerCount.ShouldBe(0);
        stuck.IsCompleted.ShouldBeFalse();

        // Nothing of the wait is left to act on the hold.
        cts.Cancel();
        time.Advance(Limit);

        var guard = NonCopyable.Move(ref reader2).Result;
        guard.IsEntered.ShouldBeTrue();
        guard.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void Enter_ThatCannotBeSetUp_WhileWaiterBehindCannotBeResumed_FailsWithItsOwnException()
    {
        AsyncReadWriteLock rwLock = null!;
        Task<bool>? stuck = null;
        ValueTask<AsyncReadGuard> reader2 = default;

        // While the writer's wait is set up, two readers queue behind it. Then the time provider throws, the writer is taken out of the queue, and
        // the readers are let in, the first of which cannot be resumed.
        var time = new ThrowingTimeProvider(() => {
            stuck = AwaitOn(new ThrowingSynchronizationContext(), () => rwLock.EnterReadGuardAsync());
            reader2 = rwLock.EnterReadGuardAsync();
        });

        rwLock = new AsyncReadWriteLock(Fair, time);
        var reader1 = rwLock.TryEnterReadGuard();

        var writer = rwLock.TryEnterWriteGuardAsync(Limit);

        // The writer's failure is the time provider's, which is its own, and not the context's of the reader.
        ShouldBeFaulted<NotSupportedException>(writer.AsTask()).Message.ShouldBe("No timers.");

        reader2.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(2);
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(0);
        stuck.ShouldNotBeNull().IsCompleted.ShouldBeFalse();

        reader1.Dispose();
        NonCopyable.Move(ref reader2).Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TimerThatThrowsWhenDisposed_IsIgnoredOnEveryPath()
    {
        var time = new DisposeThrowsTimeProvider();
        var rwLock = new AsyncReadWriteLock(Fair, time);
        var holder = rwLock.TryEnterWriteGuard();

        // A wait that ends at its time limit, one that is canceled and one that is granted the lock each dispose their timer.
        using var cts = new CancellationTokenSource();
        var timedOut = rwLock.TryEnterReadGuardAsync(Limit);
        var canceled = rwLock.TryEnterReadGuardAsync(Limit + Limit, cts.Token);
        var granted = rwLock.TryEnterReadGuardAsync(Limit + Limit);
        time.Inner.PendingTimerCount.ShouldBe(3);

        time.Inner.Advance(Limit);
        timedOut.Result.IsEntered.ShouldBeFalse();

        cts.Cancel();
        canceled.IsCanceled.ShouldBeTrue();

        holder.Dispose();
        granted.IsCompletedSuccessfully.ShouldBeTrue();
        time.Inner.PendingTimerCount.ShouldBe(0);
        time.DisposeCount.ShouldBe(3);

        var guard = granted.Result;
        guard.IsEntered.ShouldBeTrue();
        guard.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TimerThatThrowsWhenDisposed_AfterLockWasGrantedDuringSetUp_LeavesCallerItsGuard(bool write)
    {
        AsyncWriteGuard holder = default;

        // The holder leaves while the timer of the wait is created, so the wait is granted the lock before it has its timer, and the call that
        // made the timer disposes it. That throws, and must not cost the caller the task of a wait that holds the lock.
        var time = new DisposeThrowsTimeProvider(() => holder.Dispose());
        var rwLock = new AsyncReadWriteLock(Fair, time);
        holder = rwLock.TryEnterWriteGuard();

        var wait = new Wait(rwLock, write, Limit, CancellationToken.None);

        holder.IsDisposed.ShouldBeTrue();
        wait.IsCompleted.ShouldBeTrue();
        time.DisposeCount.ShouldBe(1);
        time.Inner.PendingTimerCount.ShouldBe(0);

        wait.TakeGuard().ShouldBeTrue();
        wait.Release();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    public void TaskSchedulerThatRefusesContinuation_DoesNotThrowIntoRelease()
    {
        var rwLock = new AsyncReadWriteLock();
        var scheduler = new QueueingTaskScheduler();
        var holder = rwLock.TryEnterWriteGuard();

        var stuck = Task.Factory.StartNew(
            async () => {
                using var guard = await rwLock.EnterReadGuardAsync();
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            scheduler).Unwrap();

        scheduler.RunQueued();
        rwLock.WaitingReadCount.ShouldBe(1);
        var reader = rwLock.EnterReadGuardAsync();

        scheduler.Refuse = true;
        holder.Dispose();

        reader.IsCompletedSuccessfully.ShouldBeTrue();
        rwLock.CurrentReadCount.ShouldBe(1);
        rwLock.WaitingReadCount.ShouldBe(0);
        stuck.IsCompleted.ShouldBeFalse();

        reader.Result.Dispose();
        ShouldBeFree(rwLock);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WaitWhoseHoldWasTakenBack_HasNoResult(bool write)
    {
        var rwLock = new AsyncReadWriteLock();
        var holder = rwLock.TryEnterWriteGuard();
        var wait = new Wait(rwLock, write, Timeout.InfiniteTimeSpan, CancellationToken.None);
        bool resumed = false;

        OnContext(new ThrowingSynchronizationContext(), () => wait.OnCompleted(() => resumed = true));

        holder.Dispose();
        resumed.ShouldBeFalse();
        ShouldBeFree(rwLock);

        // The task is complete, and a caller that looks at it after all gets no guard for a hold that is gone.
        wait.IsCompleted.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => wait.TakeGuard());
        ShouldBeFree(rwLock);
        rwLock.TryEnterWriteGuard().IsEntered.ShouldBeTrue();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ContextThatRunsContinuationAndThenThrows_LeavesHoldWithItsGuard(bool write)
    {
        var rwLock = new AsyncReadWriteLock();
        var holder = rwLock.TryEnterWriteGuard();
        var wait = new Wait(rwLock, write, Timeout.InfiniteTimeSpan, CancellationToken.None);
        bool entered = false;

        // This context runs what it is given and then fails. The continuation has taken the guard by then, so the hold is the guard's to release
        // and the lock must not take it back as well.
        OnContext(new RunsThenThrowsSynchronizationContext(), () => wait.OnCompleted(() => entered = wait.TakeGuard()));

        holder.Dispose();

        entered.ShouldBeTrue();
        (write ? rwLock.IsHeldForWrite : rwLock.IsHeldForRead).ShouldBeTrue();

        wait.Release();
        ShouldBeFree(rwLock);
    }

    #endregion

    #region Races between a grant and a failure

    [TestMethod]
    [DoNotParallelize]
    [DataRow(Fair, false)]
    [DataRow(Fair, true)]
    [DataRow(Readers, false)]
    [DataRow(Readers, true)]
    [DataRow(Writers, false)]
    [DataRow(Writers, true)]
    public void Race_GrantAgainstCancellation_HasOneOutcome(AsyncReadWriteLockPreference preference, bool write)
    {
        using var canceler = new RaceThread();

        var stopwatch = Stopwatch.StartNew();

        int i;

        for (i = 0; KeepRacing(i, stopwatch); i++)
        {
            var rwLock = new AsyncReadWriteLock(preference);
            var holder = rwLock.TryEnterWriteGuard();
            using var cts = new CancellationTokenSource();

            var wait = new Wait(rwLock, write, Timeout.InfiniteTimeSpan, cts.Token);
            wait.IsCompleted.ShouldBeFalse();

            canceler.Start(cts.Cancel, i % 23);
            Thread.SpinWait((i / 23) % 23);
            holder.Dispose();
            canceler.Join();

            // Both have returned, so the wait has its outcome: the lock, or the cancellation, and never both or neither.
            wait.IsCompleted.ShouldBeTrue();

            if (wait.IsCanceled)
            {
                ShouldBeFree(rwLock);
                wait.ShouldThrowCanceled(cts.Token);
            }
            else
            {
                wait.TakeGuard().ShouldBeTrue();
                wait.Release();
            }

            ShouldBeFree(rwLock);
            rwLock.TryEnterWriteGuard().IsEntered.ShouldBeTrue();
        }

        ReportIterations(i);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(Fair, false)]
    [DataRow(Fair, true)]
    [DataRow(Readers, false)]
    [DataRow(Readers, true)]
    [DataRow(Writers, false)]
    [DataRow(Writers, true)]
    public void Race_GrantAgainstTimeLimit_HasOneOutcome(AsyncReadWriteLockPreference preference, bool write)
    {
        using var clock = new RaceThread();

        var stopwatch = Stopwatch.StartNew();

        int i;

        for (i = 0; KeepRacing(i, stopwatch); i++)
        {
            var time = new ManualTimeProvider();
            var rwLock = new AsyncReadWriteLock(preference, time);
            var holder = rwLock.TryEnterWriteGuard();

            var wait = new Wait(rwLock, write, Limit, CancellationToken.None);
            wait.IsCompleted.ShouldBeFalse();

            clock.Start(() => time.Advance(Limit), i % 23);
            Thread.SpinWait((i / 23) % 23);
            holder.Dispose();
            clock.Join();

            // Both have returned, so the wait has its outcome: a guard that entered the lock and holds it, or one that did not and the lock is free.
            wait.IsCompleted.ShouldBeTrue();
            time.PendingTimerCount.ShouldBe(0);

            if (!wait.TakeGuard())
                ShouldBeFree(rwLock);

            wait.Release();
            ShouldBeFree(rwLock);
            rwLock.TryEnterWriteGuard().IsEntered.ShouldBeTrue();
        }

        ReportIterations(i);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public void Race_GrantAgainstCancellationAndTimeLimit_HasOneOutcome(bool write)
    {
        using var canceler = new RaceThread();
        using var clock = new RaceThread();

        var stopwatch = Stopwatch.StartNew();

        int i;

        for (i = 0; KeepRacing(i, stopwatch); i++)
        {
            var time = new ManualTimeProvider();
            var rwLock = new AsyncReadWriteLock(Fair, time);
            var holder = rwLock.TryEnterWriteGuard();
            using var cts = new CancellationTokenSource();

            // A second waiter behind the first shows that the lock goes on to it whatever happens to the first.
            var wait = new Wait(rwLock, write, Limit, cts.Token);
            var next = rwLock.EnterWriteGuardAsync();

            canceler.Start(cts.Cancel, i % 17);
            clock.Start(() => time.Advance(Limit), (i / 17) % 17);
            Thread.SpinWait((i / 289) % 17);
            holder.Dispose();
            canceler.Join();
            clock.Join();

            wait.IsCompleted.ShouldBeTrue();
            time.PendingTimerCount.ShouldBe(0);

            if (!wait.IsCanceled && wait.TakeGuard())
            {
                // The first waiter holds the lock, so the one behind it is still waiting.
                next.IsCompleted.ShouldBeFalse();
                wait.Release();
            }

            next.IsCompletedSuccessfully.ShouldBeTrue();
            rwLock.CurrentReadCount.ShouldBe(0);
            rwLock.IsHeldForWrite.ShouldBeTrue();
            next.Result.Dispose();
            ShouldBeFree(rwLock);
        }

        ReportIterations(i);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public void Race_ReleaseWhileWaitIsSetUp_GrantsAndLeavesNoTimer(bool write)
    {
        using var releaser = new RaceThread();

        var stopwatch = Stopwatch.StartNew();

        int i;

        for (i = 0; KeepRacing(i, stopwatch); i++)
        {
            var time = new ManualTimeProvider();
            var rwLock = new AsyncReadWriteLock(Fair, time);
            var holder = rwLock.TryEnterWriteGuard();
            using var cts = new CancellationTokenSource();

            // The release lands before the call, while it queues the waiter, or while it creates the timer and registers with the token.
            releaser.Start(() => holder.Dispose(), i % 23);
            Thread.SpinWait((i / 23) % 23);
            var wait = new Wait(rwLock, write, Limit, cts.Token);
            releaser.Join();

            wait.IsCompleted.ShouldBeTrue();
            time.PendingTimerCount.ShouldBe(0);

            // Nothing of the wait is left behind to act on the hold.
            cts.Cancel();
            time.Advance(Limit);

            wait.TakeGuard().ShouldBeTrue();
            wait.Release();
            ShouldBeFree(rwLock);
        }

        ReportIterations(i);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(Fair, false)]
    [DataRow(Fair, true)]
    [DataRow(Readers, true)]
    [DataRow(Writers, false)]
    [DataRow(Writers, true)]
    public void Race_ArrivalWhileLockIsReleased_DoesNotPassWaitingWriter(AsyncReadWriteLockPreference preference, bool write)
    {
        using var releaser = new RaceThread();
        var stopwatch = Stopwatch.StartNew();

        int i;

        for (i = 0; KeepRacing(i, stopwatch); i++)
        {
            var rwLock = new AsyncReadWriteLock(preference);
            var holder = rwLock.TryEnterReadGuard();
            var first = rwLock.EnterWriteGuardAsync();
            first.IsCompleted.ShouldBeFalse();

            // The late arrival lands before the release, after it, or between the moment the last reader is out and the moment the lock is handed
            // to the waiting writer. The lock is free for that moment, and the arrival still must not take it.
            releaser.Start(() => holder.Dispose(), i % 23);
            Thread.SpinWait((i / 23) % 23);

            bool lateEntered;
            ValueTask<AsyncWriteGuard> lateWriter = default;

            if (!write)
                lateEntered = rwLock.TryEnterReadGuard().IsEntered;
            else if ((i & 1) is 0)
                lateEntered = rwLock.TryEnterWriteGuard().IsEntered;
            else
                lateEntered = (lateWriter = rwLock.EnterWriteGuardAsync()).IsCompleted;

            releaser.Join();

            lateEntered.ShouldBeFalse();
            first.IsCompletedSuccessfully.ShouldBeTrue();
            rwLock.IsHeldForWrite.ShouldBeTrue();
            rwLock.CurrentReadCount.ShouldBe(0);

            first.Result.Dispose();

            if (write && (i & 1) is not 0)
            {
                // The late writer that waited is next, and was not lost.
                lateWriter.IsCompletedSuccessfully.ShouldBeTrue();
                lateWriter.Result.Dispose();
            }

            ShouldBeFree(rwLock);
        }

        ReportIterations(i);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public void Race_TwoThreadsDisposeOneGuard_ReleaseOnce(bool write)
    {
        using var other = new RaceThread();
        var rwLock = new AsyncReadWriteLock();

        // The guard is in an array so that both threads dispose the same instance and not a copy each.
        var readGuards = new AsyncReadGuard[1];
        var writeGuards = new AsyncWriteGuard[1];

        var stopwatch = Stopwatch.StartNew();
        int i;

        for (i = 0; KeepRacing(i, stopwatch); i++)
        {
            if (write)
            {
                writeGuards[0] = rwLock.TryEnterWriteGuard();
                writeGuards[0].IsEntered.ShouldBeTrue();

                other.Start(() => writeGuards[0].Dispose(), i % 11);
                Thread.SpinWait((i / 11) % 11);
                writeGuards[0].Dispose();
                other.Join();

                writeGuards[0].IsDisposed.ShouldBeTrue();
            }
            else
            {
                // A second release would take the hold of the reader beside it.
                var bystander = rwLock.TryEnterReadGuard();
                readGuards[0] = rwLock.TryEnterReadGuard();
                readGuards[0].IsEntered.ShouldBeTrue();

                other.Start(() => readGuards[0].Dispose(), i % 11);
                Thread.SpinWait((i / 11) % 11);
                readGuards[0].Dispose();
                other.Join();

                readGuards[0].IsDisposed.ShouldBeTrue();
                rwLock.CurrentReadCount.ShouldBe(1);
                bystander.Dispose();
            }

            ShouldBeFree(rwLock);
        }

        ReportIterations(i);
    }

    #endregion

    #region Stress

    [TestMethod]
    [DoNotParallelize]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public async Task Stress_ManyReadersAndWriters_KeepInvariantAndLoseNothing(AsyncReadWriteLockPreference preference)
    {
        const int WorkerCount = 12;
        const int Iterations = 40_000;

        var rwLock = new AsyncReadWriteLock(preference);
        var toCancel = new ConcurrentQueue<CancellationTokenSource>();

        int readersInside = 0;
        int writersInside = 0;
        int violations = 0;
        int countViolations = 0;
        long entered = 0;
        long notEntered = 0;
        long canceled = 0;
        long protectedValue = 0;
        long writes = 0;
        bool stop = false;

        // Cancels the tokens of waits as they are made, without a pause, so that cancellations land before a wait, during it and after it. It also
        // watches the counts: each worker has one call in progress at a time, so none of them can be below zero or above the number of workers.
        var canceler = new Thread(() => {
            while (!Volatile.Read(ref stop))
            {
                if ((uint)rwLock.CurrentReadCount > WorkerCount || (uint)rwLock.WaitingReadCount > WorkerCount ||
                    (uint)rwLock.WaitingWriteCount > WorkerCount)
                {
                    Interlocked.Increment(ref countViolations);
                }

                if (toCancel.TryDequeue(out var cts))
                    cts.Cancel();
                else
                    Thread.Yield();
            }
        });

        canceler.Start();

        var workers = Enumerable.Range(0, WorkerCount).Select(worker => Task.Run(async () => {
            var random = new Random(worker * 7919);

            for (int i = 0; i < Iterations; i++)
            {
                bool write = random.Next(100) < 10;
                int form = random.Next(7);
                CancellationTokenSource? cts = null;
                var token = CancellationToken.None;

                if (form >= 5)
                {
                    cts = new CancellationTokenSource();
                    token = cts.Token;
                    toCancel.Enqueue(cts);
                }

                var timeout = form switch {
                    0 or 5 => Timeout.InfiniteTimeSpan,
                    1 => TimeSpan.Zero,
                    2 => TimeSpan.FromTicks(1),
                    3 or 6 => TimeSpan.FromMilliseconds(1 + random.Next(3)),
                    _ => default,
                };

                AsyncReadGuard readGuard = default;
                AsyncWriteGuard writeGuard = default;
                bool isEntered;

                try
                {
                    if (write)
                    {
                        writeGuard = form switch {
                            0 => await rwLock.EnterWriteGuardAsync(),
                            4 => rwLock.TryEnterWriteGuard(),
                            5 => await rwLock.EnterWriteGuardAsync(token),
                            _ => await rwLock.TryEnterWriteGuardAsync(timeout, token),
                        };

                        isEntered = writeGuard.IsEntered;
                    }
                    else
                    {
                        readGuard = form switch {
                            0 => await rwLock.EnterReadGuardAsync(),
                            4 => rwLock.TryEnterReadGuard(),
                            5 => await rwLock.EnterReadGuardAsync(token),
                            _ => await rwLock.TryEnterReadGuardAsync(timeout, token),
                        };

                        isEntered = readGuard.IsEntered;
                    }
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref canceled);
                    continue;
                }

                if (!isEntered)
                {
                    Interlocked.Increment(ref notEntered);
                    (write ? writeGuard.IsDefault : readGuard.IsDefault).ShouldBeFalse();
                    continue;
                }

                Interlocked.Increment(ref entered);

                if (write)
                {
                    if (Interlocked.Increment(ref writersInside) is not 1 || Volatile.Read(ref readersInside) is not 0)
                        Interlocked.Increment(ref violations);

                    // Not atomic on purpose: the lock alone keeps the value right.
                    long value = protectedValue;

                    if (random.Next(4) is 0)
                        await Task.Yield();
                    else
                        Thread.SpinWait(random.Next(200));

                    protectedValue = value + 1;
                    writes++;

                    Interlocked.Decrement(ref writersInside);
                    writeGuard.Dispose();
                }
                else
                {
                    Interlocked.Increment(ref readersInside);

                    if (Volatile.Read(ref writersInside) is not 0)
                        Interlocked.Increment(ref violations);

                    long value = Volatile.Read(ref protectedValue);

                    if (random.Next(4) is 0)
                        await Task.Yield();
                    else
                        Thread.SpinWait(random.Next(200));

                    if (Volatile.Read(ref protectedValue) != value || Volatile.Read(ref writersInside) is not 0)
                        Interlocked.Increment(ref violations);

                    Interlocked.Decrement(ref readersInside);
                    readGuard.Dispose();
                }
            }
        })).ToArray();

        try
        {
            // This is a test of safety. It says little about liveness: a waiter that a release missed is let in by the next release, of which there
            // are always more here. Stress_SingleShotRounds_LeaveNoWaiterBehind is the test for that.
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromMinutes(5));
        }
        finally
        {
            Volatile.Write(ref stop, true);
            canceler.Join();
        }

        violations.ShouldBe(0);
        countViolations.ShouldBe(0);
        (entered + notEntered + canceled).ShouldBe((long)WorkerCount * Iterations);
        protectedValue.ShouldBe(writes);
        entered.ShouldBeGreaterThan(0);

        // Every wait has ended, so nothing is queued: a waiter that was counted and not removed, or removed twice, shows here.
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(0);

        // A hold that was granted twice, or released twice, would leave the lock held or the count wrong.
        ShouldBeFree(rwLock);
        rwLock.TryEnterWriteGuard().IsEntered.ShouldBeTrue();

        Console.WriteLine($"entered {entered}, not entered {notEntered}, canceled {canceled}, writes {writes}");
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public async Task Stress_SingleShotRounds_LeaveNoWaiterBehind(AsyncReadWriteLockPreference preference)
    {
        const int Callers = 6;

        // A test of liveness. In each round a few callers enter the lock once, waiting without a limit, and leave. After the last release of a round
        // nothing else comes along, so a waiter that a release missed, or that was queued and not looked at again, waits forever and the round
        // does not end.
        var rwLock = new AsyncReadWriteLock(preference);
        var tasks = new Task[Callers];
        var stopwatch = Stopwatch.StartNew();
        int round;

        for (round = 0; KeepRacing(round, stopwatch); round++)
        {
            for (int caller = 0; caller < Callers; caller++)
            {
                bool write = ((round + caller) % 3) is 0;
                int spins = (round / (caller + 1)) % 16;

                tasks[caller] = Task.Run(async () => {
                    Thread.SpinWait(spins);

                    if (write)
                    {
                        using var guard = await rwLock.EnterWriteGuardAsync();
                        Thread.SpinWait(spins);
                    }
                    else
                    {
                        using var guard = await rwLock.EnterReadGuardAsync();
                        Thread.SpinWait(spins);
                    }
                });
            }

            var all = Task.WhenAll(tasks);

            try
            {
                await all.WaitAsync(LivenessTimeout);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Round {round} did not end: {tasks.Count(t => !t.IsCompleted)} callers are still waiting, with {rwLock.CurrentReadCount} read " +
                    $"holds, write held {rwLock.IsHeldForWrite}, {rwLock.WaitingReadCount} readers and {rwLock.WaitingWriteCount} writers queued.");
            }

            ShouldBeFree(rwLock);
        }

        ReportIterations(round);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(Fair)]
    [DataRow(Readers)]
    [DataRow(Writers)]
    public async Task Stress_WaitersThatCannotBeResumed_KeepExclusionAndLeaveLockFree(AsyncReadWriteLockPreference preference)
    {
        const int Workers = 8;
        const int OperationsPerWorker = 30_000;

        // The give-back of a hold whose waiter cannot be resumed, with several threads entering and leaving at once. A hold that is given back twice,
        // or given back while its guard is out, lets a writer in next to a reader or another writer; a hold that is neither given back nor released
        // leaves the lock held at the end.
        var rwLock = new AsyncReadWriteLock(preference);
        var resumed = new ConcurrentBag<Task>();
        int readersInside = 0;
        int writersInside = 0;
        int violations = 0;
        int entered = 0;
        int takenBack = 0;
        int unexpected = 0;

        void Inside(bool write)
        {
            if (write)
            {
                if (Interlocked.Increment(ref writersInside) is not 1 || Volatile.Read(ref readersInside) is not 0)
                    Interlocked.Increment(ref violations);
            }
            else
            {
                Interlocked.Increment(ref readersInside);

                if (Volatile.Read(ref writersInside) is not 0)
                    Interlocked.Increment(ref violations);
            }

            Thread.SpinWait(Random.Shared.Next(100));

            if (write)
            {
                if (Volatile.Read(ref readersInside) is not 0)
                    Interlocked.Increment(ref violations);

                Interlocked.Decrement(ref writersInside);
            }
            else
            {
                if (Volatile.Read(ref writersInside) is not 0)
                    Interlocked.Increment(ref violations);

                Interlocked.Decrement(ref readersInside);
            }
        }

        async Task EnterOnceAsync(bool write)
        {
            try
            {
                if (write)
                {
                    using var guard = await rwLock.EnterWriteGuardAsync();
                    Inside(write: true);
                }
                else
                {
                    using var guard = await rwLock.EnterReadGuardAsync();
                    Inside(write: false);
                }

                Interlocked.Increment(ref entered);
            }
            catch (InvalidOperationException)
            {
                // The context queued the continuation and threw, and the lock took the hold back before the continuation took the guard.
                Interlocked.Increment(ref takenBack);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref unexpected);
            }
        }

        Task StartOn(SynchronizationContext context, bool write)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);

            try
            {
                return EnterOnceAsync(write);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        var workers = Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker);

            for (int i = 0; i < OperationsPerWorker; i++)
            {
                bool write = random.Next(100) < 15;
                int kind = random.Next(10);

                if (kind is 0)
                {
                    // Normally never resumed, so its task does not end and is not waited for.
                    _ = StartOn(new FailsForOtherThreadsSynchronizationContext(PostFailure.Refuse), write);
                }
                else if (kind is 1)
                {
                    resumed.Add(StartOn(new FailsForOtherThreadsSynchronizationContext(PostFailure.QueueThenThrow), write));
                }
                else if (kind is 2)
                {
                    resumed.Add(StartOn(new FailsForOtherThreadsSynchronizationContext(PostFailure.RunThenThrow), write));
                }
                else if (write)
                {
                    using var guard = await rwLock.EnterWriteGuardAsync().ConfigureAwait(false);
                    Inside(write: true);
                }
                else
                {
                    using var guard = await rwLock.EnterReadGuardAsync().ConfigureAwait(false);
                    Inside(write: false);
                }
            }
        })).ToArray();

        // A hold that nobody can release leaves the workers waiting, so this is where such a defect shows. A run takes a few seconds.
        await Task.WhenAll(workers).WaitAsync(TestTimeout);
        await Task.WhenAll(resumed).WaitAsync(TestTimeout);

        // A caller on a refusing context that its own thread resumed is not in the list and can still be inside for a moment.
        SpinWait.SpinUntil(() => rwLock.CurrentReadCount is 0 && !rwLock.IsHeldForWrite, LivenessTimeout);

        Console.WriteLine($"entered on a failing context {entered}, hold taken back before the guard was taken {takenBack}");

        Volatile.Read(ref violations).ShouldBe(0);
        Volatile.Read(ref unexpected).ShouldBe(0);
        ShouldBeFree(rwLock);
        rwLock.TryEnterWriteGuard().IsEntered.ShouldBeTrue();
    }

    #endregion

    #region Helpers

    private static void ShouldBeFree(AsyncReadWriteLock rwLock)
    {
        rwLock.IsHeldForRead.ShouldBeFalse();
        rwLock.IsHeldForWrite.ShouldBeFalse();
        rwLock.CurrentReadCount.ShouldBe(0);
        rwLock.WaitingReadCount.ShouldBe(0);
        rwLock.WaitingWriteCount.ShouldBe(0);
    }

    /// <summary>
    /// Awaits a wait and returns whether its continuation ran on a thread while that thread was releasing the lock, canceling or advancing the time.
    /// </summary>
    private static async Task<bool> WaitAndObserveAsync<[NonCopyable] TGuard>(ValueTask<TGuard> wait) where TGuard : struct, IDisposable
    {
        try
        {
            var guard = await wait.ConfigureAwait(false);
            bool ranInline = _resolvingOnThisThread;
            guard.Dispose();
            return ranInline;
        }
        catch (OperationCanceledException)
        {
            return _resolvingOnThisThread;
        }
    }

    /// <summary>
    /// Starts the specified number of waits, one after the other.
    /// </summary>
    private static ValueTask<TGuard>[] EnterMany<[NonCopyable] TGuard>(int count, Func<ValueTask<TGuard>> enter)
    {
        var waits = new ValueTask<TGuard>[count];

        for (int i = 0; i < count; i++)
            waits[i] = enter();

        return waits;
    }

    // These read the waits in place: LINQ and Shouldly would take a copy of each task.
    private static bool[] Completed<[NonCopyable] TGuard>(ValueTask<TGuard>[] waits)
    {
        bool[] completed = new bool[waits.Length];

        for (int i = 0; i < waits.Length; i++)
            completed[i] = waits[i].IsCompleted;

        return completed;
    }

    private static bool[] CompletedSuccessfully<[NonCopyable] TGuard>(ValueTask<TGuard>[] waits)
    {
        bool[] completed = new bool[waits.Length];

        for (int i = 0; i < waits.Length; i++)
            completed[i] = waits[i].IsCompletedSuccessfully;

        return completed;
    }

    // A race goes on until it has been repeated RaceIterations times or RaceTimeBudget has passed, and never stops before MinRaceIterations: a
    // machine that is loaded takes longer, it does not test less.
    private static bool KeepRacing(int iteration, Stopwatch stopwatch) =>
        iteration < MinRaceIterations || (iteration < RaceIterations && stopwatch.Elapsed < RaceTimeBudget);

    // Puts the number of times a race was run in the output of the test.
    private static void ReportIterations(int iterations)
    {
        Console.WriteLine($"{iterations} iterations");
        iterations.ShouldBeGreaterThanOrEqualTo(MinRaceIterations);
    }

    private static TException ShouldBeFaulted<TException>(Task task) where TException : Exception
    {
        task.IsFaulted.ShouldBeTrue();
        return task.Exception.ShouldNotBeNull().InnerException.ShouldBeOfType<TException>();
    }

    /// <summary>
    /// Runs an action with the specified synchronization context as the current one, so that what the action awaits captures it.
    /// </summary>
    private static void OnContext(SynchronizationContext context, Action action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Starts a method that awaits the lock on the specified synchronization context and releases the lock when it continues. The task completes
    /// when the method has continued.
    /// </summary>
    private static Task<bool> AwaitOn<[NonCopyable] TGuard>(SynchronizationContext context, Func<ValueTask<TGuard>> enter) where TGuard : struct, IDisposable
    {
        Task<bool>? task = null;
        OnContext(context, () => task = WaitAsync());
        return task!;

        async Task<bool> WaitAsync()
        {
            using var guard = await enter();
            return true;
        }
    }

    /// <summary>
    /// Runs an action at the moment when the last read hold has been released and the release has not yet handed the lock to the waiters: the
    /// thread that releases is held up at the lock's internal lock, which this thread holds meanwhile. Without this the moment is a race that is
    /// rarely hit.
    /// </summary>
    private static void InWindowAfterLastReadRelease(AsyncReadWriteLock rwLock, Action releaseLastReadHold, Action action)
    {
        object sync = typeof(AsyncReadWriteLock).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull().GetValue(rwLock)!;
        Exception? releaseException = null;
        Thread releaser;

        lock (sync)
        {
            releaser = new Thread(() => {
                try
                {
                    releaseLastReadHold();
                }
                catch (Exception ex)
                {
                    releaseException = ex;
                }
            });

            releaser.Start();
            SpinWait.SpinUntil(() => rwLock.CurrentReadCount is 0, TestTimeout).ShouldBeTrue();

            // The internal lock can be entered again by the thread that holds it, so the action runs as the caller that got there first.
            action();
        }

        releaser.Join(TestTimeout).ShouldBeTrue();
        releaseException.ShouldBeNull();
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
        thread.Join(TestTimeout).ShouldBeTrue();

        return exception;
    }

    /// <summary>
    /// A wait for the lock in either mode, so that a test can be written once for both.
    /// </summary>
    private sealed class Wait
    {
        private readonly AsyncReadWriteLock _rwLock;
        private ValueTask<AsyncReadGuard> _read;
        private ValueTask<AsyncWriteGuard> _write;
        private readonly bool _isWrite;
        private AsyncReadGuard _readGuard;
        private AsyncWriteGuard _writeGuard;

        public Wait(AsyncReadWriteLock rwLock, bool write, TimeSpan timeout, CancellationToken cancellationToken)
        {
            _rwLock = rwLock;
            _isWrite = write;

            if (write)
                _write = rwLock.TryEnterWriteGuardAsync(timeout, cancellationToken);
            else
                _read = rwLock.TryEnterReadGuardAsync(timeout, cancellationToken);
        }

        public bool IsCompleted => _isWrite ? _write.IsCompleted : _read.IsCompleted;

        public bool IsCanceled => _isWrite ? _write.IsCanceled : _read.IsCanceled;

        /// <summary>
        /// Registers a continuation the way an await does, which captures the current synchronization context.
        /// </summary>
        /// <remarks>
        /// The awaiter is a copy of the task, as it is in an await. It only registers the continuation, and the guard is taken once, from the field,
        /// by <see cref="TakeGuard"/>.
        /// </remarks>
        public void OnCompleted(Action continuation)
        {
            if (_isWrite)
                _write.GetAwaiter().OnCompleted(continuation);
            else
                _read.GetAwaiter().OnCompleted(continuation);
        }

        public void ShouldThrowCanceled(CancellationToken cancellationToken)
        {
            try
            {
                if (_isWrite)
                    _ = NonCopyable.Move(ref _write).Result;
                else
                    _ = NonCopyable.Move(ref _read).Result;
            }
            catch (OperationCanceledException ex)
            {
                ex.CancellationToken.ShouldBe(cancellationToken);
                return;
            }

            throw new InvalidOperationException("The wait was not canceled.");
        }

        /// <summary>
        /// Takes the guard of the completed wait, which can be done once, and returns whether it entered the lock. If it did, the lock must be held
        /// in the mode of the wait and by nothing else.
        /// </summary>
        public bool TakeGuard()
        {
            bool entered;

            if (_isWrite)
            {
                _writeGuard = NonCopyable.Move(ref _write).Result;
                entered = _writeGuard.IsEntered;
            }
            else
            {
                _readGuard = NonCopyable.Move(ref _read).Result;
                entered = _readGuard.IsEntered;
            }

            if (entered)
            {
                _rwLock.IsHeldForWrite.ShouldBe(_isWrite);
                _rwLock.CurrentReadCount.ShouldBe(_isWrite ? 0 : 1);
            }

            return entered;
        }

        public void Release()
        {
            if (_isWrite)
                _writeGuard.Dispose();
            else
                _readGuard.Dispose();
        }
    }

    /// <summary>
    /// A thread that runs one action at a time as soon as it is given, so that it overlaps with what the test's thread does next.
    /// </summary>
    private sealed class RaceThread : IDisposable
    {
        private readonly Thread _thread;
        private Action? _action;
        private int _spins;
        private Exception? _exception;
        private volatile bool _busy;
        private volatile bool _stop;

        public RaceThread()
        {
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        public void Start(Action action, int spins)
        {
            _action = action;
            _spins = spins;
            _busy = true;
        }

        public void Join()
        {
            long started = Stopwatch.GetTimestamp();
            SpinWait spin = default;

            while (_busy)
            {
                // A race that ends in a deadlock fails its test instead of hanging the run.
                if (Stopwatch.GetElapsedTime(started) > TestTimeout)
                    throw new TimeoutException("The action of the race thread did not end.");

                spin.SpinOnce(sleep1Threshold: -1);
            }

            if (_exception is not null)
                throw new InvalidOperationException("The action of the race thread failed.", _exception);
        }

        public void Dispose()
        {
            // The thread is a background thread, so one that is stuck in its action is left behind and does not keep the test from ending.
            _stop = true;
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        private void Run()
        {
            // The wait for work yields rather than spinning on Thread.SpinWait alone: a thread in a tight loop of short spins is hard for the garbage
            // collector to suspend in optimized code, and every collection would stall the test.
            SpinWait idle = default;

            while (!_stop)
            {
                if (!_busy)
                {
                    idle.SpinOnce(sleep1Threshold: -1);
                    continue;
                }

                try
                {
                    Thread.SpinWait(_spins);
                    _action!();
                }
                catch (Exception ex)
                {
                    _exception = ex;
                }

                _busy = false;
            }
        }
    }

    private sealed class QueueingSynchronizationContext : SynchronizationContext
    {
        public Queue<(SendOrPostCallback Callback, object? State)> Posted { get; } = [];

        public override void Post(SendOrPostCallback d, object? state) => Posted.Enqueue((d, state));

        public void RunPosted()
        {
            while (Posted.TryDequeue(out var posted))
                posted.Callback(posted.State);
        }
    }

    private sealed class ThrowingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new NotSupportedException("Nothing can be posted.");
    }

    private sealed class RunsThenThrowsSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            d(state);
            throw new NotSupportedException("Posted, run and failed.");
        }
    }

    private enum PostFailure
    {
        Refuse,
        QueueThenThrow,
        RunThenThrow,
    }

    /// <summary>
    /// A context that fails when a thread other than the one that created it posts to it, which is the lock completing a wait.
    /// </summary>
    /// <remarks>
    /// A post from the thread that created it can be the awaiting code registering its continuation on a wait that was completed in that moment. A
    /// throw there is rethrown on the thread pool, as with any awaitable, and ends the test host, so such a post is queued as usual.
    /// </remarks>
    private sealed class FailsForOtherThreadsSynchronizationContext : SynchronizationContext
    {
        private readonly PostFailure _failure;
        private readonly int _owner = Environment.CurrentManagedThreadId;

        public FailsForOtherThreadsSynchronizationContext(PostFailure failure)
        {
            _failure = failure;
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (Environment.CurrentManagedThreadId == _owner)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => d(state), null);
                return;
            }

            if (_failure is PostFailure.QueueThenThrow)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ => d(state), null);
                Thread.SpinWait(Random.Shared.Next(50));
            }
            else if (_failure is PostFailure.RunThenThrow)
            {
                d(state);
            }

            throw new NotSupportedException("Posted from another thread and failed.");
        }
    }

    /// <summary>
    /// A task scheduler that runs its tasks when the test says so, and that can be made to refuse them.
    /// </summary>
    private sealed class QueueingTaskScheduler : TaskScheduler
    {
        private readonly ConcurrentQueue<Task> _tasks = new();

        public bool Refuse { get; set; }

        public int QueuedCount => _tasks.Count;

        public void RunQueued()
        {
            while (_tasks.TryDequeue(out var task))
                TryExecuteTask(task);
        }

        protected override void QueueTask(Task task)
        {
            if (Refuse)
                throw new NotSupportedException("Nothing can be queued.");

            _tasks.Enqueue(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => _tasks;
    }

    /// <summary>
    /// A time provider over a <see cref="ManualTimeProvider"/> whose timers throw when they are disposed, after they have been disposed.
    /// </summary>
    private sealed class DisposeThrowsTimeProvider(Action? afterCreating = null) : TimeProvider
    {
        private int _disposeCount;

        public ManualTimeProvider Inner { get; } = new();

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ThrowingTimer(this, Inner.CreateTimer(callback, state, dueTime, period));
            afterCreating?.Invoke();
            return timer;
        }

        private sealed class ThrowingTimer(DisposeThrowsTimeProvider owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                inner.Dispose();
                Interlocked.Increment(ref owner._disposeCount);
                throw new NotSupportedException("The timer cannot be disposed.");
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }

    private sealed class ThrowingTimeProvider(Action? beforeThrowing = null) : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            beforeThrowing?.Invoke();
            throw new NotSupportedException("No timers.");
        }
    }

    #endregion
}
