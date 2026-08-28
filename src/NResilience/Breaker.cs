using NResilience.Internal;

namespace NResilience;

/// <summary>What a <see cref="Breaker" /> is currently doing.</summary>
public enum BreakerState
{
    /// <summary>Calls pass through. Outcomes are being sampled.</summary>
    Closed,

    /// <summary>Calls are refused until the break duration expires.</summary>
    Open,

    /// <summary>
    ///     The break has expired and a trickle of trial calls is allowed through. Successes close the
    ///     breaker; a failure re-opens it with a longer break.
    /// </summary>
    HalfOpen,

    /// <summary>Forced open by <see cref="Breaker.Isolate" />. Never self-heals.</summary>
    Isolated,
}

/// <summary>
///     A state change a call caused, handed back to the executor so it can raise the matching
///     <see cref="CallEvent" /> after the breaker's lock has been released.
///     <para>
///         Internal, and deliberately not a public "the breaker changed state" callback on
///         <see cref="Breaker" /> itself. A breaker is shared and a listener is per-policy: the transition
///         belongs to the call that caused it, which is the only context in which "which policy saw this?"
///         has an answer. <see cref="Breaker.Isolate" /> and <see cref="Breaker.Reset" /> are administrative
///         and raise nothing, because there is no call to attribute them to.
///     </para>
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
///     How a <see cref="Breaker" /> decides to trip, how long it stays tripped, and what it takes to
///     close it again.
/// </summary>
/// <remarks>
///     Every default here is a departure from Polly v8, and each one is deliberate. Polly removed
///     classic consecutive-failure breaking, leaving only a rate-based trip at <c>FailureRatio</c> 0.1
///     over a minimum throughput of 100 calls per 30 s - which means a service doing fewer than 100
///     calls per 30 s can never open its breaker, and that is the median .NET service. Consecutive
///     failures is therefore the default trip condition here, and the rate-based trip is opt-in
///     alongside it.
/// </remarks>
public sealed record BreakerSettings
{
    private readonly TimeProvider? _time;

    /// <summary>Consecutive failures before opening. The reading most people have of "circuit breaker".</summary>
    public int ConsecutiveFailures { get; init; } = 5;

    /// <summary>
    ///     Optional rate-based trip, evaluated alongside the consecutive counter. Null disables it, and
    ///     nothing rate-based - including <see cref="SlowCallThreshold" /> - is evaluated until
    ///     <see cref="MinimumCalls" /> outcomes have landed in <see cref="Window" />.
    /// </summary>
    public double? FailureRatio { get; init; }

    /// <summary>How many sampled calls a rate-based trip needs before it means anything.</summary>
    public int MinimumCalls { get; init; } = 20;

    /// <summary>The sliding window the rates are measured over.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Trip on brownouts, not just errors: a constant above which an attempt counts against
    ///     <see cref="SlowCallRatio" />, even when it succeeded.
    ///     <para>
    ///         The most common real degradation is not a dependency returning errors, it is a dependency
    ///         returning 200s at 30× normal latency while your thread pool and connection pool fill. An
    ///         error-rate breaker sits closed through the entire incident.
    ///     </para>
    ///     <para>
    ///         This is the number an operator has to pick per dependency, in milliseconds, before that
    ///         dependency has ever run in production. <see cref="SlowCalls" /> is the same trip expressed
    ///         as a multiple of measured normal latency, which is the form that ports. Set one or the
    ///         other, not both.
    ///     </para>
    /// </summary>
    public TimeSpan? SlowCallThreshold { get; init; }

    /// <summary>
    ///     The same brownout trip as <see cref="SlowCallThreshold" />, defined relative to how long a
    ///     call to this dependency normally takes: <c>SlowCalls.Above(3)</c> is "three times slower than
    ///     normal".
    ///     <para>
    ///         The breaker measures normal itself, from the successful attempts it already samples, so
    ///         nothing has to be guessed up front and nothing has to be re-tuned when the dependency's
    ///         latency changes. See <see cref="NResilience.SlowCalls" /> for why the baseline is a low
    ///         quantile over a long window, and why both halves of that are required rather than
    ///         cosmetic.
    ///     </para>
    /// </summary>
    public SlowCalls? SlowCalls { get; init; }

