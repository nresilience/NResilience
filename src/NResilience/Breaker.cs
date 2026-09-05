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

    /// <summary>
    ///     The probes succeeded and the breaker is handing the traffic back a growing fraction at a
    ///     time rather than all at once. Calls that fall outside that fraction are refused exactly as
    ///     an open breaker refuses them; a failure re-opens it. Only reachable when
    ///     <see cref="BreakerSettings.Recovery" /> is configured.
    /// </summary>
    Recovering,

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
///     <para>
///         Every default here is a departure from Polly v8, and each one is deliberate. Polly removed
///         classic consecutive-failure breaking, leaving only a rate-based trip at <c>FailureRatio</c>
///         0.1 over a minimum throughput of 100 calls per 30 s - which means a service doing fewer than
///         100 calls per 30 s can never open its breaker, and that is the median .NET service.
///         Consecutive failures is therefore the first trip condition here, and the absolute rate-based
///         trip is opt-in alongside it.
///     </para>
///     <para>
///         The two <i>relative</i> trips are not opt-in. <see cref="SlowCalls" /> and
///         <see cref="Failures" /> are on by default, because the constants their absolute counterparts
///         need are numbers nobody can pick before the dependency has run, and because a default
///         breaker that cannot see a brownout cannot see the most common way a dependency fails. Both
///         are measured against the dependency's own behaviour, both stay invisible until they have a
///         baseline, and both can only trip sooner than the settings around them - so the cold and the
///         healthy cases behave exactly as a consecutive-failures breaker does. Set either to
///         <c>null</c> to turn it off.
///     </para>
///     <para>
///         Each of the two has an absolute counterpart - <see cref="SlowCallThreshold" /> for
///         <see cref="SlowCalls" />, <see cref="FailureRatio" /> for <see cref="Failures" /> - and one
///         rule covers both pairs, and the policy's <c>AttemptTimeout</c> and <c>AttemptCeiling</c> besides:
///         <b>
///             a bound may be stated as a constant, measured from the dependency, or both, and when both
///             the tighter one wins.
///         </b>
///         The measured term never loosens what you wrote.
///     </para>
/// </remarks>
public sealed record BreakerSettings
{
    /// <summary>
    ///     The multiple the default relative failure trip uses: five times the dependency's own
    ///     measured error rate.
    /// </summary>
    private const double DefaultFailureMultiple = 5.0;

    /// <summary>
    ///     The multiple the default brownout trip uses: three times the dependency's own measured
    ///     normal latency.
    /// </summary>
    private const double DefaultSlowCallMultiple = 3.0;

    /// <summary>
    ///     The longest baseline window the library will derive for a trip it defaulted on. Beyond it the
    ///     baseline would no longer describe "normally", so the default steps aside instead - see
    ///     <see cref="DefaultFailures" />.
    /// </summary>
    private static readonly TimeSpan MaxDerivedBaseline = TimeSpan.FromHours(1);

    private readonly Failures? _failures;
    private readonly bool _failuresSet;
    private readonly SlowCalls? _slowCalls;
    private readonly bool _slowCallsSet;

    /// <summary>
    ///     Whether this breaker is allowed to measure the dependency and trip on what it measures. True
    ///     by default.
    ///     <para>
    ///         Setting it to <c>false</c> turns off both relative trips - <see cref="SlowCalls" /> and
    ///         <see cref="Failures" /> - in one word, leaving a breaker that opens only on
    ///         <see cref="ConsecutiveFailures" /> and on whatever absolute rates were written here. It is
    ///         the breaker's half of <see cref="Resilience.Adaptive" />, and it is separate because a
    ///         breaker is a live object two policies may share.
    ///     </para>
    ///     <para>
    ///         It suppresses defaults rather than overriding what you wrote. Settings that say
    ///         <c>false</c> and then configure <see cref="SlowCalls" /> or <see cref="Failures" /> have
    ///         contradicted themselves, and <see cref="Validate" /> says so rather than picking a winner.
    ///     </para>
    ///     <para>
    ///         <see cref="Recovery" /> is unaffected. Its ramp needs no baseline - without one it is
    ///         driven by the clock alone, which is a weaker ramp rather than a broken one.
    ///     </para>
    /// </summary>
    public bool Adaptive { get; init; } = true;

    /// <summary>Consecutive failures before opening. The reading most people have of "circuit breaker".</summary>
    public int ConsecutiveFailures { get; init; } = 5;

    /// <summary>
    ///     Optional rate-based trip, evaluated alongside the consecutive counter. Null disables it, and
    ///     nothing rate-based - including <see cref="SlowCallThreshold" /> - is evaluated until
    ///     <see cref="MinimumCalls" /> outcomes have landed in <see cref="TripWindow" />.
    ///     <para>
    ///         This is an absolute rate, which is the number that ports nowhere: 5% is catastrophic for a
    ///         payments API and a quiet day for a flaky search backend. <see cref="Failures" /> is the same
    ///         trip expressed as a multiple of the dependency's own measured rate. Setting both is
    ///         supported and useful - the absolute number becomes the ceiling.
    ///     </para>
    /// </summary>
    public double? FailureRatio { get; init; }

