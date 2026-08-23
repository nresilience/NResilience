namespace NResilience;

/// <summary>What a <see cref="Breaker"/> is currently doing.</summary>
public enum BreakerState
{
    /// <summary>Calls pass through. Outcomes are being sampled.</summary>
    Closed,

    /// <summary>Calls are refused until the break duration expires.</summary>
    Open,

    /// <summary>
    /// The break has expired and a trickle of trial calls is allowed through. Successes close the
    /// breaker; a failure re-opens it with a longer break.
    /// </summary>
    HalfOpen,

    /// <summary>Forced open by <see cref="Breaker.Isolate"/>. Never self-heals.</summary>
    Isolated,
}

/// <summary>
/// A state change a call caused, handed back to the executor so it can raise the matching
/// <see cref="CallEvent"/> after the breaker's lock has been released.
/// <para>
/// Internal, and deliberately not a public "the breaker changed state" callback on
/// <see cref="Breaker"/> itself. A breaker is shared and a listener is per-policy: the transition
/// belongs to the call that caused it, which is the only context in which "which policy saw this?"
/// has an answer. <see cref="Breaker.Isolate"/> and <see cref="Breaker.Reset"/> are administrative
/// and raise nothing, because there is no call to attribute them to.
/// </para>
/// </summary>
internal enum BreakerTransition : byte
{
    /// <summary>Nothing changed.</summary>
    None,

    /// <summary>The breaker tripped.</summary>
    Opened,

    /// <summary>The breaker recovered.</summary>
    Closed,

    /// <summary>The break duration elapsed and this call became the breaker's probe.</summary>
    HalfOpened,
}

/// <summary>
/// How a <see cref="Breaker"/> decides to trip, how long it stays tripped, and what it takes to
/// close it again.
/// </summary>
/// <remarks>
/// Every default here is a departure from Polly v8, and each one is deliberate. Polly removed
/// classic consecutive-failure breaking, leaving only a rate-based trip at <c>FailureRatio</c> 0.1
/// over a minimum throughput of 100 calls per 30 s - which means a service doing fewer than 100
/// calls per 30 s can never open its breaker, and that is the median .NET service. Consecutive
/// failures is therefore the default trip condition here, and the rate-based trip is opt-in
/// alongside it.
/// </remarks>
public sealed record BreakerSettings
{
    /// <summary>Consecutive failures before opening. The reading most people have of "circuit breaker".</summary>
    public int ConsecutiveFailures { get; init; } = 5;

    /// <summary>
    /// Optional rate-based trip, evaluated alongside the consecutive counter. Null disables it, and
    /// nothing rate-based - including <see cref="SlowCallThreshold"/> - is evaluated until
    /// <see cref="MinimumCalls"/> outcomes have landed in <see cref="Window"/>.
    /// </summary>
    public double? FailureRatio { get; init; }

    /// <summary>How many sampled calls a rate-based trip needs before it means anything.</summary>
    public int MinimumCalls { get; init; } = 20;

    /// <summary>The sliding window the rates are measured over.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Trip on brownouts, not just errors. An attempt slower than this counts against
    /// <see cref="SlowCallRatio"/>, even when it succeeded.
    /// <para>
    /// The most common real degradation is not a dependency returning errors, it is a dependency
    /// returning 200s at 30× normal latency while your thread pool and connection pool fill. An
    /// error-rate breaker sits closed through the entire incident.
    /// </para>
    /// </summary>
    public TimeSpan? SlowCallThreshold { get; init; }

    /// <summary>The proportion of slow calls in the window that opens the breaker.</summary>
    public double SlowCallRatio { get; init; } = 0.5;

    /// <summary>How long the first break lasts.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <see cref="BreakDuration"/> doubles on each consecutive open, up to this. Set equal to
    /// <see cref="BreakDuration"/> to disable growth.
    /// <para>
    /// This is exponential backoff applied to the breaker itself, and its absence is why breakers
    /// flap on a fixed cadence forever. The counter resets on a clean close.
    /// </para>
    /// </summary>
    public TimeSpan MaxBreakDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Concurrent trial calls allowed while half-open.</summary>
    public int HalfOpenProbes { get; init; } = 1;

    /// <summary>
    /// Successful probes required to close. More than one on purpose: closing a breaker on a single
    /// lucky probe, in front of a dependency that is still broken and a client fleet whose
    /// accumulated retries are waiting, is how breakers oscillate and how a metastable failure
    /// sustains itself.
    /// </summary>
    public int ProbeSuccesses { get; init; } = 2;

