using System.Collections.Concurrent;
using System.Text.Json;

using GCLatencyMode = System.Runtime.GCLatencyMode;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace NResilience.Stress;

/// <summary>How the driver offered work to the arm. The two modes answer different questions.</summary>
internal enum LoadMode
{
    /// <summary>
    ///     Fixed concurrency: DOP workers each loop on the arm, awaiting one op before starting
    ///     the next. Measures what the process sustains, so it is the right tool for throughput
    ///     and for per-op allocation - and the wrong tool for latency, because a closed loop's
    ///     latency is <c>DOP / throughput</c> by Little's Law and therefore carries no
    ///     information the throughput column does not already have.
    /// </summary>
    ClosedLoop,

    /// <summary>
    ///     Fixed arrival rate: ops are launched on a schedule and never wait for their
    ///     predecessors. Measures latency at a stated offered load - including the queueing a
    ///     real service sees - and it is the only mode in which a client that slows itself down
    ///     does not thereby reduce the load it offers, which is what makes it the only mode that
    ///     can measure how much work a guard actually keeps off a dependency.
    /// </summary>
    OpenLoop,
}

/// <summary>One measured cell: the shape of the work, the load it ran under, and every number the window produced.</summary>
internal sealed record CellResult
{
    public required string Arm { get; init; }

    public required string Kind { get; init; }

    public required LoadMode Mode { get; init; }

    /// <summary>Worker count in closed loop; the pacer count in open loop.</summary>
    public required int Dop { get; init; }

    /// <summary>Open loop only: the arrival rate the pacers were asked to hold. Zero in closed loop.</summary>
    public required double OfferedRate { get; init; }

    public required bool ServerGc { get; init; }

    public required bool ConcurrentGc { get; init; }

    /// <summary>Which repetition of this spec this result is, from 1.</summary>
    public required int Repetition { get; init; }

    public required long Ops { get; init; }

    public required double OpsPerSecond { get; init; }

    public required TimeSpan P50 { get; init; }

    public required TimeSpan P90 { get; init; }

    public required TimeSpan P99 { get; init; }

    public required TimeSpan Max { get; init; }

    public required double BytesPerOp { get; init; }

    public required long AllocatedBytes { get; init; }

    /// <summary>Bytes allocated per second: the quantity GC frequency actually tracks.</summary>
    public required double AllocatedBytesPerSecond { get; init; }

    public required double Gen0PerSecond { get; init; }

    public required double Gen1PerSecond { get; init; }

    public required double Gen2PerSecond { get; init; }

    public required int Collections { get; init; }

    public required TimeSpan GcPauseTotal { get; init; }

    public required double GcPausePercent { get; init; }

    /// <summary>Wall-clock cost of one collection. See <see cref="GcDelta.MeanPause" />.</summary>
    public required TimeSpan GcMeanPause { get; init; }

    /// <summary>Peak thread count observed across the window, not a single end-of-window reading.</summary>
    public required int PoolThreadPeak { get; init; }

    /// <summary>Peak queued work items observed across the window. The only honest evidence of pool saturation.</summary>
    public required long PoolQueuePeak { get; init; }

    public required double PoolQueueMean { get; init; }

    public required long Successes { get; init; }

    public required long Failures { get; init; }

    /// <summary>Callback invocations over the window: the attempts actually made, not the failures.</summary>
    public required long Attempts { get; init; }

    /// <summary>
    ///     Attempts per operation - the amplification a dependency feels, and the figure a guard
    ///     has to move to be doing anything. Independent of how fast the client happens to run,
    ///     which is what makes it comparable between arms that do not share a throughput.
    /// </summary>
    public required double AttemptsPerOp { get; init; }

    public required long Retries { get; init; }

    /// <summary>Retries per operation: <see cref="AttemptsPerOp" /> less the one attempt every op makes.</summary>
    public required double RetriesPerOp { get; init; }

    public required long BudgetRejections { get; init; }