    /// <summary>
    ///     The same rate-based trip as <see cref="FailureRatio" />, defined relative to how often this
    ///     dependency normally fails: <c>Failures.Above(5)</c> is "five times its own recent error rate".
    ///     <para>
    ///         The breaker measures the baseline itself, from the outcomes it already samples, so nothing
    ///         has to be guessed up front and nothing has to be re-tuned when the dependency's error rate
    ///         changes. It composes with its absolute counterpart the way every measured term in the
    ///         library does: when <see cref="FailureRatio" /> is also set, the relative trip can only
    ///         fire sooner, never later. See <see cref="NResilience.Failures" /> for why the absolute
    ///         floor and the long window are both required rather than cosmetic.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     On by default at <c>Failures.Above(5)</c>. Set it to <c>null</c> to turn the relative trip
    ///     off; set it to a value to change it. The default is invisible until the breaker has a
    ///     baseline - <see cref="NResilience.Failures.MinimumSamples" /> outcomes over
    ///     <see cref="NResilience.Failures.Window" /> - and cannot fire below
    ///     <see cref="NResilience.Failures.Floor" /> however quiet that baseline was.
    /// </remarks>
    public Failures? Failures
    {
        get => _failuresSet ? _failures : DefaultFailures();
        init
        {
            _failures = value;
            _failuresSet = true;
        }
    }

    /// <summary>How many sampled calls a rate-based trip needs before it means anything.</summary>
    public int MinimumCalls { get; init; } = 20;

    /// <summary>
    ///     The sliding window the trip ratios are measured over: how much recent history the breaker
    ///     decides on.
    ///     <para>
    ///         Not to be confused with <see cref="NResilience.SlowCalls.Window" /> and
    ///         <see cref="NResilience.Failures.Window" />, which are the <i>baseline</i> windows the two
    ///         relative trips measure "normal" over. Those are ten times longer by default and serve the
    ///         opposite purpose: this window is what reacts quickly, and a baseline is what has to
    ///         outlast the incident it is measuring.
    ///     </para>
    /// </summary>
    public TimeSpan TripWindow { get; init; } = TimeSpan.FromSeconds(30);

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
    ///         as a multiple of measured normal latency, which is the form that ports. Setting both is
    ///         supported and composes: an attempt is slow when it is above either threshold, so naming a
    ///         constant here tightens the trip rather than replacing the measured one.
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
    /// <remarks>
    ///     On by default at <c>SlowCalls.Above(3)</c>, because a dependency answering <c>200 OK</c> in
    ///     thirty seconds is the most common way one fails and an error-rate breaker sits closed through
    ///     the whole incident. Set it to <c>null</c> to turn the brownout trip off; set it to a value to
    ///     change it. Naming <see cref="SlowCallThreshold" /> does not turn it off: the two are the same
    ///     trip defined two ways, and they compose, so an attempt is slow when it is above either
    ///     threshold.
    ///     <para>
    ///         The default is invisible until the breaker has a baseline -
    ///         <see cref="NResilience.SlowCalls.MinimumSamples" /> successful attempts over
    ///         <see cref="NResilience.SlowCalls.Window" /> - so a cold breaker behaves exactly as a
    ///         consecutive-failures one does.
    ///     </para>
    /// </remarks>
    public SlowCalls? SlowCalls
    {
        get => _slowCallsSet ? _slowCalls : DefaultSlowCalls();
        init
        {
            _slowCalls = value;
            _slowCallsSet = true;
        }
    }

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
    public TimeSpan MaximumBreakDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     How much randomness to apply to the break duration. <see cref="Jitter.Equal" /> by default:
    ///     the break runs for <c>half the computed duration + random(0, half)</c>.
    ///     <para>
    ///         Two hundred pods watching one dependency fail all open within a second of each other and
    ///         all set the same break, so fifteen seconds later they all probe in the same second and a
    ///         dependency halfway through recovering takes a two-hundred-request synchronized pulse. If it
    ///         fails them they re-open together, with a doubled break, and do it again. Jitter is what
    ///         breaks that correlation, for exactly the reason <see cref="Backoff.Jitter" /> defaults to
    ///         it - <see cref="HalfOpenProbes" /> makes each pod polite and does nothing about the fleet.
    ///     </para>
    ///     <para>
    ///         <see cref="Jitter.Equal" /> rather than <see cref="Jitter.Full" />, because the break
    ///         duration has a purpose beyond de-correlation: it is how long the dependency gets left
    ///         alone, and full jitter would let a pod probe after 200 ms of a 15-second break.
    ///         <see cref="Jitter.None" /> is the escape hatch for a test that needs a break to expire at
    ///         exactly <see cref="BreakDuration" />.
    ///     </para>
    /// </summary>
    public Jitter BreakJitter { get; init; } = Jitter.Equal;

    /// <summary>
    ///     How the traffic is handed back once the probes succeed. Null - the default - is a cliff:
    ///     <see cref="ProbeSuccesses" /> probes land and the entire offered load hits the dependency in
    ///     the next millisecond.
    ///     <para>
    ///         <c>Recovery.Over(0.25)</c> puts a fourth state between half-open and closed, in which a
    ///         growing fraction of calls is admitted and the rest are refused the way an open breaker
    ///         refuses them. The ramp's length comes from the break just served and its growth comes
    ///         from whether the admitted calls are fast, so there is no number to pick beyond the
    ///         fraction itself. See <see cref="NResilience.Recovery" /> for what it costs and why it is
    ///         opt-in.
    ///     </para>
    /// </summary>
    public Recovery? Recovery { get; init; }

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
        get => ConfiguredTime ?? TimeProvider.System;
        init => ConfiguredTime = value;
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
    internal TimeProvider? ConfiguredTime { get; private set; }

    /// <summary>
    ///     Runs <see cref="Validate" /> and returns these settings, so a bad configuration throws where
    ///     it is written. The shape for the <c>static readonly</c> field a shared breaker's settings
    ///     usually live in; a <c>with</c> expression needs parentheses before the call.
    /// </summary>
    /// <returns>These settings.</returns>
    /// <exception cref="ResilienceConfigurationException">The settings cannot be used.</exception>
    public BreakerSettings Validated()
    {
        Validate();
        return this;
    }

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

        if (TripWindow <= TimeSpan.Zero)
            problems.Add($"{nameof(TripWindow)} must be positive; it is {TripWindow}.");