    /// <summary>
    /// The clock. Leave it alone in production.
    /// <para>
    /// A breaker owns its clock rather than borrowing the executing policy's, because
    /// <see cref="Breaker.State"/> and <see cref="Breaker.OpenedAt"/> are read from health
    /// endpoints and admin handlers that have no policy in hand - and because one breaker shared by
    /// two policies with different clocks would otherwise have no single answer to "how long have
    /// you been open?".
    /// </para>
    /// </summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Checks the settings and throws listing every problem at once.</summary>
    /// <exception cref="ResilienceConfigurationException">The settings cannot be used.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (ConsecutiveFailures < 1)
        {
            problems.Add($"{nameof(ConsecutiveFailures)} must be at least 1; it is {ConsecutiveFailures}.");
        }

        if (FailureRatio is { } ratio && (ratio <= 0 || ratio > 1 || double.IsNaN(ratio)))
        {
            problems.Add($"{nameof(FailureRatio)} must be in (0, 1]; it is {ratio}.");
        }

        if (MinimumCalls < 1)
        {
            problems.Add($"{nameof(MinimumCalls)} must be at least 1; it is {MinimumCalls}.");
        }

        if (Window <= TimeSpan.Zero)
        {
            problems.Add($"{nameof(Window)} must be positive; it is {Window}.");
        }

        if (SlowCallThreshold is { } slow && slow <= TimeSpan.Zero)
        {
            problems.Add($"{nameof(SlowCallThreshold)} must be positive, or null for no slow-call trip; it is {slow}.");
        }

        if (SlowCallRatio <= 0 || SlowCallRatio > 1 || double.IsNaN(SlowCallRatio))
        {
            problems.Add($"{nameof(SlowCallRatio)} must be in (0, 1]; it is {SlowCallRatio}.");
        }

        if (BreakDuration <= TimeSpan.Zero)
        {
            problems.Add($"{nameof(BreakDuration)} must be positive; it is {BreakDuration}.");
        }

        if (MaxBreakDuration < BreakDuration)
        {
            problems.Add($"{nameof(MaxBreakDuration)} must be at least {nameof(BreakDuration)}; they are {MaxBreakDuration} and {BreakDuration}.");
        }

        if (HalfOpenProbes < 1)
        {
            problems.Add($"{nameof(HalfOpenProbes)} must be at least 1; it is {HalfOpenProbes}.");
        }

        if (ProbeSuccesses < 1)
        {
            problems.Add($"{nameof(ProbeSuccesses)} must be at least 1; it is {ProbeSuccesses}.");
        }

        if (Time is null)
        {
            problems.Add($"{nameof(Time)} must not be null.");
        }

        if (problems.Count > 0)
        {
            throw new ResilienceConfigurationException(problems);
        }
    }
}

/// <summary>
/// A circuit breaker: an object you construct, hold, and share exactly as widely as you intend.
/// <para>
/// Breaker scope is the single most confusing thing in the .NET resilience ecosystem, because in
/// every existing library it is an emergent property of where a pipeline happened to be registered.
/// Here it is a variable with a name and a lifetime, visible at the point you write
/// <c>new Breaker()</c>. <c>with</c> on a <see cref="Resilience"/> copies the <i>reference</i>,
/// never the state, so two policies derived from a common ancestor share whatever breaker that
/// ancestor held - and that is exactly the intent.
/// </para>
/// <para>
/// It samples individual <b>attempts</b>, always, because that is the only reading that produces a
/// useful failure signal - so "does the breaker see attempts or whole operations?" has one answer
/// rather than depending on composition order. Only <see cref="VerdictKind.Transient"/> counts as
/// evidence: a <see cref="VerdictKind.Throttled"/> response means the dependency is working
/// correctly and defending itself, and a <see cref="VerdictKind.Permanent"/> one is overwhelmingly
/// a client-side fact.
/// </para>
/// </summary>
/// <example>
/// <code>
/// public sealed class Dependencies
/// {
///     public Breaker Payments { get; } = new() { Name = "payments" };
///     public Breaker Search   { get; } = new() { Name = "search" };
/// }
///
/// var payments = Resilience.Http with { Breaker = deps.Payments };
///
/// app.MapGet("/health/payments", () =>
///     deps.Payments.State is BreakerState.Closed ? Results.Ok() : Results.StatusCode(503));
/// </code>
/// </example>
/// <remarks>
/// Guarded by an uncontended <c>lock</c> rather than being lock-free. Sliding-window rotation is a
/// multi-word operation whose failure mode under <c>Interlocked</c> alone is a silently incorrect
/// failure ratio - far worse than being slow. An uncontended lock is roughly 20 ns and the callback
/// it guards dominates by orders of magnitude.
/// </remarks>
public sealed class Breaker
{
    /// <summary>
    /// Buckets in the sliding window. Ten gives a rotation granularity of 3 s on the default 30 s
    /// window, which is finer than any trip decision needs and costs 120 bytes of <c>int</c> per
    /// breaker - and only when a rate-based trip is actually configured.
    /// </summary>
    private const int BucketCount = 10;