    /// <summary>Requests the server observed, read once at window end. HTTP cells only; zero elsewhere.</summary>
    public required long ServerRequests { get; init; }

    /// <summary>Attempts per op the server saw: ServerRequests / Ops. The honest amplification number.</summary>
    public required double ServerAttemptsPerOp { get; init; }

    /// <summary>Open loop: arrivals the pacers launched. Zero in closed loop.</summary>
    public required long Arrivals { get; init; }

    /// <summary>
    ///     Open loop: arrivals the pacers could not launch on schedule. Non-zero means the row
    ///     measures the pacer, not the arm, and the report says so rather than quoting it.
    /// </summary>
    public required long MissedArrivals { get; init; }

    /// <summary>Open loop: arrivals dropped because the outstanding cap was reached. Non-zero invalidates the row.</summary>
    public required long ShedArrivals { get; init; }

    /// <summary>Open loop: 99th percentile of the gap between an arrival's due time and its launch.</summary>
    public required TimeSpan ArrivalLagP99 { get; init; }

    /// <summary>Open loop: the greatest number of ops in flight at once.</summary>
    public required long OutstandingPeak { get; init; }

    public required bool GcModeVerified { get; init; }

    public required string? GcModeError { get; init; }

    public required double WarmupSeconds { get; init; }

    public required double WindowSeconds { get; init; }

/// <summary>
    ///     Arrivals the pacer failed to deliver, as a fraction of the arrivals the schedule
    ///     called for. The proportion rather than the count is what matters: a single OS stall
    ///     costs one burst of missed arrivals whatever the rate, so a fixed count would condemn
    ///     a high-rate row for a shortfall a low-rate row would not even notice.
    /// </summary>
    public double ArrivalShortfall
    {
        get
        {
            var due = Arrivals + MissedArrivals + ShedArrivals;

            return due == 0 ? 0 : (MissedArrivals + ShedArrivals) / (double)due;
        }
    }

    /// <summary>
    ///     Whether the row measured what it set out to measure.
    ///     <para>
    ///         Shedding is fatal on its own: it means the outstanding cap was reached, so the arm
    ///         was not offered the load the row claims. A missed arrival is a schedule the pacer
    ///         could not hold, which is only fatal in quantity - the threshold is a tenth of a
    ///         percent, tight enough that the offered rate is still the stated one and loose
    ///         enough that one scheduling stall on a shared machine does not void the cell.
    ///     </para>
    /// </summary>
    public bool Trustworthy => GcModeVerified && ShedArrivals == 0 && ArrivalShortfall <= 0.001;

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("arm"u8, Arm);
        writer.WriteString("kind"u8, Kind);
        writer.WriteString("mode"u8, Mode.ToString());
        writer.WriteNumber("dop"u8, Dop);
        writer.WriteNumber("offeredRate"u8, OfferedRate);
        writer.WriteBoolean("serverGc"u8, ServerGc);
        writer.WriteBoolean("concurrentGc"u8, ConcurrentGc);
        writer.WriteNumber("repetition"u8, Repetition);
        writer.WriteNumber("ops"u8, Ops);
        writer.WriteNumber("opsPerSecond"u8, OpsPerSecond);
        writer.WriteString("p50"u8, P50.ToString());
        writer.WriteString("p90"u8, P90.ToString());
        writer.WriteString("p99"u8, P99.ToString());
        writer.WriteString("max"u8, Max.ToString());
        writer.WriteNumber("bytesPerOp"u8, BytesPerOp);
        writer.WriteNumber("allocatedBytes"u8, AllocatedBytes);
        writer.WriteNumber("allocatedBytesPerSecond"u8, AllocatedBytesPerSecond);
        writer.WriteNumber("gen0PerSecond"u8, Gen0PerSecond);
        writer.WriteNumber("gen1PerSecond"u8, Gen1PerSecond);
        writer.WriteNumber("gen2PerSecond"u8, Gen2PerSecond);
        writer.WriteNumber("collections"u8, Collections);
        writer.WriteString("gcPauseTotal"u8, GcPauseTotal.ToString());
        writer.WriteNumber("gcPausePercent"u8, GcPausePercent);
        writer.WriteString("gcMeanPause"u8, GcMeanPause.ToString());
        writer.WriteNumber("poolThreadPeak"u8, PoolThreadPeak);
        writer.WriteNumber("poolQueuePeak"u8, PoolQueuePeak);
        writer.WriteNumber("poolQueueMean"u8, PoolQueueMean);
        writer.WriteNumber("successes"u8, Successes);
        writer.WriteNumber("failures"u8, Failures);
        writer.WriteNumber("attempts"u8, Attempts);
        writer.WriteNumber("attemptsPerOp"u8, AttemptsPerOp);
        writer.WriteNumber("retries"u8, Retries);
        writer.WriteNumber("retriesPerOp"u8, RetriesPerOp);
        writer.WriteNumber("budgetRejections"u8, BudgetRejections);
        writer.WriteNumber("serverRequests"u8, ServerRequests);
        writer.WriteNumber("serverAttemptsPerOp"u8, ServerAttemptsPerOp);
        writer.WriteNumber("arrivals"u8, Arrivals);
        writer.WriteNumber("missedArrivals"u8, MissedArrivals);
        writer.WriteNumber("shedArrivals"u8, ShedArrivals);
        writer.WriteString("arrivalLagP99"u8, ArrivalLagP99.ToString());
        writer.WriteNumber("outstandingPeak"u8, OutstandingPeak);
        writer.WriteNumber("arrivalShortfall"u8, ArrivalShortfall);
        writer.WriteBoolean("gcModeVerified"u8, GcModeVerified);
        writer.WriteBoolean("trustworthy"u8, Trustworthy);
        writer.WriteString("gcModeError"u8, GcModeError);
        writer.WriteNumber("warmupSeconds"u8, WarmupSeconds);
        writer.WriteNumber("windowSeconds"u8, WindowSeconds);
        writer.WriteEndObject();
    }
}

