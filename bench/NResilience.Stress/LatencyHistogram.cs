using Stopwatch = System.Diagnostics.Stopwatch;

namespace NResilience.Stress;

/// <summary>
///     A fixed-capacity latency histogram with log-spaced buckets over the range a sustained
///     window can produce. Recording an op is a binary search and an array increment - no
///     allocation on the op path, which is what makes per-op percentiles under load affordable
///     to measure at all.
/// </summary>
/// <remarks>
///     <para>
///         <b>Resolution is a published number, not an implementation detail.</b> Buckets grow by
///         <see cref="Growth" />, so a percentile is known to within that factor before any
///         interpolation - and two arms whose percentiles differ by less than it are
///         indistinguishable, however different the printed figures look. The report states the
///         factor for exactly that reason, and <see cref="Separable" /> is the predicate a
///         comparison has to pass before it means anything.
///     </para>
///     <para>
///         <b>Percentiles interpolate inside the bucket rather than returning its midpoint.</b> A
///         midpoint is wrong by up to half a bucket in a direction that depends on where the
///         distribution actually sits, and it quantizes every reported figure onto the bucket
///         ladder - which makes distinct latencies print as one number and near-identical ones
///         print as a step. Placing the sample proportionally by its rank within the bucket is
///         exact for a uniform distribution and closer than the midpoint for any other.
///     </para>
/// </remarks>
internal sealed class LatencyHistogram
{
    /// <summary>
    ///     Bucket growth factor, and therefore the histogram's relative resolution: 8%. Fine
    ///     enough that the comparisons this suite makes sit well outside it, coarse enough that
    ///     the whole range fits in a few hundred counters that stay in cache under load.
    /// </summary>
    public const double Growth = 1.08;

    /// <summary>
    ///     Bucket edges in stopwatch ticks: one tick at the bottom, then geometric at
    ///     <see cref="Growth" /> once the ladder clears integer granularity, out to 300 s.
    ///     <para>
    ///         The ladder is built in ticks rather than converted from durations so that no edge
    ///         is ever truncated onto its neighbour. A stopwatch tick is 100 ns on Windows and
    ///         about 41.7 ns on Apple Silicon; a duration-based ladder starting below the local
    ///         tick collapses its bottom buckets into duplicates and silently loses resolution
    ///         exactly where the fast arms live.
    ///     </para>
    /// </summary>
    private static readonly long[] Bounds = BuildBounds();

    private readonly long[] _counts = new long[Bounds.Length + 1];
    private long _maxTicks;
    private long _sumTicks;
    private long _count;

    /// <summary>
    ///     Whether two measured durations differ by more than the histogram can resolve. Any
    ///     comparison that fails this is a comparison of bucket edges, not of arms.
    /// </summary>
    public static bool Separable(TimeSpan a, TimeSpan b)
    {
        if (a <= TimeSpan.Zero || b <= TimeSpan.Zero)
            return false;

        var ratio = a > b ? a.Ticks / (double)b.Ticks : b.Ticks / (double)a.Ticks;

        return ratio > Growth;
    }

    public long Count => Interlocked.Read(ref _count);

    /// <summary>Records one op's duration, measured from the timestamp the op was due to start.</summary>
    public void Record(long startTicks)
    {
        var elapsed = Stopwatch.GetTimestamp() - startTicks;

        if (elapsed < 0)
            elapsed = 0;

        // Upper-bound search over the edges; a sample at or above the last edge belongs to
        // the overflow bucket, which is _counts[Bounds.Length].
        var lo = 0;
        var hi = Bounds.Length;

        while (lo < hi)
        {
            var mid = (lo + hi) / 2;

            if (Bounds[mid] <= elapsed)
                lo = mid + 1;
            else
                hi = mid;
        }

        Interlocked.Increment(ref _counts[lo]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sumTicks, elapsed);

        // ACAS rather than Interlocked.Max: long.MaxValue is a plausible sentinel in a
        // saturating loop, and the CAS loop is effectively uncontended.
        long seen, observedMax = Volatile.Read(ref _maxTicks);
        while (elapsed > observedMax)
        {
            seen = observedMax;
            observedMax = Interlocked.CompareExchange(ref _maxTicks, elapsed, seen);
            if (observedMax == seen)
                break;
        }
    }

    /// <summary>
    ///     The value at the given quantile, interpolated within the bucket the quantile lands in.
    ///     Accurate to <see cref="Growth" /> - see the type's remarks.
    /// </summary>
    public TimeSpan Percentile(double q)
    {
        var total = 0L;
        var counts = new long[_counts.Length];

        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] = Volatile.Read(ref _counts[i]);
            total += counts[i];
        }

        if (total == 0)
            return TimeSpan.Zero;

        var target = Math.Max(1, (long)Math.Ceiling(total * q));
        var before = 0L;

        for (var i = 0; i < counts.Length; i++)
        {
            if (before + counts[i] < target)
            {
                before += counts[i];
                continue;
            }

            // The overflow bucket spans the top edge to infinity, so an interpolation would be
            // a made-up number; the largest observed sample is where the quantile sits.
            if (i == Bounds.Length)
                return FromTicks(Volatile.Read(ref _maxTicks));

            var lower = i == 0 ? 0 : Bounds[i - 1];
            var upper = Bounds[i];
            var position = (target - before) / (double)counts[i];

            return FromTicks(lower + (long)((upper - lower) * position));
        }

        return FromTicks(Volatile.Read(ref _maxTicks));
    }

    public TimeSpan Mean()
    {
        var count = Interlocked.Read(ref _count);

        return count == 0 ? TimeSpan.Zero : FromTicks(Interlocked.Read(ref _sumTicks) / count);
    }

    public TimeSpan Max() => FromTicks(Volatile.Read(ref _maxTicks));

    /// <summary>
    ///     Zeroes every bucket. The driver calls this after warmup, so window percentiles
    ///     describe the window alone; the max is zeroed through a CAS so a straggler record
    ///     racing the reset cannot write a stale warmup maximum back.
    /// </summary>
    public void Reset()
    {
        for (var i = 0; i < _counts.Length; i++)
            Interlocked.Exchange(ref _counts[i], 0);

        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _sumTicks, 0);

        long seen, observedMax = Volatile.Read(ref _maxTicks);
        while ((seen = Interlocked.CompareExchange(ref _maxTicks, 0, observedMax)) != observedMax)
            observedMax = seen;
    }

    private static TimeSpan FromTicks(long stopwatchTicks) =>
        TimeSpan.FromTicks((long)(stopwatchTicks * (10_000_000.0 / Stopwatch.Frequency)));

    private static long[] BuildBounds()
    {
        var cap = (long)(300.0 * Stopwatch.Frequency);
        var bounds = new List<long>(512);

        // One tick per bucket until the geometric step clears a whole tick, then geometric.
        // The bottom of the range is therefore exact rather than merely fine, which matters:
        // the fastest arms here complete in a few dozen ticks.
        for (var tick = 1L; tick < cap;)
        {
            bounds.Add(tick);

            var next = (long)(tick * Growth);
            tick = next > tick ? next : tick + 1;
        }

        bounds.Add(cap);

        return [.. bounds];
    }
}