    /// <summary>The proportion of slow calls in the window that opens the breaker.</summary>
    public double SlowCallRatio { get; init; } = 0.5;

    /// <summary>How long the first break lasts.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     <see cref="BreakDuration" /> doubles on each consecutive open, up to this. Set equal to
    ///     <see cref="BreakDuration" /> to disable growth.
    ///     <para>
    ///         This is exponential backoff applied to the breaker itself, and its absence is why breakers
    ///         flap on a fixed cadence forever. The counter resets on a clean close.
    ///     </para>
    /// </summary>
    public TimeSpan MaxBreakDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Concurrent trial calls allowed while half-open.</summary>
    public int HalfOpenProbes { get; init; } = 1;

    /// <summary>
    ///     Successful probes required to close. More than one on purpose: closing a breaker on a single
    ///     lucky probe, in front of a dependency that is still broken and a client fleet whose
    ///     accumulated retries are waiting, is how breakers oscillate and how a metastable failure
    ///     sustains itself.
    /// </summary>
    public int ProbeSuccesses { get; init; } = 2;

    /// <summary>
    ///     The clock. Leave it alone in production.
    ///     <para>
    ///         A breaker owns its clock rather than borrowing the executing policy's, because
    ///         <see cref="Breaker.State" /> and <see cref="Breaker.OpenedAt" /> are read from health
    ///         endpoints and admin handlers that have no policy in hand - and because one breaker shared by
    ///         two policies with different clocks would otherwise have no single answer to "how long have
    ///         you been open?".
    ///     </para>
    ///     <para>
    ///         Where the library builds the breaker for you - the per-host breakers, and the ones a
    ///         configuration section describes - it hands over the policy's clock unless this was set, so
    ///         one <see cref="Resilience.Time" /> drives all of them from one place.
    ///     </para>
    /// </summary>
    public TimeProvider Time
    {
        get => _time ?? TimeProvider.System;
        init => _time = value;
    }

    /// <summary>
    ///     Null when the caller never named a clock, so the library may supply the executing policy's.
    /// </summary>
    /// <remarks>
    ///     A nullable backing field rather than a separate "was set" flag: a record's synthesized
    ///     equality compares every field, and a flag would make <c>new BreakerSettings()</c> and
    ///     <c>new BreakerSettings { Time = TimeProvider.System }</c> unequal for no reason a caller
    ///     could see.
    /// </remarks>
    internal TimeProvider? ConfiguredTime => _time;

    /// <summary>Checks the settings and throws listing every problem at once.</summary>
    /// <exception cref="ResilienceConfigurationException">The settings cannot be used.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (ConsecutiveFailures < 1)
            problems.Add($"{nameof(ConsecutiveFailures)} must be at least 1; it is {ConsecutiveFailures}.");

        if (FailureRatio is { } ratio && (ratio <= 0 || ratio > 1 || double.IsNaN(ratio)))
            problems.Add($"{nameof(FailureRatio)} must be in (0, 1]; it is {ratio}.");

        if (MinimumCalls < 1)
            problems.Add($"{nameof(MinimumCalls)} must be at least 1; it is {MinimumCalls}.");

        if (Window <= TimeSpan.Zero)
            problems.Add($"{nameof(Window)} must be positive; it is {Window}.");

        if (SlowCallThreshold is { } slow && slow <= TimeSpan.Zero)
            problems.Add($"{nameof(SlowCallThreshold)} must be positive, or null for no slow-call trip; it is {slow}.");

        if (SlowCalls is { } adaptive)
        {
            if (SlowCallThreshold is not null)
            {
                problems.Add(
                    $"Set {nameof(SlowCallThreshold)} or {nameof(SlowCalls)}, not both; they are the same trip " +
                    "defined two ways. Keep SlowCalls unless the dependency has a real, externally-fixed budget.");
            }

            adaptive.Validate(problems);

            // Only worth asking once the two values it is measured against are themselves sane; each of
            // those has its own message, and a second one derived from a NaN would only be noise.
            if (Window > TimeSpan.Zero && SlowCallRatio > 0 && SlowCallRatio <= 1)
                ValidateRace(adaptive, problems);
        }

        if (SlowCallRatio <= 0 || SlowCallRatio > 1 || double.IsNaN(SlowCallRatio))
            problems.Add($"{nameof(SlowCallRatio)} must be in (0, 1]; it is {SlowCallRatio}.");

