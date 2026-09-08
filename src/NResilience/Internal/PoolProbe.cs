using System.Diagnostics;

namespace NResilience.Internal;

/// <summary>
///     One process-wide measurement of how long a work item waits in the thread pool's global queue
///     before it runs.
///     <para>
///         Every estimator in this library measures wall-clock duration around the callback and
///         attributes all of it to the dependency. When the local pool is the bottleneck that
///         attribution is wrong, and it is wrong in the direction that relaxes every bound the library
///         has: a queue delay of 400 ms reads, from inside the executor, exactly like a dependency that
///         got 400 ms slower. This is the number that tells the two apart.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         <b>Process-wide, not per policy and not per call.</b> The thread pool is one queue, so a
///         second probe would measure the same thing again and pay for it twice. One work item per
///         quarter second per process, and a read is two volatile loads.
///     </para>
///     <para>
///         <b>Paced by the reader, not by a timer.</b> A timer callback runs on the pool, so a starved
///         pool would starve the scheduler that measures the starvation. Queueing the next probe from
///         the read instead has the same effect without that circularity, and it means a process that
///         never configures <see cref="Resilience.Saturation" /> never queues a probe at all: the whole
///         feature costs nothing until something asks.
///     </para>
///     <para>
///         <b>An overdue probe is itself the reading.</b> While a sample is outstanding, its own age is
///         compared against the last delay measured and the larger wins - so a pool deep enough that
///         the probe has not come back for a second reports a second, rather than reporting the
///         microsecond it measured before the incident started. Reporting a stale low number is the one
///         failure this measurement cannot afford, because a low number is what every reader below
///         already assumes.
///     </para>
///     <para>
///         Deliberately not <c>Microsoft.Extensions.Diagnostics.ResourceMonitoring</c>:
///         <c>IResourceMonitor</c> is obsolete, it is a package dependency the core does not have, and
///         CPU percentage is the wrong question anyway. A starved pool on an idle CPU is the exact case
///         this is for, because the threads are blocked rather than busy.
///     </para>
/// </remarks>
internal static class PoolProbe
{
    /// <summary>
    ///     The quantile of queue delay the baseline reports: the median.
    ///     <para>
    ///         The body of the distribution rather than the tail, because the question this baseline
    ///         answers is "what does this process's queue normally look like" and a tail follows an
    ///         incident into it. It is the reading <see cref="SlowCalls" /> takes, and for the same
    ///         reason.
    ///     </para>
    /// </summary>
    internal const double Quantile = 0.5;

    /// <summary>How often a probe is queued, at most: four per second.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     How much history the baseline covers. One minute, which is four times shorter than any
    ///     dependency-facing window in the library: this is a fact about the current shape of the
    ///     process rather than a memory of what healthy looked like, and a pool that has just been
    ///     given more threads should be normal again within the minute.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     One instance, re-queued. Safe because <see cref="_queuedAt" /> admits exactly one outstanding
    ///     probe at a time, and it is what keeps the measurement allocation-free rather than one closure
    ///     per quarter second.
    /// </summary>
    private static readonly Work Item = new();

    private static readonly LatencyWindow Baseline = new(Quantile, Window, TimeProvider.System);

    /// <summary>
    ///     Timestamp the outstanding probe was queued at, or zero when none is. Written non-zero by
    ///     whichever reader queues it and cleared by the probe itself.
    /// </summary>
    private static long _queuedAt;

    /// <summary>Timestamp the last probe was queued at, outstanding or not, which is what paces them.</summary>
    private static long _lastQueuedAt;

    /// <summary>The delay the last completed probe measured, in ticks.</summary>
    private static long _delayTicks;

    /// <summary>
    ///     The baseline as of the last completed probe, in ticks, with the sample count behind it and
    ///     the timestamp it was computed at.
    ///     <para>
    ///         Cached rather than read from <see cref="Baseline" /> per call, and that is not a
    ///         micro-optimization: <see cref="LatencyWindow" /> allocates one small answer object each
    ///         time it recomputes, and it recomputes on <i>every</i> read while it is below the caller's
    ///         sample minimum - which is the state a cold probe is in for its first five seconds, and an
    ///         idle process's probe is in permanently. Computing it in the probe's own callback instead
    ///         moves that from once per attempt to four times a second.
    ///     </para>
    /// </summary>
    private static long _normalTicks;

    private static int _samples;

    private static long _measuredAt;

