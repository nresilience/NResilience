using System.Runtime.CompilerServices;

namespace NResilience.Internal;

/// <summary>
///     Facts about an attempt that are not its verdict, carried in the spare bits of the same byte the
///     verdict kind occupies.
///     <para>
///         Each value is the mask of the bit it occupies in that byte, so packing is an <c>or</c> and
///         needs no shifting: <see cref="VerdictKind" />'s four members occupy bits 0-1 and
///         <see cref="Verdict.SelfImposedFlag" /> takes bit 7, leaving bits 2-6 free. This is what
///         <see cref="Verdict.SelfImposedFlag" />'s own documentation says the spare bits are for, and it
///         is why hedging adds nothing to the state-machine box: an
///         <see cref="AttemptRecord" /> stays 16 bytes and the inline buffer stays
///         <c>Capacity * 16</c>.
///     </para>
/// </summary>
[Flags]
internal enum AttemptFlags : byte
{
    /// <summary>An ordinary attempt: nothing else was in flight when it started.</summary>
    None = 0,

    /// <summary>
    ///     This attempt was started as a hedge of one that had not come back yet. The first attempt of a
    ///     round never carries this; the copies started alongside it do.
    /// </summary>
    Hedged = 0x04,

    /// <summary>
    ///     This attempt was cancelled because a sibling produced the answer first. Nothing classified it,
    ///     it is not evidence about the dependency, and nothing was charged for it beyond the hedge that
    ///     started it.
    /// </summary>
    Discarded = 0x08,
}

/// <summary>
///     One attempt's timings and verdict, packed into 16 bytes.
///     <para>
///         Every byte of this struct is live across the attempt <c>await</c> and is therefore paid for in
///         the state-machine box of every suspending call, whether or not anything ever fails. The
///         24-byte version measured 96 B of box for a capacity of four - 24% of the executor's
///         total overhead - and shrinking it was the single largest lever available.
///     </para>
///     <para>
///         The packing: the attempt's start offset from the beginning of the operation in one 64-bit
///         field, and its duration in the low 56 bits of the other with the verdict kind in the top
///         eight. 56 bits of <see cref="TimeSpan" /> ticks is 228 years, which no attempt will reach.
///         Everything else an <see cref="Attempt" /> exposes is derived: the delay before an attempt is
///         the gap between the previous one ending and this one starting, and the deadline remaining is
///         the deadline minus the start offset.
///     </para>
///     <para>
///         <see cref="Verdict.SelfImposed" /> rides in the top bit of that same verdict byte. Four of its
///         256 values are enum members, so the flag is free: the record stays 16 bytes, the inline buffer
///         stays <c>Capacity * 16</c>, and the suspending-path budget does not move.
///     </para>
/// </summary>
internal struct AttemptRecord
{
    private const int VerdictShift = 56;
    private const long TicksMask = (1L << VerdictShift) - 1;

    /// <summary>
    ///     The bits of the packed byte that hold the <see cref="VerdictKind" />. Two, because the enum has
    ///     four members; everything above them is a flag - <see cref="AttemptFlags" /> and
    ///     <see cref="Verdict.SelfImposedFlag" />.
    /// </summary>
    private const byte KindMask = 0x03;

    private long _elapsedAndVerdict;

    public long StartOffsetTicks { get; private set; }

    public readonly long ElapsedTicks => _elapsedAndVerdict & TicksMask;

    public readonly VerdictKind Kind => (VerdictKind)(byte)(VerdictByte & KindMask);

    public readonly bool SelfImposed => (VerdictByte & Verdict.SelfImposedFlag) != 0;

    public readonly bool Hedged => (VerdictByte & (byte)AttemptFlags.Hedged) != 0;

    public readonly bool Discarded => (VerdictByte & (byte)AttemptFlags.Discarded) != 0;