        if (SlowCallThreshold is { } slow && slow <= TimeSpan.Zero)
            problems.Add($"{nameof(SlowCallThreshold)} must be positive, or null for no slow-call trip; it is {slow}.");

        if (!Adaptive)
        {
            // The same rule Resilience.Adaptive follows: suppress the defaults, refuse the
            // contradiction, and report both halves rather than ranking them.
            if (SlowCalls is not null)
            {
                problems.Add(
                    $"{nameof(Adaptive)} is false, so this breaker measures nothing, but {nameof(SlowCalls)} is set. " +
                    $"Remove one, or state the brownout trip as a constant with {nameof(SlowCallThreshold)}.");
            }

            if (Failures is not null)
            {
                problems.Add(
                    $"{nameof(Adaptive)} is false, so this breaker measures nothing, but {nameof(Failures)} is set. " +
                    $"Remove one, or state the rate trip as a constant with {nameof(FailureRatio)}.");
            }
        }

        if (SlowCalls is { } adaptive)
        {
            adaptive.Validate(problems);

            // Only worth asking once the two values it is measured against are themselves sane; each of
            // those has its own message, and a second one derived from a NaN would only be noise.
            if (TripWindow > TimeSpan.Zero && SlowCallRatio > 0 && SlowCallRatio <= 1)
                ValidateRace(adaptive, problems);
        }

        if (Failures is { } relative)
        {
            relative.Validate(problems);

            // Only worth asking once the two values it is measured against are themselves sane; each
            // of those has its own message, and a second one derived from a NaN would only be noise.
            if (TripWindow > TimeSpan.Zero && relative.Multiple > 1 && relative.Window > TimeSpan.Zero)
                ValidateRace(relative, problems);
        }

        if (SlowCallRatio <= 0 || SlowCallRatio > 1 || double.IsNaN(SlowCallRatio))
            problems.Add($"{nameof(SlowCallRatio)} must be in (0, 1]; it is {SlowCallRatio}.");

        if (BreakDuration <= TimeSpan.Zero)
            problems.Add($"{nameof(BreakDuration)} must be positive; it is {BreakDuration}.");

        if (MaximumBreakDuration < BreakDuration)
            problems.Add($"{nameof(MaximumBreakDuration)} must be at least {nameof(BreakDuration)}; they are {MaximumBreakDuration} and {BreakDuration}.");

        if (Recovery is { } ramp)
            ramp.Validate(problems);

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
    ///         survives for <c>SlowCalls.Quantile × SlowCalls.Window</c>. The trip needs <see cref="SlowCallRatio" /> of
    ///         <see cref="TripWindow" /> to fill with slow calls, which takes <c>SlowCallRatio × TripWindow</c>.
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
        var fills = SlowCallRatio * TripWindow.TotalSeconds;

        if (survives >= 2 * fills)
            return;

