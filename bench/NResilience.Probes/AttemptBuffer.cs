using System.Runtime.CompilerServices;

namespace NResilience.Probes;

/// <summary>
/// Timings and verdict for a single attempt. Because these are small and unmanaged, a fixed 
/// number can reside in the executor's frame and avoid heap allocations on the happy path.
/// </summary>
public struct AttemptRecord
{
    public long StartedTimestamp;
    public long ElapsedTicks;
    public VerdictKind Verdict;
}

/// <summary>
/// The inline attempt log described in plans/nresilience-design-v3.md. Attempts accumulate in 
/// a fixed-size buffer within the executor's frame; only calls that are about to fail copy 
/// these records to the heap.
///
/// This buffer is the largest contributor to the state-machine box because every byte is 
/// live across the attempt <c>await</c>. Its size is a design lever; this project measures it 
/// rather than assuming it. Every suspending call incurs a cost of 
/// <c>Capacity</c> x <c>sizeof(AttemptRecord)</c> bytes, regardless of whether a retry occurs.
/// </summary>
[InlineArray(Capacity)]
public struct AttemptBuffer
{
    public const int Capacity = 4;

    private AttemptRecord _element0;
}

/// <summary>
/// Defines how the executor records attempts. This is a non-<c>async</c> generic struct, 
/// similar to <see cref="IInvoker{TState, T}"/>; this design avoids extra frames and virtual 
/// calls, placing implementation fields directly in the state-machine box of the using loop.
///
/// Two implementations exist so the cost of the inline attempt log can be measured per 
/// suspending call. Everything live across the attempt await is stored in the box, and 
/// the log is the largest such contributor.
/// </summary>
internal interface IAttemptSink
{
    void Record(int index, long startedTimestamp, long elapsedTicks, VerdictKind verdict);

    AttemptRecord[] Materialise(int count);
}

/// <summary>The shipping implementation, which uses a fixed-size buffer in the executor's frame.</summary>
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

    /// <summary>Called only on the failing path to materialize the log in the shipping design.</summary>
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
/// A measurement counterfactual rather than a shipping option: the same loop without an 
/// attempt log. The difference between this and <see cref="InlineAttemptSink"/> reveals 
/// the per-call cost of failure diagnostics, helping determine if an 
/// <see cref="AttemptBuffer.Capacity"/> of 4 is affordable.
/// </summary>
internal struct NoAttemptSink : IAttemptSink
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Record(int index, long startedTimestamp, long elapsedTicks, VerdictKind verdict)
    {
    }

    public readonly AttemptRecord[] Materialise(int count) => [];
}