        if (BreakDuration <= TimeSpan.Zero)
            problems.Add($"{nameof(BreakDuration)} must be positive; it is {BreakDuration}.");

        if (MaxBreakDuration < BreakDuration)
            problems.Add($"{nameof(MaxBreakDuration)} must be at least {nameof(BreakDuration)}; they are {MaxBreakDuration} and {BreakDuration}.");

        if (HalfOpenProbes < 1)
            problems.Add($"{nameof(HalfOpenProbes)} must be at least 1; it is {HalfOpenProbes}.");

        if (ProbeSuccesses < 1)
            problems.Add($"{nameof(ProbeSuccesses)} must be at least 1; it is {ProbeSuccesses}.");

        if (Time is null)
            problems.Add($"{nameof(Time)} must not be null.");

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }

    /// <summary>
    ///     Checks the one thing that is wrong only in combination: an adaptive threshold whose baseline
    ///     is contaminated by a brownout before the trip window has filled with it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A total brownout starts moving the baseline once it accounts for more than
    ///         <c>1 - SlowCalls.Quantile</c> of <see cref="NResilience.SlowCalls.Window" />, so the baseline
    ///         survives for <c>Quantile × Window</c>. The trip needs <see cref="SlowCallRatio" /> of
    ///         <see cref="Window" /> to fill with slow calls, which takes <c>SlowCallRatio × Window</c>.
    ///         Lose that race and the breaker cannot open on latency at all - it does not open late, it
    ///         never opens, because the baseline catches up and the calls stop being slow.
    ///     </para>
    ///     <para>
    ///         A factor of two is the margin: the estimate lags the traffic by up to a quarter of its
    ///         window, and a real brownout is neither total nor instant. The defaults win by ten.
    ///     </para>
    /// </remarks>
    private void ValidateRace(SlowCalls adaptive, List<string> problems)
    {
        var survives = adaptive.Quantile * adaptive.Window.TotalSeconds;
        var fills = SlowCallRatio * Window.TotalSeconds;

        if (survives >= 2 * fills)
            return;

        problems.Add(
            $"{nameof(SlowCalls)}.Quantile x {nameof(SlowCalls)}.Window ({survives:0.##}s) must be at least twice " +
            $"{nameof(SlowCallRatio)} x {nameof(Window)} ({fills:0.##}s), or a brownout moves the baseline before the " +
            $"window fills with slow calls and the breaker can never open on latency. Lengthen {nameof(SlowCalls)}.Window, " +
            $"lower {nameof(SlowCalls)}.Quantile, or shorten {nameof(Window)}.");
    }
}

/// <summary>
///     A circuit breaker: an object you construct, hold, and share exactly as widely as you intend.
///     <para>
///         Breaker scope is the single most confusing thing in the .NET resilience ecosystem, because in
///         every existing library it is an emergent property of where a pipeline happened to be registered.
///         Here it is a variable with a name and a lifetime, visible at the point you write
///         <c>new Breaker()</c>. <c>with</c> on a <see cref="Resilience" /> copies the <i>reference</i>,
///         never the state, so two policies derived from a common ancestor share whatever breaker that
///         ancestor held - and that is exactly the intent.
///     </para>
///     <para>
///         It samples individual <b>attempts</b>, always, because that is the only reading that produces a
///         useful failure signal - so "does the breaker see attempts or whole operations?" has one answer
///         rather than depending on composition order. Only <see cref="VerdictKind.Transient" /> counts as
///         evidence: a <see cref="VerdictKind.Throttled" /> response means the dependency is working
///         correctly and defending itself, and a <see cref="VerdictKind.Permanent" /> one is overwhelmingly
///         a client-side fact.
///     </para>
/// </summary>
/// <example>
///     <code>
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
///     Guarded by an uncontended <c>lock</c> rather than being lock-free. Sliding-window rotation is a
///     multi-word operation whose failure mode under <c>Interlocked</c> alone is a silently incorrect
///     failure ratio - far worse than being slow. An uncontended lock is roughly 20 ns and the callback
///     it guards dominates by orders of magnitude.
/// </remarks>
public sealed class Breaker
{
    /// <summary>
    ///     Buckets in the sliding window. Ten gives a rotation granularity of 3 s on the default 30 s
    ///     window, which is finer than any trip decision needs and costs 120 bytes of <c>int</c> per
    ///     breaker - and only when a rate-based trip is actually configured.
    /// </summary>
    private const int BucketCount = 10;