    /// <summary>
    /// Cap on the doubling exponent. <c>MaxBreakDuration</c> is the real bound; this only keeps the
    /// shift from overflowing after a very long outage.
    /// </summary>
    private const int MaxGrowthShift = 40;

    private readonly object _gate = new();
    private readonly BreakerSettings _settings;
    private readonly TimeProvider _time;
    private readonly long _startedAt;
    private readonly long _ticksPerBucket;
    private readonly int[]? _calls;
    private readonly int[]? _failures;
    private readonly int[]? _slow;

    private long _epoch = -1;
    private int _consecutiveFailures;
    private BreakerState _state;
    private DateTimeOffset _openedAt;
    private long _breakUntil;
    private int _consecutiveOpens;
    private int _probesInFlight;
    private int _probeSuccesses;

    /// <summary>
    /// Set by <see cref="OpenCore"/> and <see cref="CloseCore"/> under the lock, and drained by
    /// <see cref="Record"/> on the way out. The transitions happen at four separate points inside
    /// the state machine, and threading a return value out of each of them would mean touching
    /// every one of those paths to carry a value only telemetry reads.
    /// </summary>
    private BreakerTransition _transition;

    /// <summary>Creates a breaker.</summary>
    /// <param name="settings">How it trips. Null means <see cref="BreakerSettings"/>'s defaults.</param>
    /// <exception cref="ResilienceConfigurationException">The settings cannot be used.</exception>
    public Breaker(BreakerSettings? settings = null)
    {
        _settings = settings ?? new BreakerSettings();
        _settings.Validate();
        _time = _settings.Time;
        _startedAt = _time.GetTimestamp();
        _ticksPerBucket = Math.Max(_settings.Window.Ticks / BucketCount, 1);

        // The window arrays exist only when something reads them. A consecutive-failures breaker -
        // the default - is three fields and no allocation beyond the object itself.
        if (IsWindowed(_settings))
        {
            _calls = new int[BucketCount];
            _failures = new int[BucketCount];
            _slow = new int[BucketCount];
        }
    }

    /// <summary>A name for this breaker, used in diagnostics and health endpoints.</summary>
    public string? Name { get; init; }

    /// <summary>The settings this breaker was built with.</summary>
    public BreakerSettings Settings => _settings;

    /// <summary>
    /// What the breaker is currently doing.
    /// <para>
    /// An <see cref="BreakerState.Open"/> breaker whose break duration has already elapsed reports
    /// <see cref="BreakerState.HalfOpen"/>, because that is what the next call will find. Reading
    /// this never changes it: the transition happens on admission, so a health endpoint cannot
    /// consume the probe slot a real call needs.
    /// </para>
    /// </summary>
    public BreakerState State
    {
        get
        {
            lock (_gate)
            {
                return _state == BreakerState.Open && Elapsed() >= _breakUntil
                    ? BreakerState.HalfOpen
                    : _state;
            }
        }
    }

    /// <summary>When the breaker last opened, or null while it is closed.</summary>
    public DateTimeOffset? OpenedAt
    {
        get
        {
            lock (_gate)
            {
                return _state == BreakerState.Closed ? null : _openedAt;
            }
        }
    }

    /// <summary>
    /// Forces the breaker open. It never self-heals from this state; only <see cref="Reset"/>
    /// brings it back.
    /// </summary>
    public void Isolate()
    {
        lock (_gate)
        {
            _state = BreakerState.Isolated;
            _openedAt = _time.GetUtcNow();
            _probesInFlight = 0;
            _probeSuccesses = 0;
            _consecutiveFailures = 0;
            ClearWindow();
        }
    }

