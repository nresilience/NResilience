using System.Numerics;

namespace NResilience.Internal;

/// <summary>
///     A decaying estimate of one quantile of recent call latency.
///     <para>
///         This exists because a threshold an operator has to guess is the wrong kind of threshold.
///         Hedging against a constant delay is the failure mode the FAQ describes: when a dependency
///         browns out so that every call exceeds the constant, every call hedges, and the library
///         doubles the load on a service that is already in trouble. A threshold read from the recent
///         distribution does not have that failure mode - a brownout moves the quantile with it, so the
///         fraction of calls above it stays roughly <c>1 - quantile</c> by definition rather than by an
///         operator's guess.
///     </para>
///     <para>
///         The same estimator, read at a low quantile over a long window, is what lets
///         <see cref="SlowCalls" /> replace a slow-call threshold somebody has to pick per dependency
///         before that dependency has ever run in production. That reading is the opposite one, and
///         deliberately so - see the remarks.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         <b>Two consumers, measuring opposite things.</b> <see cref="Hedge" /> reads a high quantile
///         of a short window - it wants the tail, and it wants the tail to move with the dependency so
///         the load multiplier stays bounded. <see cref="SlowCalls" /> reads a low quantile of a long
///         one - it wants the body, and it wants the body to <i>resist</i> moving, because a baseline
///         that follows a brownout is a breaker that never opens. Same primitive, opposite ends, and no
///         reason for one scope to share a window between them.
///     </para>
///     <para>
///         <b>One window answers one quantile.</b> The quantile is fixed at construction so the answer
///         can be memoized per ring slice; a consumer wanting a different quantile of the same
///         distribution needs its own window and pays for it. That trade is worth revisiting only if a
///         scope ever really does want two.
///     </para>
/// </remarks>
internal sealed class LatencyWindow
{
    /// <summary>
    ///     Bits of mantissa kept per octave, so each factor-of-two range is split into
    ///     <see cref="SubBuckets" /> buckets and the estimate is never more than one bucket wide.
    ///     <para>
    ///         Three bits puts the widest bucket at 12.5% of its own lower bound, and
    ///         <see cref="Threshold" /> returns the bucket's <i>upper</i> bound - so the answer is an
    ///         overestimate of at most that, never an underestimate. The direction is the point: a hedge
    ///         threshold that is 12% high hedges slightly less often than asked, and one that is 12% low
    ///         hedges more. Only one of those errs toward the dependency.
    ///     </para>
    /// </summary>
    private const int MantissaBits = 3;

    private const int SubBuckets = 1 << MantissaBits;

    /// <summary>
    ///     The largest octave of microseconds with a bucket of its own: 2^27 µs, or about 134 seconds.
    ///     <para>
    ///         Anything longer is clamped into the top bucket, and clamping is the one case where the
    ///         answer can come out <i>below</i> the true value rather than above it. It does not matter
    ///         in practice: a call that ran for over two minutes is past any sane attempt timeout, and a
    ///         threshold derived from one is bounded by the deadline before it reaches anything.
    ///     </para>
    /// </summary>
    private const int MaxOctave = 27;

    /// <summary>Buckets per ring: the linear region below <see cref="SubBuckets" />, then eight per octave.</summary>
    private const int Buckets = ((MaxOctave - MantissaBits + 1) << MantissaBits) + SubBuckets;

    /// <summary>
    ///     Slices the window is divided into. Four rather than the breaker's ten because each ring here
    ///     is <see cref="Buckets" /> integers rather than one, and because four already keeps between
    ///     three quarters and all of the window covered at any moment - enough that the estimate does
    ///     not visibly step when a slice is evicted.
    /// </summary>
    private const int Rings = 4;

    /// <summary>Held only while a ring is being cleared, which happens once per ring per slice.</summary>
    private readonly object _gate = new();

    private readonly double _quantile;

    /// <summary>
    ///     The epoch each ring currently holds. A ring whose stamp is not within the last
    ///     <see cref="Rings" /> epochs holds counts from a previous revolution and is skipped, which is
    ///     what makes an idle window report nothing rather than something stale.
    /// </summary>
    private readonly long[] _ringEpochs = new long[Rings];

    private readonly int[][] _rings = new int[Rings][];
    private readonly long _startedAt;
    private readonly long _ticksPerRing;
    private readonly TimeProvider _time;

    /// <summary>
    ///     The last computed answer. Recomputed when the slice moves on, so the read path is a volatile
    ///     read and a comparison rather than a scan of four rings.
    /// </summary>
    private Answer? _answer;