/// <summary>The counters a worker loop updates without allocating.</summary>
internal sealed class OpCounters
{
    private long _ops;
    private long _successes;
    private long _failures;
    private long _budgetRejections;
    private long _attempts;

    public long Ops => Interlocked.Read(ref _ops);
    public long Successes => Interlocked.Read(ref _successes);
    public long Failures => Interlocked.Read(ref _failures);
    public long BudgetRejections => Interlocked.Read(ref _budgetRejections);
    public long Attempts => Interlocked.Read(ref _attempts);

    public void Op() => Interlocked.Increment(ref _ops);
    public void Succeeded() => Interlocked.Increment(ref _successes);
    public void Failed() => Interlocked.Increment(ref _failures);
    public void BudgetRejected() => Interlocked.Increment(ref _budgetRejections);

    /// <summary>
    ///     One per callback invocation, at the top of every attempt - before the attempt can
    ///     fail and before any guard can refuse it. This is the only honest place to count
    ///     amplification from inside the client: a counter incremented where an attempt throws
    ///     counts attempts the budget then refused, which never reached the dependency.
    /// </summary>
    public void Attempted() => Interlocked.Increment(ref _attempts);

    /// <summary>
    ///     Zeroes every counter. The driver calls this after warmup, so the window's numbers
    ///     describe the window alone - warmup ops would otherwise inflate ops/s, outcomes and
    ///     the Att/op the observatory computes, because the server counter resets on the same
    ///     boundary but the op delegates keep counting through both phases.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _ops, 0);
        Interlocked.Exchange(ref _successes, 0);
        Interlocked.Exchange(ref _failures, 0);
        Interlocked.Exchange(ref _budgetRejections, 0);
        Interlocked.Exchange(ref _attempts, 0);
    }
}

/// <summary>
///     Where the HTTP cells read how many requests actually reached the server: the driver
///     resets it before the window and reads it once at window end, so the amplification a
///     report shows is observed at the dependency rather than inferred from retry counters.
/// </summary>
internal interface IServerObservatory
{
    Task ResetCountersAsync();

