using System.Threading.RateLimiting;
using NResilience.Internal;

namespace NResilience.Extensions;

/// <summary>
///     A concurrency limit the process discovers instead of one an operator configures: the permit
///     count moves with what the dependency's latency says about queueing.
///     <para>
///         Build one with <see cref="Limit.Adaptive" />. It is a
///         <see cref="System.Threading.RateLimiting.RateLimiter" /> like the other three, so it works
///         everywhere they do - <c>AcquireOrThrowAsync</c> inside a callback, or <c>AddRateLimit</c> on an
///         HTTP client - and the extra members here exist so a dashboard can see the number that was
///         discovered.
///     </para>
/// </summary>
/// <example>
///     <code>
/// using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Minimum = 4, Maximum = 200 });
/// 
/// await policy.RunAsync(async ct =>
/// {
///     using RateLimitLease lease = await limiter.AcquireOrThrowAsync(ct);
///     return await client.GetAsync(url, ct);
/// }, cancellationToken);
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>The lease is the measurement.</b> A permit is held for exactly as long as the work runs, so
///         disposing the lease is both what frees the slot and what hands the control loop one round-trip
///         sample. That is why the permit has to be taken inside the callback and released with
///         <c>using</c>: a lease that outlives the attempt reports a latency nobody waited for.
///     </para>
///     <para>
///         <b>Why it cannot wrap <see cref="ConcurrencyLimiter" />.</b> The platform limiter's
///         <c>PermitLimit</c> is fixed at construction, so a limiter whose whole purpose is to change that
///         number has to own the queue, the lease lifetime and the statistics itself. That is the expense
///         of this feature; the control loop is a dozen lines.
///     </para>
///     <para>
///         <b>What it cannot do.</b> The baseline is measured, so it can be measured wrong: a process that
///         starts while the dependency is already queueing learns the queued latency as normal and grows
///         to <see cref="AdaptiveLimitOptions.Maximum" />. The ceiling is what bounds that, which is why
///         it should be a number the dependency can survive rather than a number nobody expects to reach.
///     </para>
/// </remarks>
public sealed class AdaptiveLimiter : RateLimiter
{
    /// <summary>
    ///     The quantile of recent latency that counts as "not queueing". Low, for the reason
    ///     <see cref="SlowCalls.Quantile" /> is low: a baseline read from a high quantile moves with the
    ///     very congestion it is supposed to detect, and the loop then never backs off.
    /// </summary>
    private const double BaselineQuantile = 0.1;

    /// <summary>
    ///     How many samples the baseline needs before the loop moves at all. Twenty, matching
    ///     <see cref="BreakerSettings.MinimumCalls" /> and <see cref="Hedge.MinimumSamples" />: a cold
    ///     process holds at <see cref="AdaptiveLimitOptions.Initial" /> rather than guessing.
    /// </summary>
    private const int MinimumSamples = 20;

    /// <summary>
    ///     How much of the limit has to have been in use during a round before the round is allowed to
    ///     raise it.
    ///     <para>
    ///         Growing while the limit is not the thing constraining you discovers nothing - the load is
    ///         the bound, not the permit count - and it would ratchet an idle limiter to
    ///         <see cref="AdaptiveLimitOptions.Maximum" /> so that the first burst after a quiet period
    ///         met no limit at all.
    ///     </para>
    /// </summary>
    private const double SaturationFraction = 0.75;

    /// <summary>
    ///     How much history the baseline covers. Five minutes, the same span
    ///     <see cref="SlowCalls.Window" /> defaults to and for the same reason: it is the memory of what
    ///     healthy looked like, and it has to outlast the congestion it is measuring.
    /// </summary>
    private static readonly TimeSpan BaselineWindow = TimeSpan.FromMinutes(5);

    private readonly LatencyWindow _baseline;
    private readonly double _decreaseFactor;
    private readonly object _gate = new();
    private readonly double _maximum;
    private readonly double _minimum;
    private readonly string _name;
    private readonly Queue<Waiter> _queue = new();
    private readonly int _queueLimit;
    private readonly double _threshold;
    private readonly TimeProvider _time;
    private bool _disposed;

    /// <summary>Timestamp the limiter last went completely quiet, for <see cref="IdleDuration" />.</summary>
    private long _idleSince;

    private int _inFlight;

    /// <summary>The discovered limit, kept as a double so that repeated multiplicative decreases do not stall on a floor.</summary>
    private double _limit;

    /// <summary>The most permits in flight at once during the current round. See <see cref="SaturationFraction" />.</summary>
    private int _peak;

    private int _queuedPermits;

    /// <summary>The fastest call of the current round - the round's evidence about queueing.</summary>
    private TimeSpan _roundFastest = TimeSpan.MaxValue;

    private int _roundSamples;
    private long _totalFailed;
    private long _totalSuccessful;