    /// <summary>
    ///     Cap on the doubling exponent. <c>MaxBreakDuration</c> is the real bound; this only keeps the
    ///     shift from overflowing after a very long outage.
    /// </summary>
    private const int MaxGrowthShift = 40;

    private readonly int[]? _calls;
    private readonly int[]? _failures;

    private readonly object _gate = new();

    /// <summary>
    ///     The measured baseline an adaptive <see cref="BreakerSettings.SlowCalls" /> is relative to.
    ///     Thread-safe on its own, and deliberately never cleared by <see cref="ClearWindow" />: it is a
    ///     measurement of the dependency, not a decision about it, and the whole design depends on it
    ///     outliving the trip window it is raced against.
    /// </summary>
    private readonly LatencyWindow? _normal;

    private readonly int[]? _slow;
    private readonly long _startedAt;
    private readonly long _ticksPerBucket;
    private readonly TimeProvider _time;
    private long _breakUntil;
    private int _consecutiveFailures;
    private int _consecutiveOpens;

    private long _epoch = -1;
    private DateTimeOffset _openedAt;
    private int _probeSuccesses;
    private int _probesInFlight;
    private BreakerState _state;

    /// <summary>
    ///     Set by <see cref="OpenCore" /> and <see cref="CloseCore" /> under the lock, and drained by
    ///     <see cref="Record" /> on the way out. The transitions happen at four separate points inside
    ///     the state machine, and threading a return value out of each of them would mean touching
    ///     every one of those paths to carry a value only telemetry reads.
    /// </summary>
    private BreakerTransition _transition;

    /// <summary>Creates a breaker.</summary>
    /// <param name="settings">How it trips. Null means <see cref="BreakerSettings" />'s defaults.</param>
    /// <exception cref="ResilienceConfigurationException">The settings cannot be used.</exception>
    public Breaker(BreakerSettings? settings = null)
    {
        Settings = settings ?? new BreakerSettings();
        Settings.Validate();
        _time = Settings.Time;
        _startedAt = _time.GetTimestamp();
        _ticksPerBucket = Math.Max(Settings.Window.Ticks / BucketCount, 1);

        // The window arrays exist only when something reads them. A consecutive-failures breaker -
        // the default - is three fields and no allocation beyond the object itself.
        if (IsWindowed(Settings))
        {
            _calls = new int[BucketCount];
            _failures = new int[BucketCount];
            _slow = new int[BucketCount];
        }

        // Only an adaptive breaker pays for the estimate, and it lives on the breaker rather than on
        // the policy because the breaker is the object whose scope is already explicit. Two policies
        // sharing a breaker are two views of one dependency, and they should share one idea of what
        // that dependency's normal latency is.
        if (Settings.SlowCalls is { } adaptive)
            _normal = new LatencyWindow(adaptive.Quantile, adaptive.Window, _time);
    }

    /// <summary>A name for this breaker, used in diagnostics and health endpoints.</summary>
    public string? Name { get; init; }

    /// <summary>The settings this breaker was built with.</summary>
    public BreakerSettings Settings { get; }

    /// <summary>
    ///     What the breaker is currently doing.
    ///     <para>
    ///         An <see cref="BreakerState.Open" /> breaker whose break duration has already elapsed reports
    ///         <see cref="BreakerState.HalfOpen" />, because that is what the next call will find. Reading
    ///         this never changes it: the transition happens on admission, so a health endpoint cannot
    ///         consume the probe slot a real call needs.
    ///     </para>
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