    /// <summary>
    /// Forces the breaker closed and discards everything it had learned, including the accumulated
    /// break-duration growth.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            CloseCore();
        }
    }

    /// <summary>
    /// Admission. True means the call may proceed, and in the half-open state consumes one of the
    /// probe slots - so every true must be followed by exactly one <see cref="Record"/>.
    /// </summary>
    /// <param name="transition">
    /// The state change this admission caused, for the caller to report. Reported by the caller
    /// rather than raised here because the transition happens under the breaker's lock and a
    /// listener is arbitrary user code: raising inside the lock would let one slow listener
    /// serialize every call through the breaker.
    /// </param>
    internal bool TryEnter(out BreakerTransition transition)
    {
        transition = BreakerTransition.None;

        lock (_gate)
        {
            switch (_state)
            {
                case BreakerState.Closed:
                    return true;

                case BreakerState.Isolated:
                    return false;

                case BreakerState.Open:
                    if (Elapsed() < _breakUntil)
                    {
                        return false;
                    }

                    // Half-open is a trickle, not a surge: this call becomes the first probe and
                    // the remaining slots - if any - are handed out one admission at a time.
                    _state = BreakerState.HalfOpen;
                    _probeSuccesses = 0;
                    _probesInFlight = 1;
                    transition = BreakerTransition.HalfOpened;
                    return true;

                case BreakerState.HalfOpen:
                    if (_probesInFlight >= _settings.HalfOpenProbes)
                    {
                        return false;
                    }

                    _probesInFlight++;
                    return true;

                default:
                    throw new InvalidOperationException($"Unknown breaker state '{_state}'.");
            }
        }
    }

    /// <summary>One attempt's outcome.</summary>
    /// <param name="kind">How the executor classified it.</param>
    /// <param name="duration">How long the attempt took, for the slow-call trip.</param>
    /// <returns>
    /// The state change this outcome caused, for the caller to report. See
    /// <see cref="TryEnter"/> for why the breaker does not raise it itself.
    /// </returns>
    internal BreakerTransition Record(VerdictKind kind, TimeSpan duration)
    {
        lock (_gate)
        {
            _transition = BreakerTransition.None;
            RecordCore(kind, duration);
            return _transition;
        }
    }

    /// <summary>
    /// Returns a probe slot that <see cref="TryEnter"/> consumed but <see cref="Record"/> will not
    /// be called on - because the attempt never ran, or was aborted by caller cancellation or a
    /// deadline before it reached the recording point.
    /// </summary>
    /// <remarks>
    /// Without this, a probe admitted while half-open but never recorded leaves
    /// <see cref="_probesInFlight"/> at its cap and the breaker wedged in <see cref="BreakerState.HalfOpen"/>
    /// forever: every subsequent <see cref="TryEnter"/> sees the slots full and refuses, and the
    /// breaker has no clock-driven path back to <see cref="BreakerState.Open"/> that would reset them.
    /// </remarks>
    internal void ReleaseProbe()
    {
        lock (_gate)
        {
            // RecordCore already released the slot when it ran, and a probe that closed or re-opened
            // the breaker moved the state away from HalfOpen. This guard makes the release a no-op
            // for any path that did record, so the executor can call it unconditionally in its finally.
            if (_state == BreakerState.HalfOpen && _probesInFlight > 0)
            {
                _probesInFlight--;
            }
        }
    }

    /// <summary>The state machine itself, always called with the lock held.</summary>
    private void RecordCore(VerdictKind kind, TimeSpan duration)
    {
        // An isolated breaker is held open by hand. An outcome that lands after the breaker
        // re-opened belongs to a generation that no longer exists: counting it would either
        // double-punish a dependency already broken or credit a probe slot that was reset out
        // from under it.
        if (_state is BreakerState.Isolated or BreakerState.Open)
        {
            return;
        }

        bool probe = _state == BreakerState.HalfOpen;
        if (probe && _probesInFlight > 0)
        {
            _probesInFlight--;
        }

        long now = Elapsed();

        if (kind == VerdictKind.Ok)
        {
            bool slow = _settings.SlowCallThreshold is { } threshold && duration >= threshold;
            _consecutiveFailures = 0;
            Bucket(now, failure: false, slow: slow);

            if (probe)
            {
                // A slow probe is not a recovery. Closing on a 200 that took 30 s hands the
                // waiting client fleet straight back to a dependency that is still in trouble.
                if (slow)
                {
                    OpenCore(now);
                }
                else if (++_probeSuccesses >= _settings.ProbeSuccesses)
                {
                    CloseCore();
                }

                return;
            }

            if (slow)
            {
                Evaluate(now);
            }

            return;
        }

        // Only Transient is evidence about the dependency's health. Throttled means it is
        // working correctly and defending itself; Permanent is overwhelmingly a client-side
        // fact, and five NullReferenceExceptions in your own mapping code must not open a
        // circuit against a dependency that never misbehaved.
        if (kind != VerdictKind.Transient)
        {
            return;
        }

        Bucket(now, failure: true, slow: false);
        _consecutiveFailures++;

        if (probe)
        {
            OpenCore(now);
            return;
        }

        Evaluate(now);
    }

    /// <summary>
    /// How long until admission might succeed, for the <see cref="CallRejectedException"/> a
    /// refusal carries. Null when there is nothing useful to say - an isolated breaker will not
    /// self-heal, and a half-open one is waiting on a probe rather than on a clock.
    /// </summary>
    internal TimeSpan? RetryAfterHint()
    {
        lock (_gate)
        {
            if (_state != BreakerState.Open)
            {
                return null;
            }

            long left = _breakUntil - Elapsed();
            return left > 0 ? TimeSpan.FromTicks(left) : TimeSpan.Zero;
        }
    }

    private static bool IsWindowed(BreakerSettings settings) =>
        settings.FailureRatio is not null || settings.SlowCallThreshold is not null;

    private long Elapsed() => _time.GetElapsedTime(_startedAt).Ticks;

    private void Evaluate(long now)
    {
        if (_consecutiveFailures >= _settings.ConsecutiveFailures)
        {
            OpenCore(now);
            return;
        }

        if (_calls is null)
        {
            return;
        }

        int calls = Sum(_calls);
        if (calls < _settings.MinimumCalls)
        {
            return;
        }

        if (_settings.FailureRatio is { } ratio && Sum(_failures!) >= ratio * calls)
        {
            OpenCore(now);
            return;
        }

        if (_settings.SlowCallThreshold is not null && Sum(_slow!) >= _settings.SlowCallRatio * calls)
        {
            OpenCore(now);
        }
    }

    private void OpenCore(long now)
    {
        _transition = BreakerTransition.Opened;
        _state = BreakerState.Open;
        _openedAt = _time.GetUtcNow();

        // Exponential backoff applied to the breaker itself. The first open serves BreakDuration;
        // each consecutive one doubles, capped by MaxBreakDuration, and a clean close resets it.
        long grown = _settings.BreakDuration.Ticks << Math.Min(_consecutiveOpens, MaxGrowthShift);
        long capped = Math.Min(grown <= 0 ? long.MaxValue : grown, _settings.MaxBreakDuration.Ticks);

        _breakUntil = now + capped;
        _consecutiveOpens = Math.Min(_consecutiveOpens + 1, MaxGrowthShift);
        _probesInFlight = 0;
        _probeSuccesses = 0;
        _consecutiveFailures = 0;
        ClearWindow();
    }

    private void CloseCore()
    {
        _transition = BreakerTransition.Closed;
        _state = BreakerState.Closed;
        _openedAt = default;
        _breakUntil = 0;
        _consecutiveOpens = 0;
        _consecutiveFailures = 0;
        _probesInFlight = 0;
        _probeSuccesses = 0;
        ClearWindow();
    }

    private void Bucket(long now, bool failure, bool slow)
    {
        if (_calls is null)
        {
            return;
        }

        long epoch = now / _ticksPerBucket;
        if (epoch != _epoch)
        {
            // Every bucket the window has moved onto since the last write holds counts from a
            // previous revolution. Clearing them on write rather than on a timer means an idle
            // breaker costs nothing and a resumed one does not trip on stale evidence.
            long stale = _epoch < 0 ? BucketCount : Math.Min(epoch - _epoch, BucketCount);
            for (long i = 0; i < stale; i++)
            {
                int stalled = Index(epoch - i);
                _calls[stalled] = 0;
                _failures![stalled] = 0;
                _slow![stalled] = 0;
            }

            _epoch = epoch;
        }

        int index = Index(epoch);
        _calls[index]++;
        if (failure)
        {
            _failures![index]++;
        }

        if (slow)
        {
            _slow![index]++;
        }
    }

    private void ClearWindow()
    {
        if (_calls is null)
        {
            return;
        }

        Array.Clear(_calls);
        Array.Clear(_failures!);
        Array.Clear(_slow!);
        _epoch = -1;
    }

    private static int Index(long epoch) => (int)(((epoch % BucketCount) + BucketCount) % BucketCount);

    private static int Sum(int[] buckets)
    {
        int total = 0;
        for (int i = 0; i < buckets.Length; i++)
        {
            total += buckets[i];
        }

        return total;
    }
}