    internal AdaptiveLimiter(AdaptiveLimitOptions options, int queueLimit, string? name, TimeProvider time)
    {
        _minimum = options.Minimum;
        _maximum = options.Maximum;
        _limit = options.Initial;
        _threshold = options.Threshold;
        _decreaseFactor = options.DecreaseFactor;
        _queueLimit = queueLimit;
        _name = name ?? "(unnamed)";
        _time = time;
        _baseline = new LatencyWindow(BaselineQuantile, BaselineWindow, time);
        _idleSince = time.GetTimestamp();
    }

    /// <summary>
    ///     The permit count the loop has currently settled on. This is the number the feature exists to
    ///     produce, and the one worth watching on a dashboard.
    /// </summary>
    public int CurrentLimit
    {
        get
        {
            lock (_gate)
            {
                return (int)_limit;
            }
        }
    }

    /// <summary>Permits currently held by callers - the concurrency actually in flight.</summary>
    public int InFlight
    {
        get
        {
            lock (_gate)
            {
                return _inFlight;
            }
        }
    }

    /// <summary>
    ///     What a fast call to this dependency has recently looked like, or null while the estimate is
    ///     still cold. The loop compares each round's fastest call against this.
    /// </summary>
    public TimeSpan? Baseline => _baseline.Threshold(MinimumSamples);