    /// <summary>
    ///     How long a call to this dependency currently takes when it is healthy, as this breaker
    ///     measures it - the number an adaptive <see cref="BreakerSettings.SlowCalls" /> multiplies. Null
    ///     when the breaker is not configured adaptively, or has not seen
    ///     <see cref="NResilience.SlowCalls.MinimumSamples" /> successful attempts yet.
    ///     <para>
    ///         Worth graphing. It is the library's answer to "what does this dependency normally cost
    ///         me?", and an adaptive breaker's trip point is exactly
    ///         <c>NormalLatency × SlowCalls.Multiple</c>.
    ///     </para>
    /// </summary>
    public TimeSpan? NormalLatency =>
        _normal is null ? null : _normal.Threshold(Settings.SlowCalls!.Value.MinimumSamples);

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
    ///     Forces the breaker open. It never self-heals from this state; only <see cref="Reset" />
    ///     brings it back.
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
    ///     Forces the breaker closed and discards every decision it had reached, including the
    ///     accumulated break-duration growth.
    ///     <para>
    ///         <see cref="NormalLatency" /> survives. It is a measurement of the dependency rather than a
    ///         verdict on it, throwing it away would leave an adaptive breaker unable to see a brownout
    ///         until it had re-learned what normal is, and nothing an operator means by "reset" includes
    ///         forgetting how fast the dependency was.
    ///     </para>
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            CloseCore();
        }
    }

    /// <summary>
    ///     Admission. True means the call may proceed, and in the half-open state consumes one of the
    ///     probe slots - so every true must be followed by exactly one <see cref="Record" />.
    /// </summary>
    /// <param name="transition">
    ///     The state change this admission caused, for the caller to report. Reported by the caller
    ///     rather than raised here because the transition happens under the breaker's lock and a
    ///     listener is arbitrary user code: raising inside the lock would let one slow listener
    ///     serialize every call through the breaker.
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
                        return false;

                    // Half-open is a trickle, not a surge: this call becomes the first probe and
                    // the remaining slots - if any - are handed out one admission at a time.
                    _state = BreakerState.HalfOpen;
                    _probeSuccesses = 0;
                    _probesInFlight = 1;
                    transition = BreakerTransition.HalfOpened;
                    return true;

                case BreakerState.HalfOpen:
                    if (_probesInFlight >= Settings.HalfOpenProbes)
                        return false;

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
    ///     The state change this outcome caused, for the caller to report. See
    ///     <see cref="TryEnter" /> for why the breaker does not raise it itself.
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
    ///     Returns a probe slot that <see cref="TryEnter" /> consumed but <see cref="Record" /> will not
    ///     be called on - because the attempt never ran, or was aborted by caller cancellation or a
    ///     deadline before it reached the recording point.
    /// </summary>
    /// <remarks>
    ///     Without this, a probe admitted while half-open but never recorded leaves
    ///     <see cref="_probesInFlight" /> at its cap and the breaker wedged in <see cref="BreakerState.HalfOpen" />
    ///     forever: every subsequent <see cref="TryEnter" /> sees the slots full and refuses, and the
    ///     breaker has no clock-driven path back to <see cref="BreakerState.Open" /> that would reset them.
    /// </remarks>
    internal void ReleaseProbe()
    {
        lock (_gate)
        {
            // RecordCore already released the slot when it ran, and a probe that closed or re-opened
            // the breaker moved the state away from HalfOpen. This guard makes the release a no-op
            // for any path that did record, so the executor can call it unconditionally in its finally.
            if (_state == BreakerState.HalfOpen && _probesInFlight > 0)
                _probesInFlight--;
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
            return;

        var probe = _state == BreakerState.HalfOpen;

        if (probe && _probesInFlight > 0)
            _probesInFlight--;

        var now = Elapsed();

        if (kind == VerdictKind.Ok)
        {
            var slow = IsSlow(duration);
            _consecutiveFailures = 0;
            Bucket(now, false, slow);

            if (probe)
            {
                // A slow probe is not a recovery. Closing on a 200 that took 30 s hands the
                // waiting client fleet straight back to a dependency that is still in trouble.
                if (slow)
                    OpenCore(now);
                else if (++_probeSuccesses >= Settings.ProbeSuccesses)
                    CloseCore();

                return;
            }

            if (slow)
                Evaluate(now);

            return;
        }

        // Only Transient is evidence about the dependency's health. Throttled means it is
        // working correctly and defending itself; Permanent is overwhelmingly a client-side
        // fact, and five NullReferenceExceptions in your own mapping code must not open a
        // circuit against a dependency that never misbehaved.
        if (kind != VerdictKind.Transient)
            return;

        Bucket(now, true, false);
        _consecutiveFailures++;

        if (probe)
        {
            OpenCore(now);
            return;
        }

        Evaluate(now);
    }

    /// <summary>
    ///     How long until admission might succeed, for the <see cref="CallRejectedException" /> a
    ///     refusal carries. Null when there is nothing useful to say - an isolated breaker will not
    ///     self-heal, and a half-open one is waiting on a probe rather than on a clock.
    /// </summary>
    internal TimeSpan? RetryAfterHint()
    {
        lock (_gate)
        {
            if (_state != BreakerState.Open)
                return null;

            var left = _breakUntil - Elapsed();
            return left > 0 ? TimeSpan.FromTicks(left) : TimeSpan.Zero;
        }
    }

    /// <summary>
    ///     Whether a successful attempt counts as slow, against a constant or against the measured
    ///     baseline. Always called with the lock held, on the success path only.
    /// </summary>
    /// <remarks>
    ///     The attempt is added to the baseline before it is judged against it, which is what keeps the
    ///     estimate live without a second pass over the call. It cannot make a call judge itself
    ///     healthy: the answer is memoized per slice of the baseline window, so one sample out of
    ///     thousands moves nothing, and the first samples of a cold window are below
    ///     <see cref="NResilience.SlowCalls.MinimumSamples" /> anyway.
    /// </remarks>
    private bool IsSlow(TimeSpan duration)
    {
        if (Settings.SlowCallThreshold is { } threshold)
            return duration >= threshold;

        if (_normal is null)
            return false;

        var adaptive = Settings.SlowCalls!.Value;
        var normal = _normal.RecordAndThreshold(duration, adaptive.MinimumSamples);

        return normal is { } measured && duration >= adaptive.ThresholdFor(measured);
    }

    private static bool IsWindowed(BreakerSettings settings) =>
        settings.FailureRatio is not null || settings.SlowCallThreshold is not null || settings.SlowCalls is not null;

    private long Elapsed() => _time.GetElapsedTime(_startedAt).Ticks;

    private void Evaluate(long now)
    {
        if (_consecutiveFailures >= Settings.ConsecutiveFailures)
        {
            OpenCore(now);
            return;
        }

        if (_calls is null)
            return;

        var calls = Sum(_calls);

        if (calls < Settings.MinimumCalls)
            return;

        if (Settings.FailureRatio is { } ratio && Sum(_failures!) >= ratio * calls)
        {
            OpenCore(now);
            return;
        }

        if ((Settings.SlowCallThreshold is not null || _normal is not null) && Sum(_slow!) >= Settings.SlowCallRatio * calls)
            OpenCore(now);
    }

    private void OpenCore(long now)
    {
        _transition = BreakerTransition.Opened;
        _state = BreakerState.Open;
        _openedAt = _time.GetUtcNow();

        // Exponential backoff applied to the breaker itself. The first open serves BreakDuration;
        // each consecutive one doubles, capped by MaxBreakDuration, and a clean close resets it.
        var grown = Settings.BreakDuration.Ticks << Math.Min(_consecutiveOpens, MaxGrowthShift);
        var capped = Math.Min(grown <= 0 ? long.MaxValue : grown, Settings.MaxBreakDuration.Ticks);

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
            return;

        var epoch = now / _ticksPerBucket;

        if (epoch != _epoch)
        {
            // Every bucket the window has moved onto since the last write holds counts from a
            // previous revolution. Clearing them on write rather than on a timer means an idle
            // breaker costs nothing and a resumed one does not trip on stale evidence.
            var stale = _epoch < 0 ? BucketCount : Math.Min(epoch - _epoch, BucketCount);

            for (long i = 0; i < stale; i++)
            {
                var stalled = Index(epoch - i);
                _calls[stalled] = 0;
                _failures![stalled] = 0;
                _slow![stalled] = 0;
            }

            _epoch = epoch;
        }

        var index = Index(epoch);
        _calls[index]++;

        if (failure)
            _failures![index]++;

        if (slow)
            _slow![index]++;
    }

    private void ClearWindow()
    {
        if (_calls is null)
            return;

        Array.Clear(_calls);
        Array.Clear(_failures!);
        Array.Clear(_slow!);
        _epoch = -1;
    }

    private static int Index(long epoch) => (int)((epoch % BucketCount + BucketCount) % BucketCount);

    private static int Sum(int[] buckets)
    {
        var total = 0;

        for (var i = 0; i < buckets.Length; i++)
        {
            total += buckets[i];
        }

        return total;
    }
}
