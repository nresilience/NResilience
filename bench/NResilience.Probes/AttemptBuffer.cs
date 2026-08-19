using System.Runtime.CompilerServices;

namespace NResilience.Probes;

/// <summary>
/// One attempt's timings and verdict. Unmanaged and small, so a fixed number of them can live
/// in the executor's own frame and never touch the heap on the happy path.
/// </summary>
public struct AttemptRecord
{
    public long StartedTimestamp;
    public long ElapsedTicks;
    public VerdictKind Verdict;
}

/// <summary>
/// The inline attempt log described in plans/nresilience-design-v3.md: attempts accumulate in a
/// fixed-size buffer held in the executor's frame, and only a call that is about to fail copies
/// them onto the heap.
///
/// This buffer is the single largest contributor to the state-machine box, because every byte of
/// it is live across the attempt <c>await</c>. Its size is therefore a design lever, and Phase 0a
/// measures it rather than assuming it: <c>Capacity</c> x <c>sizeof(AttemptRecord)</c> bytes of
/// box, paid by every suspending call whether or not a retry ever happens.
/// </summary>
[InlineArray(Capacity)]
public struct AttemptBuffer
{
    public const int Capacity = 4;

    private AttemptRecord _element0;
}

/// <summary>
/// How the executor records attempts. A non-<c>async</c> generic struct, exactly like
/// <see cref="IInvoker{TState, T}"/> — so the choice costs no frame and no virtual call, and
/// the implementation's fields land in the state-machine box of whichever loop uses it.
///
/// Two implementations exist so Phase 0a can <i>measure</i> what the inline attempt log costs
/// per suspending call rather than reason about it. Everything live across the attempt await is
/// paid for in the box, and the log is by far the largest such thing.
/// </summary>
internal interface IAttemptSink
{
    void Record(int index, long startedTimestamp, long elapsedTicks, VerdictKind verdict);

    AttemptRecord[] Materialise(int count);
}

/// <summary>The shipping shape: a fixed-size buffer in the executor's own frame.</summary>
internal struct InlineAttemptSink : IAttemptSink
{
    private AttemptBuffer _buffer;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Record(int index, long startedTimestamp, long elapsedTicks, VerdictKind verdict)
    {
        if ((uint)index >= AttemptBuffer.Capacity)
        {
            return;
        }

        ref AttemptRecord slot = ref _buffer[index];
        slot.StartedTimestamp = startedTimestamp;
        slot.ElapsedTicks = elapsedTicks;
        slot.Verdict = verdict;
    }

    /// <summary>Called only on the failing path, which is where the shipping design materialises the log.</summary>
    public AttemptRecord[] Materialise(int count)
    {
        int n = Math.Min(count, AttemptBuffer.Capacity);
        var result = new AttemptRecord[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = _buffer[i];
        }

        return result;
    }
}

/// <summary>
/// Measurement counterfactual, not a shipping option: the same loop with no attempt log at all.
/// The gap between this and <see cref="InlineAttemptSink"/> is the per-call price of failure
/// diagnostics, and it is what tells Phase 1 whether <see cref="AttemptBuffer.Capacity"/> of 4
/// is affordable.
/// </summary>
internal struct NoAttemptSink : IAttemptSink
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Record(int index, long startedTimestamp, long elapsedTicks, VerdictKind verdict)
    {
    }

    public readonly AttemptRecord[] Materialise(int count) => [];
}