    /// <summary>
    ///     What the pool currently looks like, and what normal looks like for this process.
    /// </summary>
    /// <param name="minimumSamples">How many samples the baseline needs before it reports one.</param>
    /// <returns>The reading. <see cref="Reading.Normal" /> is null while the baseline is cold.</returns>
    /// <remarks>
    ///     Also what paces the probes: a read that finds none outstanding and the interval elapsed
    ///     queues the next one. Two volatile loads and a comparison on the common path, and the queue
    ///     itself happens on at most one read in every <see cref="Interval" />.
    /// </remarks>
    internal static Reading Read(int minimumSamples)
    {
        var now = Stopwatch.GetTimestamp();
        var delay = TimeSpan.FromTicks(Volatile.Read(ref _delayTicks));
        var pending = Volatile.Read(ref _queuedAt);

        if (pending != 0)
        {
            var age = Stopwatch.GetElapsedTime(pending, now);

            if (age > delay)
                delay = age;
        }
        else if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastQueuedAt), now) >= Interval)
        {
            Queue(now);
        }

        return new Reading(delay, NormalAt(now, minimumSamples));
    }

    /// <summary>
    ///     The cached baseline, or null when there are too few samples behind it or they are all older
    ///     than the window.
    /// </summary>
    /// <param name="now">The timestamp the caller already read.</param>
    /// <param name="minimumSamples">How many samples the baseline needs before it reports one.</param>
    /// <returns>The baseline, or null.</returns>
    /// <remarks>
    ///     The staleness check is what keeps an idle process from reporting a baseline whose samples
    ///     have all aged out of the window. Reads are what pace the probes, so a process that is asking
    ///     this question keeps the answer fresh by asking it; the one stale read is the first one after
    ///     a quiet period, and it is the read that queues the probe that fixes it.
    /// </remarks>
    private static TimeSpan? NormalAt(long now, int minimumSamples)
    {
        if (Volatile.Read(ref _samples) < minimumSamples)
            return null;

        if (Stopwatch.GetElapsedTime(Volatile.Read(ref _measuredAt), now) >= Window)
            return null;

        return TimeSpan.FromTicks(Volatile.Read(ref _normalTicks));
    }

    /// <summary>Queues one probe, unless another reader got there first.</summary>
    /// <param name="now">The timestamp the caller already read.</param>
    private static void Queue(long now)
    {
        // Zero is the "none outstanding" marker, and a timestamp of zero is possible at the very
        // start of a process. One tick earlier than the truth is not a measurement anyone can see.
        var stamp = now == 0 ? 1 : now;

        if (Interlocked.CompareExchange(ref _queuedAt, stamp, 0) != 0)
            return;

        Volatile.Write(ref _lastQueuedAt, stamp);

        // preferLocal: false so the probe joins the global queue the way a continuation resuming from
        // I/O does, rather than a thread-local one it might jump the line of. Measuring the queue this
        // library's own callbacks wait in is the entire point.
        ThreadPool.UnsafeQueueUserWorkItem(Item, preferLocal: false);
    }

    /// <summary>Records the outstanding probe's delay. Runs on a pool thread, once per probe.</summary>
    private static void Complete()
    {
        var queuedAt = Volatile.Read(ref _queuedAt);

        if (queuedAt == 0)
            return;

        var delay = Stopwatch.GetElapsedTime(queuedAt);
        Baseline.Record(delay);

        // Samples first: it recomputes the window and publishes the answer, which the Threshold call
        // beside it then reads rather than recomputing a second time.
        var samples = Baseline.Samples;
        var normal = Baseline.Threshold(minimumSamples: 1);

        Volatile.Write(ref _normalTicks, normal?.Ticks ?? 0);
        Volatile.Write(ref _measuredAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref _samples, samples);

        // The measurement is published before the slot is released, so a reader can never see "no
        // probe outstanding" beside the delay from the probe before this one.
        Volatile.Write(ref _delayTicks, delay.Ticks);
        Volatile.Write(ref _queuedAt, 0);
    }

    /// <summary>
    ///     What the pool looks like now, and what normal looks like for this process. Published as a
    ///     whole so a reader cannot compare a delay from one instant against a baseline from another.
    /// </summary>
    /// <param name="Delay">How long a work item is currently waiting to run.</param>
    /// <param name="Normal">The median delay over the last <see cref="Window" />, or null while cold.</param>
    internal readonly record struct Reading(TimeSpan Delay, TimeSpan? Normal);

    /// <summary>The probe itself, as a work item rather than a closure.</summary>
    private sealed class Work : IThreadPoolWorkItem
    {
        public void Execute() => Complete();
    }
}