        problems.Add(
            $"{nameof(SlowCalls)}.Quantile x {nameof(SlowCalls)}.Window ({survives:0.##}s) must be at least twice " +
            $"{nameof(SlowCallRatio)} x {nameof(TripWindow)} ({fills:0.##}s), or a brownout moves the baseline before the " +
            $"window fills with slow calls and the breaker can never open on latency. Lengthen {nameof(SlowCalls)}.Window, " +
            $"lower {nameof(SlowCalls)}.Quantile, or shorten {nameof(TripWindow)}.");
    }

    /// <summary>
    ///     The same race, for the relative failure trip: a baseline that has absorbed the incident
    ///     before the trip window has filled with it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A total outage raises the baseline as it fills <see cref="NResilience.Failures.Window" />,
    ///         so after <c>t</c> seconds the baseline reads <c>t / Failures.Window</c> and the trip point
    ///         reads <c>Multiple x</c> that. The trip window's own ratio cannot exceed 1, so once the
    ///         baseline reaches <c>1 / Multiple</c> the breaker cannot open on the error rate at all -
    ///         which takes <c>Failures.Window / Multiple</c> seconds. The trip window needs
    ///         <see cref="TripWindow" /> to turn over.
    ///     </para>
    ///     <para>
    ///         A factor of two is the margin, the same one <see cref="SlowCalls" /> is held to, and the
    ///         defaults meet it exactly: a 5-minute baseline at a multiple of 5 survives 60 s against a
    ///         30-second window. Raising <see cref="NResilience.Failures.Multiple" /> therefore wants a
    ///         longer baseline, which is the trade the message states.
    ///     </para>
    /// </remarks>
    private void ValidateRace(Failures relative, List<string> problems)
    {
        var survives = relative.Window.TotalSeconds / relative.Multiple;
        var fills = TripWindow.TotalSeconds;

        if (survives >= 2 * fills)
            return;

        problems.Add(
            $"{nameof(Failures)}.Window / {nameof(Failures)}.Multiple ({survives:0.##}s) must be at least twice " +
            $"{nameof(TripWindow)} ({fills:0.##}s), or an outage raises the baseline before the trip window fills with " +
            $"failures and the breaker can never open on the error rate. Lengthen {nameof(Failures)}.Window, lower " +
            $"{nameof(Failures)}.Multiple, or shorten {nameof(TripWindow)}.");
    }

    /// <summary>The relative failure trip a caller who never mentioned one gets.</summary>
    /// <returns>The configuration, or null when the library declines to default one on.</returns>
    /// <remarks>
    ///     <see cref="NResilience.Failures.Window" />'s own default wins the contamination race
    ///     <see cref="ValidateRace(Failures, List{string})" /> checks at <see cref="TripWindow" />'s default
    ///     and only there, so a default the caller did not write widens its baseline to whatever the
    ///     configured trip window needs. The library does not refuse a configuration on account of a
    ///     value the caller never named - and past <see cref="MaxDerivedBaseline" /> a baseline stops
    ///     describing "normally" at all, so there the default steps aside instead.
    /// </remarks>
    private Failures? DefaultFailures()
    {
        if (!Adaptive)
            return null;

        var failures = NResilience.Failures.Above();

        // TripWindow has its own message in Validate, and the race check skips itself for the same
        // reason: a second complaint derived from a nonsense value would only be noise.
        if (TripWindow <= TimeSpan.Zero)
            return failures;

        if (Baseline(2 * DefaultFailureMultiple * TripWindow.Ticks, failures.Window) is not { } baseline)
            return null;

        return baseline == failures.Window ? failures : failures with { Window = baseline };
    }

    /// <summary>The brownout trip a caller who never mentioned one gets.</summary>
    /// <returns>The configuration, or null when the library declines to default one on.</returns>
    /// <remarks>The baseline widens for the reason <see cref="DefaultFailures" /> gives.</remarks>
    private SlowCalls? DefaultSlowCalls()
    {
        if (!Adaptive)
            return null;

        var slow = NResilience.SlowCalls.Above();

        // Each of these has its own message in Validate; see DefaultFailures.
        if (TripWindow <= TimeSpan.Zero || double.IsNaN(SlowCallRatio) || SlowCallRatio <= 0 || SlowCallRatio > 1)
            return slow;

        if (Baseline(2 * SlowCallRatio * TripWindow.Ticks / slow.Quantile, slow.Window) is not { } baseline)
            return null;

        return baseline == slow.Window ? slow : slow with { Window = baseline };
    }

    /// <summary>
    ///     The baseline window a derived default gets: its own, when that already outlasts the trip
    ///     window by the factor the race check demands, and the demanded span when it does not.
    /// </summary>
    /// <param name="ticks">The ticks the race check demands of the baseline.</param>
    /// <param name="own">The baseline window the value defaults to on its own.</param>
    /// <returns>The window, or null when the demanded span is longer than one worth measuring.</returns>
    private static TimeSpan? Baseline(double ticks, TimeSpan own)
    {
        if (double.IsNaN(ticks) || ticks > MaxDerivedBaseline.Ticks)
            return null;

        if (ticks <= own.Ticks)
            return own;

        // A second of cushion on a widened window, so the "at least twice" the race check wants
        // cannot be lost to rounding on the way back out through a double.
        return TimeSpan.FromTicks((long)Math.Ceiling(ticks) + TimeSpan.TicksPerSecond);
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
    ///     Cap on the doubling exponent. <c>MaximumBreakDuration</c> is the real bound; this only keeps the
    ///     shift from overflowing after a very long outage.
    /// </summary>
    private const int MaxGrowthShift = 40;

    /// <summary>
    ///     Failures the trip window needs before a relative <see cref="BreakerSettings.Failures" /> may
    ///     open the breaker, whatever the ratio says. Two, because one failure is a single event and
    ///     this trip is a claim about a rate.
    /// </summary>
    private const int MinimumRelativeFailures = 2;

    /// <summary>
    ///     <see cref="Settings" />'s two relative trips, read once. Both are defaulted on read rather
    ///     than stored, so reading them per attempt would recompute a default the settings cannot
    ///     change. Non-null exactly when <see cref="_normal" /> and <see cref="_rate" /> are.
    /// </summary>
    private readonly SlowCalls? _adaptive;

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

    /// <summary>
    ///     The measured baseline error rate a relative <see cref="BreakerSettings.Failures" /> is
    ///     multiplied against. Deliberately never cleared by <see cref="ClearWindow" />, for the reason
    ///     <see cref="_normal" /> is not: it is a measurement of the dependency, not a decision about
    ///     it, and the design depends on it outliving the trip window it is raced against.
    /// </summary>
    private readonly RateWindow? _rate;

    /// <summary>
    ///     How the traffic is handed back, read once for the reason <see cref="_adaptive" /> is. Null -
    ///     the default - means the breaker closes on a cliff and <see cref="BreakerState.Recovering" />
    ///     is unreachable.
    /// </summary>
    private readonly Recovery? _recovery;

    private readonly Failures? _relative;

    private readonly int[]? _slow;
    private readonly long _startedAt;
    private readonly long _ticksPerBucket;
    private readonly TimeProvider _time;

    /// <summary>The break the current open is actually serving, jitter included - the ramp's input.</summary>
    private long _breakServed;

    private long _breakUntil;
    private int _consecutiveFailures;
    private int _consecutiveOpens;

    private long _epoch = -1;
    private DateTimeOffset _openedAt;
    private int _probeSuccesses;
    private int _probesInFlight;

    /// <summary>
    ///     The ceiling the evidence puts on the admitted fraction. It starts at 1 - no cap - halves
    ///     whenever an admitted call comes back slow, and climbs by one
    ///     <see cref="NResilience.Recovery.InitialFraction" /> behind every
    ///     <see cref="BreakerSettings.ProbeSuccesses" /> that come back fast. The clock is the other
    ///     half, and the effective fraction is the lower of the two.
    /// </summary>
    private double _rampAdmit;

    /// <summary>
    ///     Deficit accounting for the admitted fraction, so a ramp admits an even <c>p</c> of the calls
    ///     offered to it rather than a random <c>p</c>. Deterministic on purpose: the fleet is already
    ///     de-correlated by <see cref="BreakerSettings.BreakJitter" />, which decides when each pod's
    ///     ramp starts, so a second source of randomness would buy nothing and cost the simulation.
    /// </summary>
    private double _rampCredit;

    private long _rampStartedAt;
    private int _rampSuccesses;
    private long _rampTicks;
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
        _ticksPerBucket = Math.Max(Settings.TripWindow.Ticks / BucketCount, 1);

        // The window arrays exist only when something reads them. A breaker whose relative trips have
        // both been turned off, leaving only the consecutive counter, is three fields and no
        // allocation beyond the object itself.
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
        {
            _adaptive = adaptive;
            _normal = new LatencyWindow(adaptive.Quantile, adaptive.Window, _time);
        }

        // The same bargain for the error rate, and the same reason it lives on the breaker: two
        // policies sharing a breaker are two views of one dependency, and they should share one idea
        // of how often that dependency fails.
        if (Settings.Failures is { } relative)
        {
            _relative = relative;
            _rate = new RateWindow(relative.Window);
        }

        _recovery = Settings.Recovery;
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
    ///     <para>
    ///         A <see cref="BreakerState.Recovering" /> breaker whose ramp has run out reports
    ///         <see cref="BreakerState.Closed" /> for the same reason, and a ramp still stalled on slow
    ///         traffic keeps reporting <see cref="BreakerState.Recovering" /> however long ago it
    ///         started - which is the reading worth alerting on.
    ///     </para>
    /// </summary>
    public BreakerState State
    {
        get
        {
            lock (_gate)
            {
                return _state switch
                {
                    BreakerState.Open when Elapsed() >= _breakUntil => BreakerState.HalfOpen,
                    BreakerState.Recovering when Admission(Elapsed()) >= 1 => BreakerState.Closed,
                    _ => _state,
                };
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
        _normal is null ? null : _normal.Threshold(_adaptive!.Value.MinimumSamples);

    /// <summary>
    ///     How often a call to this dependency currently fails, as this breaker measures it - the number
    ///     a relative <see cref="BreakerSettings.Failures" /> multiplies. Null when the breaker is not
    ///     configured that way, or has not seen <see cref="NResilience.Failures.MinimumSamples" />
    ///     outcomes yet.
    ///     <para>
    ///         Worth graphing beside <see cref="NormalLatency" />. It is the library's answer to "how
    ///         reliable is this dependency, normally?", and the trip point is
    ///         <c>max(Failures.Floor, NormalFailureRate x Failures.Multiple)</c>.
    ///     </para>
    /// </summary>
    public double? NormalFailureRate
    {
        get
        {
            if (_rate is null)
                return null;

            lock (_gate)
            {
                return _rate.Ratio(_relative!.Value.MinimumSamples);
            }
        }
    }

    /// <summary>
    ///     When the breaker last opened, or null while it is closed.
    ///     <para>
    ///         A <see cref="BreakerState.Recovering" /> breaker still reports it. The ramp is the tail
    ///         of the open it is recovering from, and "recovering, open since 12:04:11" is the reading
    ///         an operator wants; the timestamp is cleared when the ramp completes.
    ///     </para>
    /// </summary>
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
            ClearRamp();
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
    ///     probe slots - so every true must be followed by exactly one <see cref="Record" />, or by
    ///     <see cref="ReleaseProbe" /> when <paramref name="probe" /> came back true.
    /// </summary>
    /// <param name="transition">
    ///     The state change this admission caused, for the caller to report. Reported by the caller
    ///     rather than raised here because the transition happens under the breaker's lock and a
    ///     listener is arbitrary user code: raising inside the lock would let one slow listener
    ///     serialize every call through the breaker.
    /// </param>
    /// <param name="probe">
    ///     Whether this admission consumed a probe slot, which only the half-open state hands out. It
    ///     is the caller's obligation to give one back - see <see cref="ReleaseProbe" /> - and knowing
    ///     it here is the only reliable way: a closed breaker consumes nothing, and by the time an
    ///     attempt finishes the breaker may have opened and half-opened again, so the state at release
    ///     time cannot answer "did I take one of these?".
    /// </param>
    internal bool TryEnter(out BreakerTransition transition, out bool probe)
    {
        transition = BreakerTransition.None;
        probe = false;

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
                    probe = true;
                    return true;

                case BreakerState.HalfOpen:
                    if (_probesInFlight >= Settings.HalfOpenProbes)
                        return false;

                    _probesInFlight++;
                    probe = true;
                    return true;

                case BreakerState.Recovering:
                    var admit = Admission(Elapsed());

                    // The ramp finished. Closing here rather than on the next recorded outcome means a
                    // breaker whose ramp ran out during a quiet minute is closed by the call that ends
                    // the quiet, not left reporting Recovering behind it.
                    if (admit >= 1)
                    {
                        CloseCore();
                        return true;
                    }

                    // Deficit accounting rather than a coin flip: p of the calls offered, evenly
                    // spaced. Two hundred pods do not have to be de-correlated here, because the
                    // jittered break already decided that they would not start their ramps together.
                    _rampCredit += admit;

                    if (_rampCredit < 1)
                        return false;

                    _rampCredit -= 1;
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
    ///     <para>
    ///         Without this, a probe admitted while half-open but never recorded leaves
    ///         <see cref="_probesInFlight" /> at its cap and the breaker wedged in <see cref="BreakerState.HalfOpen" />
    ///         forever: every subsequent <see cref="TryEnter" /> sees the slots full and refuses, and the
    ///         breaker has no clock-driven path back to <see cref="BreakerState.Open" /> that would reset them.
    ///     </para>
    ///     <para>
    ///         <b>Call this only for an admission whose <c>probe</c> came back true.</b> The state check
    ///         below cannot stand in for that: it says what the breaker is doing <i>now</i>, and the
    ///         call being cleaned up was admitted some time ago. An attempt admitted through a closed
    ///         breaker took no slot at all, and if the breaker has opened and half-opened since, a
    ///         release here would hand back a slot that a different call is holding - letting one more
    ///         probe through than <see cref="BreakerSettings.HalfOpenProbes" /> allows.
    ///     </para>
    /// </remarks>
    internal void ReleaseProbe()
    {
        lock (_gate)
        {
            // Belt and braces for the caller's obligation above: a probe that closed or re-opened the
            // breaker moved the state away from HalfOpen and RecordCore already released its slot, so
            // this keeps that ordering from double-releasing.
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
        var recovering = _state == BreakerState.Recovering;

        if (probe && _probesInFlight > 0)
            _probesInFlight--;

        var now = Elapsed();

        if (kind == VerdictKind.Ok)
        {
            var slow = IsSlow(duration);
            _consecutiveFailures = 0;

            // A slow call during a ramp is recorded as an outcome but not as a slow one. Slow is the
            // expected reading while a dependency warms, and letting it reach the slow-call trip
            // would re-open the breaker every time - which makes the stall below, the one thing that
            // can say "up, and not ready", unreachable.
            Bucket(now, false, slow && _state != BreakerState.Recovering);

            if (probe)
            {
                // A slow probe is not a recovery. Closing on a 200 that took 30 s hands the waiting
                // client fleet straight back to a dependency that is still in trouble - so without a
                // ramp the only other answer available is to re-open.
                //
                // With a ramp there is a third answer, and it is the right one: "up, and not ready"
                // is the state the ramp exists to express. The probe counts, the ramp starts, and it
                // stalls at Recovery.InitialFraction until the traffic it admits comes back fast. That
                // trickle is also the traffic the dependency needs in order to warm, which re-opening
                // would deny it - and it is bounded, unlike the close a slow probe must never cause.
                if (slow && _recovery is null)
                    OpenCore(now);
                else if (++_probeSuccesses >= Settings.ProbeSuccesses)
                    RecoverCore(now);

                return;
            }

            if (recovering)
            {
                Pace(now, slow);
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

        // A failure during the ramp re-opens on the spot, without waiting for the trip conditions a
        // closed breaker is held to. The ramp is a hypothesis about a dependency that was broken
        // ninety seconds ago, and one transient failure is enough to withdraw it - the break the
        // re-open serves is the doubled one, because nothing has closed the breaker cleanly yet.
        if (probe || recovering)
        {
            OpenCore(now);
            return;
        }

        Evaluate(now);
    }

    /// <summary>
    ///     How long until admission might succeed, for the <see cref="CallRejectedException" /> a
    ///     refusal carries. Null when there is nothing useful to say - an isolated breaker will not
    ///     self-heal, a half-open one is waiting on a probe rather than on a clock, and a recovering
    ///     one will very likely admit the caller's next call, so any span it named would be an
    ///     overstatement the caller would honour.
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
        var threshold = Settings.SlowCallThreshold;

        if (_normal is { } baseline)
        {
            var adaptive = _adaptive!.Value;

            // Recorded whether or not an absolute threshold is also set: the baseline is a measurement
            // of the dependency, not a decision about it, and a breaker that stopped measuring because
            // somebody named a constant could never start again.
            if (baseline.RecordAndThreshold(duration, adaptive.MinimumSamples) is { } measured)
            {
                var relative = adaptive.ThresholdFor(measured);

                threshold = threshold is { } absolute && absolute < relative ? absolute : relative;
            }
        }

        return threshold is { } effective && duration >= effective;
    }

    private static bool IsWindowed(BreakerSettings settings) =>
        settings.FailureRatio is not null
        || settings.Failures is not null
        || settings.SlowCallThreshold is not null
        || settings.SlowCalls is not null;

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

        if (TripsOnRate(calls))
        {
            OpenCore(now);
            return;
        }

        if ((Settings.SlowCallThreshold is not null || _normal is not null) && Sum(_slow!) >= Settings.SlowCallRatio * calls)
            OpenCore(now);
    }

    /// <summary>
    ///     Whether the trip window's error rate is enough to open the breaker, against
    ///     <see cref="BreakerSettings.FailureRatio" />, against the measured baseline, or both. Always
    ///     called with the lock held, and only once the window holds
    ///     <see cref="BreakerSettings.MinimumCalls" /> outcomes.
    /// </summary>
    /// <param name="calls">How many outcomes the trip window holds.</param>
    /// <returns>True when the breaker should open.</returns>
    /// <remarks>
    ///     A relative trip can only fire sooner than the absolute one, never later:
    ///     <see cref="BreakerSettings.FailureRatio" /> stays the ceiling when both are set. Until the
    ///     baseline has <see cref="NResilience.Failures.MinimumSamples" /> outcomes there is no relative
    ///     trip at all, so a cold breaker behaves exactly as it does without the feature.
    /// </remarks>
    private bool TripsOnRate(int calls)
    {
        var failures = Sum(_failures!);

        if (Settings.FailureRatio is { } absolute && failures >= absolute * calls)
            return true;

        if (_rate is null)
            return false;

        // One failure is not a rate. At the default floor of 5% a 20-call window would otherwise
        // open on a single transient error against a dependency that has never failed once, which
        // is twitchier than anything else the breaker does. The absolute trip above is deliberately
        // left alone: a caller who wrote FailureRatio = 0.05 with MinimumCalls = 20 asked for
        // exactly that reading, and this feature does not get to second-guess it.
        if (failures < MinimumRelativeFailures)
            return false;

        return TripRatio() is { } trip && failures >= trip * calls;
    }

    /// <summary>
    ///     The proportion of the trip window that has to fail to open this breaker, or null while
    ///     nothing rate-based is armed - no <see cref="BreakerSettings.FailureRatio" />, and no baseline
    ///     for <see cref="BreakerSettings.Failures" /> to multiply yet. Always called with the lock held.
    /// </summary>
    /// <returns>The effective trip ratio.</returns>
    /// <remarks>
    ///     When both trips are configured the relative one can only fire sooner, so the effective ratio
    ///     is the lower of the two.
    /// </remarks>
    private double? TripRatio()
    {
        var trip = Settings.FailureRatio;

        if (_rate?.Ratio(_relative!.Value.MinimumSamples) is { } baseline)
        {
            var relative = _relative.Value.ThresholdFor(baseline);
            trip = trip is { } ceiling ? Math.Min(ceiling, relative) : relative;
        }

        return trip;
    }

    /// <summary>
    ///     Whether the trip window's error rate has reached <paramref name="fraction" /> of the rate that
    ///     would open this breaker - the "closed but not healthy" reading that suppresses hedging.
    /// </summary>
    /// <param name="fraction">How far towards the trip point counts as elevated.</param>
    /// <returns>True when the dependency is failing often enough that it should not be sent extra load.</returns>
    /// <remarks>
    ///     Disarmed until there is something to be a fraction of: the window needs
    ///     <see cref="BreakerSettings.MinimumCalls" /> outcomes, a rate-based trip has to be armed, and -
    ///     for the same reason the relative trip does - two failures have to have happened, because one
    ///     failure is an event rather than a rate.
    /// </remarks>
    internal bool IsErrorRateElevated(double fraction)
    {
        lock (_gate)
        {
            if (_calls is null)
                return false;

            var calls = Sum(_calls);

            if (calls < Settings.MinimumCalls)
                return false;

            var failures = Sum(_failures!);

            return failures >= MinimumRelativeFailures
                   && TripRatio() is { } trip
                   && failures >= fraction * trip * calls;
        }
    }

    private void OpenCore(long now)
    {
        _transition = BreakerTransition.Opened;
        _state = BreakerState.Open;
        _openedAt = _time.GetUtcNow();

        // Exponential backoff applied to the breaker itself. The first open serves BreakDuration;
        // each consecutive one doubles, capped by MaximumBreakDuration, and a clean close resets it.
        var grown = Settings.BreakDuration.Ticks << Math.Min(_consecutiveOpens, MaxGrowthShift);
        var capped = Math.Min(grown <= 0 ? long.MaxValue : grown, Settings.MaximumBreakDuration.Ticks);

        // Jittered once, here, so RetryAfterHint reports the break this breaker is actually serving
        // rather than the nominal one. The growth above is computed from the nominal duration, so
        // jitter de-correlates the fleet without slowing the backoff down.
        _breakServed = Jittered(capped);
        _breakUntil = now + _breakServed;
        _consecutiveOpens = Math.Min(_consecutiveOpens + 1, MaxGrowthShift);
        _probesInFlight = 0;
        _probeSuccesses = 0;
        _consecutiveFailures = 0;
        ClearRamp();
        ClearWindow();
    }

    /// <summary>
    ///     What the probes succeeding means: a ramp when one is configured, and the cliff a breaker
    ///     without one has always had. Always called with the lock held.
    /// </summary>
    /// <param name="now">The current elapsed reading.</param>
    /// <remarks>
    ///     <para>
    ///         The transition reported is <see cref="BreakerTransition.Closed" /> either way, and it is
    ///         reported here rather than when the ramp completes, because this is the moment the
    ///         breaker stops refusing everything. A listener that sees <c>BreakerClosed</c> and then a
    ///         <see cref="CallEventKind.RejectedByBreaker" /> is seeing the ramp, and
    ///         <see cref="State" /> says <see cref="BreakerState.Recovering" /> for exactly as long as
    ///         that lasts. The completion is deliberately silent: a second <c>BreakerClosed</c> for one
    ///         recovery would make the event sequence lie about how many times the breaker closed.
    ///     </para>
    ///     <para>
    ///         <see cref="Reset" /> does not come through here. It is administrative - somebody decided
    ///         the dependency is fine - and warming a dependency the operator has already vouched for
    ///         is not something an operator means by "reset".
    ///     </para>
    /// </remarks>
    private void RecoverCore(long now)
    {
        // A ramp derived from a break of zero would be the Minimum floor rather than anything the
        // breaker measured, and _breakServed is only zero when this breaker never opened.
        if (_recovery is not { } recovery || _breakServed <= 0)
        {
            CloseCore();
            return;
        }

        _transition = BreakerTransition.Closed;
        _state = BreakerState.Recovering;
        _breakUntil = 0;
        _consecutiveFailures = 0;
        _probesInFlight = 0;
        _probeSuccesses = 0;

        // No evidence-based cap yet, so the clock alone paces a recovery nothing has complained
        // about - including one with no traffic at all, which would otherwise report Recovering
        // forever because it never collected the successes a floor-to-ceiling climb would need.
        _rampAdmit = 1;
        _rampCredit = 0;
        _rampSuccesses = 0;
        _rampStartedAt = now;
        _rampTicks = recovery.RampFor(TimeSpan.FromTicks(_breakServed)).Ticks;

        // The accumulated growth is not forgotten yet. A ramp that fails is not a clean close, and
        // the break it re-opens with has to be the doubled one or a dependency that fails every ramp
        // gets probed on a fixed cadence forever - the exact flapping MaximumBreakDuration exists to stop.
        ClearWindow();
    }

    /// <summary>
    ///     The evidence half of the ramp, applied to one admitted outcome. Always called with the lock
    ///     held, and only while <see cref="_state" /> is <see cref="BreakerState.Recovering" />.
    /// </summary>
    /// <param name="now">The current elapsed reading.</param>
    /// <param name="slow">Whether the call was slow, as <see cref="IsSlow" /> judged it.</param>
    /// <remarks>
    ///     <para>
    ///         AIMD, with the same asymmetry the adaptive limiter uses and for the same reason: evidence
    ///         that a dependency is coping is weak and evidence that it is not is strong. A slow call
    ///         halves the admitted fraction on the spot, floored at <see cref="NResilience.Recovery.InitialFraction" />;
    ///         <see cref="BreakerSettings.ProbeSuccesses" /> fast ones in a row add one
    ///         <see cref="NResilience.Recovery.InitialFraction" /> back, capped at 1, where the clock takes the
    ///         decision back. Additive rather than doubling on the way up because a ramp that overshoots
    ///         saturates the dependency, and a saturated dependency stops warming - which is the failure
    ///         the whole feature exists to avoid.
    ///     </para>
    ///     <para>
    ///         A ramp sitting at 12% because the traffic it admitted is three times slower than normal
    ///         is the feature working, and it is the only way a breaker has of saying that the
    ///         dependency is up and is not ready. Halving rather than merely holding is what lets the
    ///         ramp find a level the dependency can actually serve - a ramp pinned above that level
    ///         keeps it saturated, and a saturated dependency never warms.
    ///     </para>
    /// </remarks>
    private void Pace(long now, bool slow)
    {
        if (slow)
        {
            _rampAdmit = Math.Max(_recovery!.Value.InitialFraction, Admission(now) / 2);
            _rampSuccesses = 0;
            return;
        }

        if (++_rampSuccesses < Settings.ProbeSuccesses)
            return;

        _rampAdmit = Math.Min(1, _rampAdmit + _recovery!.Value.InitialFraction);
        _rampSuccesses = 0;
    }

    /// <summary>
    ///     The fraction of offered calls a ramp admits right now: the lower of what the clock has
    ///     reached and what the evidence has earned. Always called with the lock held, and only while
    ///     <see cref="_state" /> is <see cref="BreakerState.Recovering" />, which is reachable only when
    ///     <see cref="_recovery" /> is set.
    /// </summary>
    /// <param name="now">The current elapsed reading.</param>
    /// <returns>The admitted fraction. 1 or more means the ramp is over.</returns>
    /// <remarks>
    ///     Both halves are needed and neither is sufficient. A purely evidence-driven ramp completes in
    ///     milliseconds against a busy dependency - ten successful calls at a thousand a second - which
    ///     is the cliff again; a purely clock-driven one hands the traffic back on schedule to a
    ///     dependency that is answering three times slower than normal. The clock paces a healthy
    ///     recovery and the evidence stalls an unhealthy one.
    /// </remarks>
    private double Admission(long now)
    {
        var initial = _recovery!.Value.InitialFraction;
        var elapsed = now - _rampStartedAt;
        var clock = _rampTicks <= 0 ? 1 : initial + (1 - initial) * ((double)elapsed / _rampTicks);

        return Math.Min(_rampAdmit, clock);
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
        ClearRamp();
        ClearWindow();
    }

    private void ClearRamp()
    {
        _rampAdmit = 0;
        _rampCredit = 0;
        _rampSuccesses = 0;
        _rampStartedAt = 0;
        _rampTicks = 0;
    }

    /// <summary>
    ///     The break this open actually serves. Never longer than the computed duration, so
    ///     <see cref="BreakerSettings.MaximumBreakDuration" /> still bounds it.
    /// </summary>
    /// <param name="ticks">The computed break duration.</param>
    /// <returns>The jittered break duration.</returns>
    private long Jittered(long ticks)
    {
        var jittered = Settings.BreakJitter switch
        {
            Jitter.Full => ticks * Rng.NextDouble(),
            Jitter.Equal => ticks / 2.0 + ticks / 2.0 * Rng.NextDouble(),
            _ => ticks,
        };

        return jittered >= long.MaxValue ? long.MaxValue : (long)jittered;
    }

    private void Bucket(long now, bool failure, bool slow)
    {
        // The baseline sees the same sample stream the trip window does, and outlives it: a rate
        // measured over five minutes is the only thing that can say whether thirty seconds of
        // failures is unusual for this dependency.
        _rate?.Record(now, failure);

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

    /// <summary>
    ///     A sliding failure rate over a window of its own - the baseline a relative
    ///     <see cref="BreakerSettings.Failures" /> is measured against.
    /// </summary>
    /// <remarks>
    ///     Deliberately not the same rings the trip window uses, even though the rotation is the same
    ///     shape. The trip window is cleared on every open, because a decision must not be made twice
    ///     on the same evidence; this one must survive exactly that, or the breaker would forget what
    ///     normal was at the moment it most needs to know. It is guarded by the breaker's lock, like
    ///     the trip window, rather than being thread-safe on its own.
    /// </remarks>
    private sealed class RateWindow
    {
        private readonly int[] _calls = new int[BucketCount];
        private readonly int[] _failures = new int[BucketCount];
        private readonly long _ticksPerBucket;
        private long _epoch = -1;

        public RateWindow(TimeSpan window)
        {
            _ticksPerBucket = Math.Max(window.Ticks / BucketCount, 1);
        }

        public void Record(long now, bool failure)
        {
            var epoch = now / _ticksPerBucket;

            if (epoch != _epoch)
            {
                var stale = _epoch < 0 ? BucketCount : Math.Min(epoch - _epoch, BucketCount);

                for (long i = 0; i < stale; i++)
                {
                    var stalled = Index(epoch - i);
                    _calls[stalled] = 0;
                    _failures[stalled] = 0;
                }

                _epoch = epoch;
            }

            var index = Index(epoch);
            _calls[index]++;

            if (failure)
                _failures[index]++;
        }

        /// <summary>
        ///     The measured rate, or null while the window holds fewer than <paramref name="minimumSamples" />
        ///     outcomes - an error rate estimated from a handful of calls has a resolution coarser than
        ///     the floor it would be compared against.
        /// </summary>
        /// <param name="minimumSamples">How many outcomes the estimate needs.</param>
        /// <returns>The proportion of sampled outcomes that failed.</returns>
        public double? Ratio(int minimumSamples)
        {
            var calls = Sum(_calls);

            return calls >= minimumSamples ? (double)Sum(_failures) / calls : null;
        }
    }
}
