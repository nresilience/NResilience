using System.Runtime.CompilerServices;

namespace NResilience.Internal;

/// <summary>
/// One attempt's timings and verdict, packed into 16 bytes.
/// <para>
/// Every byte of this struct is live across the attempt <c>await</c> and is therefore paid for in
/// the state-machine box of every suspending call, whether or not anything ever fails. Phase 0a
/// measured the 24-byte version at 96 B of box for a capacity of four — 24% of the executor's
/// total overhead — and named shrinking it as the single largest lever available to Phase 1.
/// </para>
/// <para>
/// The packing: the attempt's start offset from the beginning of the operation in one 64-bit
/// field, and its duration in the low 56 bits of the other with the verdict kind in the top
/// eight. 56 bits of <see cref="TimeSpan"/> ticks is 228 years, which no attempt will reach.
/// Everything else an <see cref="Attempt"/> exposes is derived: the delay before an attempt is
/// the gap between the previous one ending and this one starting, and the deadline remaining is
/// the deadline minus the start offset.
/// </para>
/// </summary>
internal struct AttemptRecord
{
    private const int VerdictShift = 56;
    private const long TicksMask = (1L << VerdictShift) - 1;

    private long _startOffsetTicks;
    private long _elapsedAndVerdict;

    public readonly long StartOffsetTicks => _startOffsetTicks;

    public readonly long ElapsedTicks => _elapsedAndVerdict & TicksMask;

    public readonly VerdictKind Verdict => (VerdictKind)(byte)((ulong)_elapsedAndVerdict >> VerdictShift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(long startOffsetTicks, long elapsedTicks, VerdictKind verdict)
    {
        _startOffsetTicks = startOffsetTicks < 0 ? 0 : startOffsetTicks;
        long clamped = elapsedTicks < 0 ? 0 : (elapsedTicks > TicksMask ? TicksMask : elapsedTicks);
        _elapsedAndVerdict = clamped | ((long)(byte)verdict << VerdictShift);
    }
}

/// <summary>
/// The inline attempt log. Attempts accumulate in a fixed-size buffer held in the executor's own
/// frame, and only a call that needs the history copies them onto the heap.
/// </summary>
/// <remarks>
/// It cannot be a <c>stackalloc</c> span, for two independent reasons that are worth recording
/// because the obvious implementation is wrong twice: an attempt holds an <see cref="Exception"/>,
/// making it a managed type <c>stackalloc</c> rejects outright (CS0208); and even with an
/// unmanaged element type a <see cref="Span{T}"/> cannot live across the <c>await</c> at the
/// centre of the loop (CS4007).
/// </remarks>
[InlineArray(Capacity)]
internal struct AttemptBuffer
{
    /// <summary>
    /// Chosen so the shipped default of three attempts, plus one, fits without touching the heap.
    /// A policy configured beyond it spills to a heap array on the attempt that overflows —
    /// paying only for the configuration that asked for it.
    /// </summary>
    public const int Capacity = 4;

    private AttemptRecord _element0;
}

/// <summary>
/// Where the executor records attempts.
/// <para>
/// Exceptions are held in a lazily-allocated side array rather than inline: an inline array of
/// four references would cost 32 bytes of box on every suspending call, including the happy path
/// that never throws, to store nulls.
/// </para>
/// </summary>
internal struct AttemptSink
{
    private AttemptBuffer _inline;
    private AttemptRecord[]? _spill;
    private Exception?[]? _exceptions;
    private int _count;

    public readonly int Count => _count;

    public void Record(long startOffsetTicks, long elapsedTicks, VerdictKind verdict, Exception? error)
    {
        int index = _count++;

        if (index < AttemptBuffer.Capacity)
        {
            _inline[index].Set(startOffsetTicks, elapsedTicks, verdict);
        }
        else
        {
            int spillIndex = index - AttemptBuffer.Capacity;
            if (_spill is null)
            {
                _spill = new AttemptRecord[4];
            }
            else if (spillIndex >= _spill.Length)
            {
                Array.Resize(ref _spill, _spill.Length * 2);
            }

            _spill[spillIndex].Set(startOffsetTicks, elapsedTicks, verdict);
        }

        if (error is not null)
        {
            _exceptions ??= new Exception?[Math.Max(AttemptBuffer.Capacity, index + 1)];
            if (index >= _exceptions.Length)
            {
                Array.Resize(ref _exceptions, index + 1);
            }

            _exceptions[index] = error;
        }
    }

    public AttemptLog Materialise(TimeSpan elapsed, TimeSpan deadline, bool bounded)
    {
        if (_count == 0)
        {
            return AttemptLog.Empty;
        }

        var attempts = new Attempt[_count];
        long previousEnd = 0;

        for (int i = 0; i < _count; i++)
        {
            AttemptRecord record = i < AttemptBuffer.Capacity ? _inline[i] : _spill![i - AttemptBuffer.Capacity];

            long start = record.StartOffsetTicks;
            long delay = i == 0 ? 0 : start - previousEnd;
            previousEnd = start + record.ElapsedTicks;

            Exception? error = _exceptions is not null && i < _exceptions.Length ? _exceptions[i] : null;
            TimeSpan remaining = bounded
                ? (deadline.Ticks > start ? TimeSpan.FromTicks(deadline.Ticks - start) : TimeSpan.Zero)
                : Timeout.InfiniteTimeSpan;

            attempts[i] = new Attempt(
                i + 1,
                TimeSpan.FromTicks(record.ElapsedTicks),
                TimeSpan.FromTicks(delay < 0 ? 0 : delay),
                new VerdictOf(record.Verdict).Value,
                error,
                remaining);
        }

        return new AttemptLog(attempts, elapsed);
    }

    /// <summary>
    /// Rebuilds a <see cref="Verdict"/> from the kind the buffer stored. Pushback is deliberately
    /// not round-tripped; see <see cref="Attempt.Verdict"/>.
    /// </summary>
    private readonly struct VerdictOf(VerdictKind kind)
    {
        public Verdict Value => kind switch
        {
            VerdictKind.Ok => Verdict.Ok,
            VerdictKind.Transient => Verdict.Transient,
            VerdictKind.Throttled => Verdict.Throttled(),
            _ => Verdict.Permanent,
        };
    }
}