    /// <summary>Creates a window.</summary>
    /// <param name="quantile">The quantile to report, strictly between 0 and 1.</param>
    /// <param name="window">How much history the estimate covers.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentOutOfRangeException">The quantile is not between 0 and 1, or the window is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="time" /> is null.</exception>
    internal LatencyWindow(double quantile, TimeSpan window, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        if (double.IsNaN(quantile) || quantile <= 0 || quantile >= 1)
            throw new ArgumentOutOfRangeException(nameof(quantile), quantile, "The quantile must be strictly between 0 and 1.");

        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), window, "The window must be positive.");

        _quantile = quantile;
        _time = time;
        _startedAt = time.GetTimestamp();
        _ticksPerRing = Math.Max(window.Ticks / Rings, 1);

        for (var slot = 0; slot < Rings; slot++)
        {
            _rings[slot] = new int[Buckets];

            // Far enough in the past that no real epoch can be mistaken for it, and near enough that
            // subtracting it from one cannot overflow.
            _ringEpochs[slot] = -1 - Rings;
        }
    }

    /// <summary>
    ///     How many samples the window currently holds. Recomputes rather than reading the memo,
    ///     because this exists for diagnostics and tests rather than for the read path.
    /// </summary>
    internal int Samples => Recompute().Samples;

    /// <summary>Adds one completed call to the window.</summary>
    /// <param name="duration">How long it took. A negative duration is ignored.</param>
    /// <remarks>
    ///     One clock read, one division, one bucket index and one interlocked increment. A sample that
    ///     lands in a ring while another thread is clearing it for a new slice is lost; the window is an
    ///     estimate over thousands of calls, and paying for a lock per attempt to keep one of them would
    ///     be the wrong trade.
    /// </remarks>
    internal void Record(TimeSpan duration) => RecordAt(Epoch(), duration);

    /// <summary>
    ///     Adds one completed call and reports the quantile including it, for a consumer that needs
    ///     both on every call.
    /// </summary>
    /// <param name="duration">How long it took. A negative duration is ignored.</param>
    /// <param name="minimumSamples">How many samples are required before an answer is given at all.</param>
    /// <returns>The quantile, or null.</returns>
    /// <remarks>
    ///     Exists to read the clock once rather than twice. <see cref="Breaker" /> asks both questions
    ///     about every successful attempt it samples, and <see cref="Epoch" /> is a timestamp read and a
    ///     division that neither answer needs its own copy of.
    /// </remarks>
    internal TimeSpan? RecordAndThreshold(TimeSpan duration, int minimumSamples)
    {
        var epoch = Epoch();
        RecordAt(epoch, duration);

        return ThresholdAt(epoch, minimumSamples);
    }

    /// <summary>
    ///     The configured quantile of recent latency, or null when the window has not seen enough calls
    ///     to have an opinion.
    /// </summary>
    /// <param name="minimumSamples">How many samples are required before an answer is given at all.</param>
    /// <returns>The quantile, or null.</returns>
    /// <remarks>
    ///     The answer is recomputed when the window moves onto a new slice, so it is at most one slice -
    ///     a quarter of the window - behind the traffic. That staleness is deliberate: the question is
    ///     about a distribution rather than about the last call, and rescanning four rings per attempt
    ///     to answer it sooner would cost every call for an answer that moves on the scale of seconds.
    /// </remarks>
    internal TimeSpan? Threshold(int minimumSamples) => ThresholdAt(Epoch(), minimumSamples);

    private void RecordAt(long epoch, TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            return;

        var slot = (int)(epoch & (Rings - 1));

        if (Volatile.Read(ref _ringEpochs[slot]) != epoch)
            Claim(slot, epoch);

        Interlocked.Increment(ref _rings[slot][IndexOf(duration)]);
    }

    private TimeSpan? ThresholdAt(long epoch, int minimumSamples)
    {
        var answer = Current(epoch);

        if (answer.Samples >= minimumSamples)
            return answer.Threshold;

        // A memoized "not enough yet" must not outlive the slice it was computed in, or a window that
        // crossed the minimum a thousand calls ago would still report nothing until the slice rolled -
        // and the first read of a cold window is exactly when that happens. Reached only while the
        // window is below the minimum, which is a state that is cheap to be in by definition.
        answer = Recompute();

        return answer.Samples >= minimumSamples ? answer.Threshold : null;
    }

    /// <summary>
    ///     The bucket a duration falls in: a linear region below <see cref="SubBuckets" /> microseconds,
    ///     then <see cref="SubBuckets" /> buckets per octave. The layout HdrHistogram uses, at the
    ///     smallest precision that answers this question.
    /// </summary>
    private static int IndexOf(TimeSpan duration)
    {
        var value = duration.Ticks / TimeSpan.TicksPerMicrosecond;

        if (value < SubBuckets)
            return (int)value;

        var octave = 63 - BitOperations.LeadingZeroCount((ulong)value);

        if (octave > MaxOctave)
            return Buckets - 1;

        var mantissa = (int)((value >> (octave - MantissaBits)) & (SubBuckets - 1));
        return ((octave - MantissaBits + 1) << MantissaBits) + mantissa;
    }

    /// <summary>
    ///     The exclusive upper bound of a bucket - the answer <see cref="Threshold" /> reports, so that
    ///     it is never below the true quantile.
    /// </summary>
    private static TimeSpan UpperBoundOf(int index)
    {
        if (index < SubBuckets)
            return TimeSpan.FromTicks((index + 1) * TimeSpan.TicksPerMicrosecond);

        // index = ((octave - MantissaBits + 1) << MantissaBits) + mantissa, so the group is the top
        // bits and the octave follows from it.
        var group = index >> MantissaBits;
        var mantissa = index & (SubBuckets - 1);
        var micros = (long)(SubBuckets + mantissa + 1) << (group - 1);

        return TimeSpan.FromTicks(micros * TimeSpan.TicksPerMicrosecond);
    }

    private long Epoch() => _time.GetElapsedTime(_startedAt).Ticks / _ticksPerRing;

    /// <summary>
    ///     Takes a ring over for a new slice, clearing whatever revolution's counts it still held.
    ///     Clearing on write rather than on a timer is what makes an idle window free.
    /// </summary>
    private void Claim(int slot, long epoch)
    {
        lock (_gate)
        {
            if (_ringEpochs[slot] == epoch)
                return;

            Array.Clear(_rings[slot]);
            Volatile.Write(ref _ringEpochs[slot], epoch);
        }
    }

    private Answer Current(long epoch)
    {
        var answer = Volatile.Read(ref _answer);

        return answer is not null && answer.Epoch == epoch ? answer : Recompute();
    }

    /// <summary>Computes this slice's answer and publishes it, whatever the memo already held.</summary>
    private Answer Recompute()
    {
        var answer = Compute(Epoch());
        Volatile.Write(ref _answer, answer);

        return answer;
    }

    /// <summary>
    ///     Two passes over the live rings: one for the total, one to walk the buckets until the rank the
    ///     quantile asks for is reached. Once per slice, so a few thousand integer reads is not a cost
    ///     worth avoiding, and it keeps a merged snapshot from having to exist at all.
    /// </summary>
    private Answer Compute(long epoch)
    {
        Span<int> live = stackalloc int[Rings];
        var rings = 0;

        for (var slot = 0; slot < Rings; slot++)
        {
            var stamped = Volatile.Read(ref _ringEpochs[slot]);

            if (stamped <= epoch && epoch - stamped < Rings)
                live[rings++] = slot;
        }

        long total = 0;

        for (var i = 0; i < rings; i++)
        {
            var ring = _rings[live[i]];

            for (var bucket = 0; bucket < Buckets; bucket++)
            {
                total += ring[bucket];
            }
        }

        if (total == 0)
            return new Answer(epoch, TimeSpan.Zero, 0);

        // A 1-based rank, rounded up: at the 95th percentile of 100 samples the answer is the bucket
        // the 95th one falls in, not the 96th.
        var rank = (long)Math.Ceiling(_quantile * total);

        if (rank < 1)
            rank = 1;

        var samples = (int)Math.Min(total, int.MaxValue);
        long cumulative = 0;

        for (var bucket = 0; bucket < Buckets; bucket++)
        {
            for (var i = 0; i < rings; i++)
            {
                cumulative += _rings[live[i]][bucket];
            }

            if (cumulative >= rank)
                return new Answer(epoch, UpperBoundOf(bucket), samples);
        }

        // Unreachable while the counts do not shrink under us, and cheaper to answer than to assert.
        return new Answer(epoch, UpperBoundOf(Buckets - 1), samples);
    }

    /// <summary>
    ///     One slice's answer, published as a whole so a reader cannot see a threshold from one slice
    ///     beside a sample count from another. One small object per slice - not per call, and not on any
    ///     path the allocation gates measure.
    /// </summary>
    private sealed class Answer(long epoch, TimeSpan threshold, int samples)
    {
        public long Epoch { get; } = epoch;

        public TimeSpan Threshold { get; } = threshold;

        public int Samples { get; } = samples;
    }
}