    private readonly byte VerdictByte => (byte)((ulong)_elapsedAndVerdict >> VerdictShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(long startOffsetTicks, long elapsedTicks, VerdictKind verdict, bool selfImposed, AttemptFlags flags)
    {
        StartOffsetTicks = startOffsetTicks < 0 ? 0 : startOffsetTicks;
        var clamped = elapsedTicks < 0 ? 0 : elapsedTicks > TicksMask ? TicksMask : elapsedTicks;
        var packed = (byte)((byte)verdict | (selfImposed ? Verdict.SelfImposedFlag : 0) | (byte)flags);
        _elapsedAndVerdict = clamped | ((long)packed << VerdictShift);
    }
}

/// <summary>
///     The inline attempt log. Attempts accumulate in a fixed-size buffer held in the executor's own
///     frame, and only a call that needs the history copies them onto the heap.
/// </summary>
/// <remarks>
///     It cannot be a <c>stackalloc</c> span, for two independent reasons that are worth recording
///     because the obvious implementation is wrong twice: an attempt holds an <see cref="Exception" />,
///     making it a managed type <c>stackalloc</c> rejects outright (CS0208); and even with an
///     unmanaged element type a <see cref="Span{T}" /> cannot live across the <c>await</c> at the
///     center of the loop (CS4007).
/// </remarks>
[InlineArray(Capacity)]
internal struct AttemptBuffer
{
    /// <summary>
    ///     Chosen so the shipped default of three attempts, plus one, fits without touching the heap.
    ///     A policy configured beyond it spills to a heap array on the attempt that overflows -
    ///     paying only for the configuration that asked for it.
    /// </summary>
    public const int Capacity = 4;

    private AttemptRecord _element0;
}

/// <summary>
///     Where the executor records attempts.
///     <para>
///         Exceptions are held in a lazily-allocated side array rather than inline: an inline array of
///         four references would cost 32 bytes of box on every suspending call, including the happy path
///         that never throws, to store nulls.
///     </para>
/// </summary>
internal struct AttemptSink
{
    private AttemptBuffer _inline;
    private AttemptRecord[]? _spill;
    private Exception?[]? _exceptions;

    public int Count { get; private set; }

    public void Record(long startOffsetTicks, long elapsedTicks, VerdictKind verdict, bool selfImposed, Exception? error,
        AttemptFlags flags)
    {
        var index = Count++;

        if (index < AttemptBuffer.Capacity)
            _inline[index].Set(startOffsetTicks, elapsedTicks, verdict, selfImposed, flags);
        else
        {
            var spillIndex = index - AttemptBuffer.Capacity;

            if (_spill is null)
                _spill = new AttemptRecord[AttemptBuffer.Capacity];
            else if (spillIndex >= _spill.Length)
                Array.Resize(ref _spill, _spill.Length * 2);

            _spill[spillIndex].Set(startOffsetTicks, elapsedTicks, verdict, selfImposed, flags);
        }

        if (error is not null)
        {
            _exceptions ??= new Exception?[Math.Max(AttemptBuffer.Capacity, index + 1)];

            if (index >= _exceptions.Length)
            {
                // Doubling, the same rule the spill array grows by.
                Array.Resize(ref _exceptions, Math.Max(_exceptions.Length * 2, index + 1));
            }

            _exceptions[index] = error;
        }
    }

    public AttemptLog Materialize(TimeSpan elapsed, TimeSpan deadline)
    {
        if (Count == 0)
            return AttemptLog.Empty;

        var attempts = new Attempt[Count];
        long previousEnd = 0;

        for (var i = 0; i < Count; i++)
        {
            var record = i < AttemptBuffer.Capacity ? _inline[i] : _spill![i - AttemptBuffer.Capacity];

            var start = record.StartOffsetTicks;
            var delay = i == 0 ? 0 : start - previousEnd;

            // The furthest anything has run to, not the previous entry's end. Hedged attempts overlap,
            // and a discarded one is recorded after the sibling that beat it - so without the max, an
            // entry that ended earlier than its predecessor would rewind the clock and report the next
            // attempt's backoff as longer than it was.
            var end = start + record.ElapsedTicks;

            if (end > previousEnd)
                previousEnd = end;

            var error = _exceptions is not null && i < _exceptions.Length ? _exceptions[i] : null;

            var remaining = deadline != Timeout.InfiniteTimeSpan
                ? deadline.Ticks > start ? TimeSpan.FromTicks(deadline.Ticks - start) : TimeSpan.Zero
                : Timeout.InfiniteTimeSpan;

            attempts[i] = new Attempt(
                i + 1,
                TimeSpan.FromTicks(record.ElapsedTicks),
                TimeSpan.FromTicks(delay < 0 ? 0 : delay),
                record.Kind,
                record.SelfImposed,
                error,
                remaining,
                TimeSpan.FromTicks(start),
                record.Hedged,
                record.Discarded);
        }

        return new AttemptLog(attempts, elapsed);
    }

}