    /// <inheritdoc />
    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_gate)
            {
                return _inFlight > 0 || _queuedPermits > 0 ? null : _time.GetElapsedTime(_idleSince);
            }
        }
    }

    /// <inheritdoc />
    public override RateLimiterStatistics GetStatistics()
    {
        lock (_gate)
        {
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = Math.Max(0, (int)_limit - _inFlight),
                CurrentQueuedCount = _queuedPermits,
                TotalSuccessfulLeases = _totalSuccessful,
                TotalFailedLeases = _totalFailed,
            };
        }
    }

    /// <inheritdoc />
    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        Check(permitCount);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Head-of-line order: a caller that walks past a queue is a caller that starved it.
            return _queuedPermits == 0 && TryTake(permitCount) ? Grant(permitCount) : Refuse();
        }
    }

    /// <inheritdoc />
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        Check(permitCount);

        if (cancellationToken.IsCancellationRequested)
            return new ValueTask<RateLimitLease>(Task.FromCanceled<RateLimitLease>(cancellationToken));

        Waiter waiter;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_queuedPermits == 0 && TryTake(permitCount))
                return new ValueTask<RateLimitLease>(Grant(permitCount));

            if (_queuedPermits + permitCount > _queueLimit)
                return new ValueTask<RateLimitLease>(Refuse());

            waiter = new Waiter(permitCount);
            _queue.Enqueue(waiter);
            _queuedPermits += permitCount;

            // Registered while holding the gate. If the token is already cancelled the callback runs
            // here, on this thread, and the lock is re-entered rather than deadlocked on.
            waiter.Registration = cancellationToken.Register(
                static state =>
                {
                    var (limiter, cancelled) = ((AdaptiveLimiter, Waiter))state!;
                    limiter.Abandon(cancelled);
                },
                (this, waiter));
        }

        return new ValueTask<RateLimitLease>(waiter.Completion.Task);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Shutdown();

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override ValueTask DisposeAsyncCore()
    {
        Shutdown();

        return default;
    }

    /// <summary>Caller holds <see cref="_gate" />.</summary>
    private RateLimitLease Refuse()
    {
        _totalFailed++;

        return DeniedLease.Instance;
    }

    private void Check(int permitCount)
    {
        if (permitCount < 0)
            throw new ArgumentOutOfRangeException(nameof(permitCount), permitCount, "A permit count cannot be negative.");

        if (permitCount > _maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(permitCount), permitCount,
                $"The limiter can never grant {permitCount} permits at once; its Maximum is {(int)_maximum}.");
        }
    }

    /// <summary>Takes permits if the current limit has room. Caller holds <see cref="_gate" />.</summary>
    private bool TryTake(int permitCount)
    {
        if (_inFlight + permitCount > (int)_limit)
            return false;

        _inFlight += permitCount;

        if (_inFlight > _peak)
            _peak = _inFlight;

        return true;
    }

    /// <summary>Caller holds <see cref="_gate" />.</summary>
    private RateLimitLease Grant(int permitCount)
    {
        _totalSuccessful++;

        return new Lease(this, permitCount, _time.GetTimestamp());
    }

    /// <summary>A queued caller gave up. Its permits were never taken, so only the reservation is returned.</summary>
    private void Abandon(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Settled)
                return;

            // Left in the queue rather than removed from it - a Queue<T> cannot remove from the
            // middle, and Drain skips settled entries for a fraction of the cost of a linked list.
            waiter.Settled = true;
            _queuedPermits -= waiter.Permits;
            _totalFailed++;
        }

        waiter.Completion.TrySetCanceled();
    }

    /// <summary>
    ///     A lease ended. Returns its permits, hands the control loop one round-trip sample, and lets
    ///     whoever the new limit admits through.
    /// </summary>
    private void Release(int permitCount, TimeSpan duration)
    {
        List<Waiter>? granted;
        int? moved;

        lock (_gate)
        {
            _inFlight -= permitCount;

            // A zero-permit lease is an availability check rather than work, so it is not evidence
            // about how long the dependency takes.
            moved = permitCount > 0 ? Sample(duration) : null;

            granted = Drain();

            if (_inFlight == 0 && _queuedPermits == 0)
                _idleSince = _time.GetTimestamp();
        }

        Complete(granted);

        if (moved is { } limit)
            ResilienceTelemetry.RecordLimit(_name, limit);
    }

    /// <summary>
    ///     Folds one completed call into the round, and closes the round when it is full. Returns the
    ///     new limit when the round moved it, so the caller can report it outside the lock.
    ///     <para>
    ///         A round is a limit's worth of calls - one pass through the permits currently on offer -
    ///         so the loop reacts at the pace the dependency is actually being driven at rather than on
    ///         a timer, and a limit of 4 is not re-decided five times as often as a limit of 20. The
    ///         round's <i>fastest</i> call is the evidence: one slow call among many is a tail, and even
    ///         the fastest call being slow is a queue.
    ///     </para>
    ///     Caller holds <see cref="_gate" />.
    /// </summary>
    private int? Sample(TimeSpan duration)
    {
        var baseline = _baseline.RecordAndThreshold(duration, MinimumSamples);

        _roundSamples++;

        if (duration < _roundFastest)
            _roundFastest = duration;

        if (_roundSamples < Math.Max(MinimumSamples, (int)_limit))
            return null;

        var before = (int)_limit;

        if (baseline is { Ticks: > 0 } normal)
        {
            if (_roundFastest.Ticks > normal.Ticks * _threshold)
                _limit = Math.Max(_minimum, _limit * _decreaseFactor);
            else if (_peak >= _limit * SaturationFraction)
                _limit = Math.Min(_maximum, _limit + 1);
        }

        _roundSamples = 0;
        _roundFastest = TimeSpan.MaxValue;
        _peak = _inFlight;

        var after = (int)_limit;

        return after == before ? null : after;
    }

    /// <summary>Admits as many queued callers as the current limit has room for. Caller holds <see cref="_gate" />.</summary>
    private List<Waiter>? Drain()
    {
        List<Waiter>? granted = null;

        while (_queue.Count > 0)
        {
            var head = _queue.Peek();

            if (head.Settled)
            {
                _queue.Dequeue();
                continue;
            }

            if (!TryTake(head.Permits))
                break;

            _queue.Dequeue();
            head.Settled = true;
            _queuedPermits -= head.Permits;
            _totalSuccessful++;
            (granted ??= []).Add(head);
        }

        return granted;
    }

    /// <summary>
    ///     Hands the admitted callers their leases, outside the lock - the continuation waiting on one
    ///     may do anything at all, including acquiring again.
    /// </summary>
    private void Complete(List<Waiter>? granted)
    {
        if (granted is null)
            return;

        foreach (var waiter in granted)
        {
            waiter.Registration.Dispose();

            var lease = new Lease(this, waiter.Permits, _time.GetTimestamp());

            // Settled is set under the gate before we get here, so nothing else can have completed
            // this waiter. Returning the permits rather than asserting is the cheaper way to be sure.
            if (!waiter.Completion.TrySetResult(lease))
                lease.Dispose();
        }
    }

    private void Shutdown()
    {
        List<Waiter>? abandoned = null;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;

            while (_queue.Count > 0)
            {
                var waiter = _queue.Dequeue();

                if (waiter.Settled)
                    continue;

                waiter.Settled = true;
                _totalFailed++;
                (abandoned ??= []).Add(waiter);
            }

            _queuedPermits = 0;
        }

        if (abandoned is null)
            return;

        // A refusal rather than a cancellation: nothing was cancelled, the limiter went away, and a
        // refusal is the answer the caller already knows how to turn into a retry.
        foreach (var waiter in abandoned)
        {
            waiter.Registration.Dispose();
            waiter.Completion.TrySetResult(DeniedLease.Instance);
        }
    }

    /// <summary>One queued caller.</summary>
    private sealed class Waiter(int permits)
    {
        internal readonly TaskCompletionSource<RateLimitLease> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly int Permits = permits;

        internal CancellationTokenRegistration Registration;

        /// <summary>Whether the waiter has been given an answer. Written only under the limiter's gate.</summary>
        internal bool Settled;
    }

    /// <summary>
    ///     A granted permit. Disposing it is what returns the slot <i>and</i> what reports the
    ///     round-trip time, which is why the two cannot be separated.
    /// </summary>
    private sealed class Lease(AdaptiveLimiter owner, int permits, long acquiredAt) : RateLimitLease
    {
        private int _released;

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;

            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.Release(permits, owner._time.GetElapsedTime(acquiredAt));
        }
    }

    /// <summary>
    ///     A refusal. It carries no <c>RetryAfter</c>, because a concurrency limit has no scheduled
    ///     moment at which a permit appears - the throttled backoff curve decides the delay instead.
    /// </summary>
    private sealed class DeniedLease : RateLimitLease
    {
        internal static readonly DeniedLease Instance = new();

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;

            return false;
        }
    }
}