    Task<long> ReadRequestsAsync();
}

/// <summary>An arm's operation: runs one op, recording its own latency against the timestamp it was due to start.</summary>
internal delegate ValueTask OpDelegate(long scheduledTicks);

/// <summary>The load driver. See <see cref="LoadMode" /> for why there are two modes and what each is for.</summary>
internal static class LoadDriver
{
    /// <summary>Pacer threads the open-loop mode uses. Mirrored from the matrix so the pool floor can account for them.</summary>
    private const int Pacers = 2;

    /// <summary>
    ///     Fixed concurrency: <paramref name="dop" /> workers, each looping on the arm's
    ///     operation until the window expires. Warmup runs the identical loop without measuring,
    ///     so tiering and the thread pool reach steady state before the clock starts.
    /// </summary>
    public static async Task<CellResult> RunClosedLoopAsync(
        CellIdentity identity,
        int dop,
        TimeSpan warmup,
        TimeSpan window,
        Func<OpCounters, LatencyHistogram, OpDelegate> opFactory,
        IServerObservatory? observatory = null)
    {
        var histogram = new LatencyHistogram();
        var counters = new OpCounters();

        // Build the per-worker operations once, before any measurement. Per worker rather than
        // shared: an arm whose failure pattern lives in a field would otherwise race it across
        // every worker, and the pattern each op sees is part of the experiment.
        var ops = new OpDelegate[dop];

        for (var i = 0; i < ops.Length; i++)
            ops[i] = opFactory(counters, histogram);

        await DriveAsync(ops, warmup).ConfigureAwait(false);

        var measurement = await MeasureAsync(
            window,
            observatory,
            counters,
            histogram,
            () => DriveAsync(ops, window)).ConfigureAwait(false);

        return Build(identity, LoadMode.ClosedLoop, dop, offeredRate: 0, warmup, window, measurement, default);
    }

