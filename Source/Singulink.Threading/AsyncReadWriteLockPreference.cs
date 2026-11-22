namespace Singulink.Threading;

/// <summary>
/// Specifies who enters an <see cref="AsyncReadWriteLock"/> first when readers and writers both want it.
/// </summary>
/// <remarks>
/// Under every preference, writers enter in the order they arrived, and a caller that enters while nothing is held and nothing is waiting does not wait.
/// The preferences differ in whether a reader that arrives while a writer is waiting enters ahead of it, and in who is next when the lock is released.
/// </remarks>
public enum AsyncReadWriteLockPreference
{
    /// <summary>
    /// Readers and writers enter in the order they arrived. This is the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reader enters at once only while no writer holds the lock and no writer is waiting; otherwise it waits behind the writers that arrived before
    /// it. Readers that are next to each other in arrival order enter together.</para>
    /// <para>
    /// Neither side can be kept waiting forever as long as every hold is released: a waiter is only ever behind the holds and the waiters that were
    /// there when it arrived.</para>
    /// <para>
    /// A caller that holds the lock in read mode and enters it in read mode again while a writer is waiting never gets in: the writer is waiting for
    /// the first hold.</para>
    /// </remarks>
    Fair,

    /// <summary>
    /// Readers enter whenever the lock is not held in write mode, ahead of any writer that is waiting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A waiting writer never holds a reader up. A reader waits only while a writer holds the lock, and when that writer leaves, every waiting reader
    /// enters before any waiting writer, so a reader waits for at most the rest of one write hold.</para>
    /// <para>
    /// Writers can be kept waiting forever: a writer enters only when no read hold is left, which never happens while read holds keep overlapping.
    /// Use this where writers are rare and can give up, with a time limit on their wait.</para>
    /// <para>
    /// A caller that holds the lock in read mode can enter it in read mode again without waiting, whatever is waiting.</para>
    /// </remarks>
    Readers,

    /// <summary>
    /// Writers enter ahead of any reader that is waiting, and readers that arrive while a writer is waiting wait behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A writer waits only for the holds that are there when it arrives and for the writers ahead of it. When the lock is released, the next waiting
    /// writer enters; waiting readers enter only when no writer is waiting.</para>
    /// <para>
    /// Readers can be kept waiting forever: they do not enter while writers keep arriving before the last one has left.</para>
    /// <para>
    /// A caller that holds the lock in read mode and enters it in read mode again while a writer is waiting never gets in: the writer is waiting for
    /// the first hold.</para>
    /// </remarks>
    Writers,
}