    /// <summary>
    ///     Fixed arrival rate: <paramref name="pacers" /> dedicated threads launch ops on a
    ///     schedule and never wait for one to finish. Latency is measured from each arrival's
    ///     <i>due</i> time, so queueing counts against the arm exactly as it would against a
    ///     service whose callers do not slow down when it does.
    /// </summary>
    /// <remarks>
    ///     The pacers are dedicated threads rather than pool work items on purpose: a pacer that
    ///     spins waiting for its next due time would, on a pool thread, be spinning in the same
    ///     pool the work needs to run in. <see cref="CellResult.MissedArrivals" /> and
    ///     <see cref="CellResult.ArrivalLagP99" /> are the pacer's own self-report - a row with
    ///     either one non-trivial is a row about the driver, and the report drops it rather than
    ///     quoting it.
    /// </remarks>
    public static async Task<CellResult> RunOpenLoopAsync(
        CellIdentity identity,
        int pacers,
        double ratePerSecond,
        long maxOutstanding,
        TimeSpan warmup,
        TimeSpan window,
        Func<OpCounters, LatencyHistogram, OpDelegate> opFactory,
        IServerObservatory? observatory = null)
    {
        // A modest floor under the pool's thread count, so injection hysteresis is not itself
        // the tail. Modest on purpose: a large floor oversubscribes the cores, and the pacer
        // threads then lose them - measured at eight threads per core, the pacers fell 11 ms
        // behind schedule and the row stopped being about the arm at all.
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorker, Environment.ProcessorCount + Pacers), minIo);

        var histogram = new LatencyHistogram();
        var counters = new OpCounters();
        var op = opFactory(counters, histogram);

        var warmupPacing = new PacingStats();
        await PaceAsync(op, pacers, ratePerSecond, maxOutstanding, warmup, warmupPacing).ConfigureAwait(false);

        var pacing = new PacingStats();

        var measurement = await MeasureAsync(
            window,
            observatory,
            counters,
            histogram,
            () => PaceAsync(op, pacers, ratePerSecond, maxOutstanding, window, pacing)).ConfigureAwait(false);

        return Build(identity, LoadMode.OpenLoop, pacers, ratePerSecond, warmup, window, measurement, pacing);
    }

    /// <summary>
    ///     The measurement window itself, shared by both modes: reset both sides to zero, take
    ///     the snapshots, drive, and read everything back. Only the driving differs between the
    ///     modes, so only that is a parameter.
    /// </summary>
    private static async Task<Measurement> MeasureAsync(
        TimeSpan window,
        IServerObservatory? observatory,
        OpCounters counters,
        LatencyHistogram histogram,
        Func<Task> drive)
    {
        // The window starts from zero on both sides: the op counters and the histogram
        // describe the window alone, and the observatory - when one is attached - resets on
        // the same boundary.
        counters.Reset();
        histogram.Reset();

        if (observatory is not null)
            await observatory.ResetCountersAsync().ConfigureAwait(false);

        // Warmup's garbage is collected on warmup's clock, not the window's. Without this the
        // window is charged for a collection it did not cause, which is most of the run-to-run
        // noise in the collection counts.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        using var pool = new PoolSampler(TimeSpan.FromMilliseconds(2));

        var startSnapshot = GcSnapshot.Take();
        var startTimestamp = Stopwatch.GetTimestamp();

        await drive().ConfigureAwait(false);

        var elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        var endSnapshot = GcSnapshot.Take();

        var serverRequests = observatory is null ? 0 : await observatory.ReadRequestsAsync().ConfigureAwait(false);

        return new Measurement(
            elapsedTicks / (double)Stopwatch.Frequency,
            GcSnapshot.Between(startSnapshot, endSnapshot),
            counters.Ops,
            counters.Successes,
            counters.Failures,
            counters.Attempts,
            counters.BudgetRejections,
            serverRequests,
            histogram.Percentile(0.50),
            histogram.Percentile(0.90),
            histogram.Percentile(0.99),
            histogram.Max(),
            pool.ThreadPeak,
            pool.QueuePeak,
            pool.QueueMean);
    }

    private static CellResult Build(
        CellIdentity identity,
        LoadMode mode,
        int dop,
        double offeredRate,
        TimeSpan warmup,
        TimeSpan window,
        Measurement m,
        PacingStats? pacing)
    {
        var ops = m.Ops;
        var attemptsPerOp = ops == 0 ? 0 : m.Attempts / (double)ops;

        // Retries are attempts beyond the first every op necessarily makes. Derived rather than
        // counted so the two can never disagree, and expressed per op because a retry rate
        // scales with throughput while a retry *fraction* is a property of the policy.
        var retries = Math.Max(0, m.Attempts - ops);

        return new CellResult
        {
            Arm = identity.Arm,
            Kind = identity.Section,
            Mode = mode,
            Dop = dop,
            OfferedRate = offeredRate,
            ServerGc = System.Runtime.GCSettings.IsServerGC,
            ConcurrentGc = System.Runtime.GCSettings.LatencyMode is GCLatencyMode.Interactive or GCLatencyMode.SustainedLowLatency,
            Repetition = identity.Repetition,
            Ops = ops,
            OpsPerSecond = ops / m.Seconds,
            P50 = m.P50,
            P90 = m.P90,
            P99 = m.P99,
            Max = m.Max,
            BytesPerOp = ops == 0 ? 0 : m.Delta.AllocatedBytes / (double)ops,
            AllocatedBytes = m.Delta.AllocatedBytes,
            AllocatedBytesPerSecond = m.Delta.AllocatedBytes / m.Seconds,
            Gen0PerSecond = m.Delta.Gen0 / m.Seconds,
            Gen1PerSecond = m.Delta.Gen1 / m.Seconds,
            Gen2PerSecond = m.Delta.Gen2 / m.Seconds,
            Collections = m.Delta.Gen0,
            GcPauseTotal = m.Delta.PauseDuration,
            GcPausePercent = m.Delta.PauseDuration.TotalSeconds / m.Seconds * 100,
            GcMeanPause = m.Delta.MeanPause,
            PoolThreadPeak = m.PoolThreadPeak,
            PoolQueuePeak = m.PoolQueuePeak,
            PoolQueueMean = m.PoolQueueMean,
            Successes = m.Successes,
            Failures = m.Failures,
            Attempts = m.Attempts,
            AttemptsPerOp = attemptsPerOp,
            Retries = retries,
            RetriesPerOp = ops == 0 ? 0 : retries / (double)ops,
            BudgetRejections = m.BudgetRejections,
            ServerRequests = m.ServerRequests,
            ServerAttemptsPerOp = ops == 0 ? 0 : m.ServerRequests / (double)ops,
            Arrivals = pacing?.Arrivals ?? 0,
            MissedArrivals = pacing?.Missed ?? 0,
            ShedArrivals = pacing?.Shed ?? 0,
            ArrivalLagP99 = pacing?.Lag.Percentile(0.99) ?? TimeSpan.Zero,
            OutstandingPeak = pacing?.OutstandingPeak ?? 0,
            GcModeVerified = true,
            GcModeError = null,
            WarmupSeconds = warmup.TotalSeconds,
            WindowSeconds = window.TotalSeconds,
        };
    }

    private readonly record struct Measurement(
        double Seconds,
        GcDelta Delta,
        long Ops,
        long Successes,
        long Failures,
        long Attempts,
        long BudgetRejections,
        long ServerRequests,
        TimeSpan P50,
        TimeSpan P90,
        TimeSpan P99,
        TimeSpan Max,
        int PoolThreadPeak,
        long PoolQueuePeak,
        double PoolQueueMean);

    /// <summary>
    ///     One worker per delegate: loop until the deadline, awaiting each op. A tight closed
    ///     loop - the arm's own suspension is the pacing.
    /// </summary>
    private static async Task DriveAsync(OpDelegate[] ops, TimeSpan duration)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var workers = new Task[ops.Length];

        for (var i = 0; i < ops.Length; i++)
        {
            // i is the loop variable of a plain for loop: capture a copy per worker, or every
            // worker runs the last op built.
            var op = ops[i];

            workers[i] = Task.Run(async () =>
            {
                while (Stopwatch.GetTimestamp() < deadline)
                    await op(Stopwatch.GetTimestamp()).ConfigureAwait(false);
            });
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    /// <summary>What the pacers did, so a row can be judged on whether the schedule was actually held.</summary>
    private sealed class PacingStats
    {
        public readonly LatencyHistogram Lag = new();
        private long _arrivals;
        private long _missed;
        private long _shed;
        private long _outstanding;
        private long _outstandingPeak;

        public long Arrivals => Interlocked.Read(ref _arrivals);
        public long Missed => Interlocked.Read(ref _missed);
        public long Shed => Interlocked.Read(ref _shed);
        public long OutstandingPeak => Interlocked.Read(ref _outstandingPeak);

        public void Arrived() => Interlocked.Increment(ref _arrivals);
        public void Missed1(long n) => Interlocked.Add(ref _missed, n);
        public void Shed1() => Interlocked.Increment(ref _shed);

        public long Enter()
        {
            var now = Interlocked.Increment(ref _outstanding);

            if (now > Interlocked.Read(ref _outstandingPeak))
                Interlocked.Exchange(ref _outstandingPeak, now);

            return now;
        }

        public void Exit() => Interlocked.Decrement(ref _outstanding);

        public long Outstanding => Interlocked.Read(ref _outstanding);
    }

    /// <summary>
    ///     A reusable arrival. Pooled so the driver's own launch cost does not grow with the
    ///     arrival rate; the async continuation each arrival needs is unavoidable, which is why
    ///     the open-loop sections read their allocation against an open-loop raw arm rather than
    ///     against the closed-loop one.
    /// </summary>
    private sealed class Arrival : IThreadPoolWorkItem
    {
        public OpDelegate Op = null!;
        public long ScheduledTicks;
        public PacingStats Stats = null!;
        public ConcurrentBag<Arrival> Pool = null!;

        public void Execute() => _ = RunAsync();

        private async Task RunAsync()
        {
            try
            {
                await Op(ScheduledTicks).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Arms count their own outcomes; an escaped exception must not tear down the
                // pool thread that happened to be running this arrival.
            }
            finally
            {
                Stats.Exit();
                Pool.Add(this);
            }
        }
    }

    private static Task PaceAsync(
        OpDelegate op,
        int pacers,
        double ratePerSecond,
        long maxOutstanding,
        TimeSpan duration,
        PacingStats stats)
    {
        var pool = new ConcurrentBag<Arrival>();
        var perPacer = ratePerSecond / pacers;
        var interval = (long)(Stopwatch.Frequency / perPacer);

        if (interval < 1)
            interval = 1;

        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);

        // How far behind schedule a pacer may fall before it gives up on the arrivals it
        // missed and re-bases. Without a bound it would try to catch up forever and the
        // "fixed rate" would become a burst.
        var maxLag = Stopwatch.Frequency / 50;
        var threads = new Thread[pacers];
        var done = new CountdownEvent(pacers);

        for (var i = 0; i < pacers; i++)
        {
            // Stagger the pacers across one interval so all of them do not fire together.
            var offset = (long)(interval * (i / (double)pacers));

            threads[i] = new Thread(() =>
            {
                try
                {
                    var next = Stopwatch.GetTimestamp() + offset;

                    while (true)
                    {
                        var now = Stopwatch.GetTimestamp();

                        if (now >= deadline)
                            return;

                        if (now < next)
                        {
                            var gap = next - now;

                            // Give the core back whenever the wait is long enough to be worth
                            // a context switch, and spin only for the last few microseconds.
                            // A pacer that busy-spins the whole inter-arrival gap holds a core
                            // at 100% however low the rate is - which takes that core away from
                            // the work being measured and puts the contention it causes into
                            // the tail it is supposed to be measuring.
                            if (gap > Stopwatch.Frequency / 1_000)
                                Thread.Sleep(1);
                            else if (gap > Stopwatch.Frequency / 20_000)
                                Thread.Yield();
                            else
                                Thread.SpinWait(20);

                            continue;
                        }

                        if (stats.Outstanding >= maxOutstanding)
                        {
                            stats.Shed1();
                            next += interval;
                            continue;
                        }

                        if (!pool.TryTake(out var arrival))
                            arrival = new Arrival();

                        arrival.Op = op;
                        arrival.ScheduledTicks = next;
                        arrival.Stats = stats;
                        arrival.Pool = pool;

                        stats.Lag.Record(next);
                        stats.Arrived();
                        stats.Enter();

                        ThreadPool.UnsafeQueueUserWorkItem(arrival, preferLocal: false);

                        next += interval;

                        // Too far behind to catch up: count the arrivals that will never be
                        // launched and re-base, so the row reports the shortfall instead of
                        // hiding it in a burst.
                        var behind = Stopwatch.GetTimestamp() - maxLag - next;

                        if (behind > 0)
                        {
                            stats.Missed1(behind / interval);
                            next = Stopwatch.GetTimestamp();
                        }
                    }
                }
                finally
                {
                    done.Signal();
                }
            })
            {
                IsBackground = true,
                Name = $"pacer-{i}",
            };
        }

        foreach (var thread in threads)
            thread.Start();

        return Task.Run(() =>
        {
            done.Wait();
            done.Dispose();

            // Let the arrivals already in flight finish, so their latencies land in the window
            // that offered them rather than being lost. Bounded, because an arm that is wedged
            // must not hang the matrix.
            var drainUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;

            while (stats.Outstanding > 0 && Stopwatch.GetTimestamp() < drainUntil)
                Thread.Sleep(1);
        });
    }
}
