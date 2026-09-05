namespace NResilience.Extensions;

/// <summary>
///     The bindable shape of a policy: the policy's own scalars at the top level, and one section per
///     optional feature.
///     <para>
///         This type exists because binding a configuration section directly to <see cref="Resilience" />
///         is <b>silently partial</b>. Measured against
///         <c>Microsoft.Extensions.Configuration.Binder</c> 10.0.0 on both target frameworks: scalars and
///         <c>init</c> properties bind - <c>Attempts</c>, <c>Deadline</c> and every term of
///         <c>Backoff</c> among them - but <c>Classifier</c> is ignored (leaving a policy that does not
///         retry a 503), and <c>Breaker:ConsecutiveFailures</c> constructs a live circuit breaker with
///         default settings while ignoring the configured value.
///     </para>
///     <para>
///         Neither failure is a missing setter, and neither can be fixed by adding one: a classifier is a
///         set of predicates that no binder can conjure from a string, and a breaker is a live, stateful
///         guard that configuration should not be able to construct by accident. Partial binding leads to
///         unexpected behavior - a section that sets <c>Attempts</c> and silently drops <c>Classifier</c>
///         appears to work while the resulting policy does not retry what it was told to. To eliminate
///         this class of silent failures, the binding target is a DTO and
///         <see cref="ToPolicy(Resilience?)" /> performs the projection manually. Both failures are gated
///         by <c>Binding_onto_the_record_is_silently_partial</c>, and the binder's side of the line by
///         <c>The_binder_now_does_set_init_only_scalars</c>.
///     </para>
///     <para>
///         All properties are nullable. A null value indicates that the property should not be overridden,
///         preserving the value of the base policy.
///     </para>
///     <para>
///         A section is bound with <c>ErrorOnUnknownConfiguration</c>, so a key this type does not have
///         is an error rather than a no-op. That is the same argument one level out: a misspelled or
///         renamed key binds nothing, and a policy that quietly kept its defaults reads exactly like a
///         policy nobody configured. Gated by <c>A_key_the_dto_does_not_have_fails_at_resolution</c>,
///         and under Native AOT by the probe.
///     </para>
/// </summary>
/// <example>
///     <code language="json">
/// {
///   "Resilience": {
///     "api":     { "Preset": "Http", "Attempts": 3, "Deadline": "00:00:10", "AttemptTimeout": "00:00:03" },
///     "reports": { "Preset": "Http", "Attempts": 5, "Deadline": "00:05:00" }
///   }
/// }
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>Every feature is a section, and every section has an <c>Enabled</c>.</b>
///         <see cref="Backoff" />, <see cref="Budget" />, <see cref="AttemptCeiling" />, <see cref="Breaker" />
///         and <see cref="Hedge" /> are objects whose keys are the property names of the type each one
///         configures. Writing <c>"Enabled": false</c> in any of them turns that feature off, whatever
///         else the section says - which is the only way an <c>appsettings.Production.json</c> can
///         remove a feature a base file turned on, because configuration providers merge and never
///         delete a key.
///     </para>
///     <para>
///         <see cref="Resilience.Classifier" />, <see cref="Resilience.BeforeAttempt" /> and
///         <see cref="Resilience.OnEvent" /> are not bindable and are not represented here: a classifier
///         is a lambda and JSON cannot hold one. The <c>configure</c> callback on every registration is
///         where they go, and it runs after this projection so it always wins.
///     </para>
/// </remarks>
public sealed class ResilienceOptions
{
    /// <summary>
    ///     The starting point: <c>"None"</c>, <c>"Default"</c> or <c>"Http"</c>, matching the static
    ///     properties on <see cref="Resilience" />. Case-insensitive.
    ///     <para>
    ///         A discriminator the binder has no concept of, so <see cref="ToPolicy(Resilience?)" />
    ///         resolves it explicitly. Null means the base policy the registration supplied, or
    ///         <see cref="Resilience.Default" />.
    ///     </para>
    /// </summary>
    public string? Preset { get; set; }

    /// <summary>A name for the policy, used in diagnostics and in every telemetry tag. Defaults to the registration name.</summary>
    public string? Name { get; set; }

    /// <summary>
    ///     <see cref="Resilience.Attempts" /> - how many attempts to make. <c>1</c> means no retry;
    ///     <c>3</c> means try, then retry twice.
    /// </summary>
    public int? Attempts { get; set; }

    /// <summary><see cref="Resilience.Deadline" />. Use <c>"-00:00:00.0010000"</c> - <see cref="Timeout.InfiniteTimeSpan" /> - for no bound.</summary>
    public TimeSpan? Deadline { get; set; }

    /// <summary><see cref="Resilience.AttemptTimeout" />.</summary>
    public TimeSpan? AttemptTimeout { get; set; }

    /// <summary>
    ///     <see cref="Resilience.UseAmbientDeadline" /> - whether the deadline is clamped by the one the
    ///     current request inherited from its caller. Off by default.
    /// </summary>
    public bool? UseAmbientDeadline { get; set; }

    /// <summary>
    ///     <see cref="Resilience.Adaptive" /> - whether this policy measures the dependency and bounds
    ///     itself by what it measures. On by default; <c>"Adaptive": false</c> is the one key that turns
    ///     every measured term off and leaves only the constants this section wrote.
    ///     <para>
    ///         Unlike the property it sets, this one <i>does</i> reach the <see cref="Breaker" />, which
    ///         a section builds for this policy alone rather than sharing.
    ///         <c>
    ///             "Breaker": { "Adaptive":
    ///             true }
    ///         </c>
    ///         overrides it for the breaker only.
    ///     </para>
    /// </summary>
    public bool? Adaptive { get; set; }

    /// <summary>
    ///     The backoff curve. A section rather than flat properties, so its keys are the property names
    ///     of <see cref="NResilience.Backoff" /> itself. A policy always has a curve, so there is no
    ///     <c>Enabled</c> here; the section patches what the base policy carried.
    /// </summary>
    public BackoffOptions? Backoff { get; set; }

    /// <summary>
    ///     The retry budget. <c>"Budget": { "Enabled": false }</c> turns it off; every preset but
    ///     <see cref="Resilience.None" /> has one on.
    /// </summary>
    public BudgetOptions? Budget { get; set; }

    /// <summary>
    ///     The measured per-attempt ceiling, which <see cref="Resilience.AttemptCeiling" /> has on by default.
    ///     Every property has a working default, so this section is only needed to change one -
    ///     <c>"AttemptCeiling": { "Multiple": 5 }</c> - or to turn the feature off, which is
    ///     <c>"AttemptCeiling": { "Enabled": false }</c>.
    /// </summary>
    public AttemptCeilingOptions? AttemptCeiling { get; set; }

    /// <summary>
    ///     The circuit breaker. A breaker is a live object; this is the configuration one is built from.
    ///     Off unless the section is present, and <c>"Breaker": { "Enabled": false }</c> is how a later
    ///     configuration layer removes one an earlier layer asked for.
    /// </summary>
    public BreakerOptions? Breaker { get; set; }

    /// <summary>
    ///     Hedging, or null - the default - for none. Never on by default and never on in a preset, so
    ///     this section is the only way a registered policy hedges;
    ///     <c>"Hedge": { "Enabled": false }</c> takes it back off again.
    /// </summary>
    public HedgeOptions? Hedge { get; set; }

    /// <summary>
    ///     Whether the registered policy records to <see cref="ResilienceTelemetry" />. On by default,
    ///     which is the one place this library is not pay-for-play - see
    ///     <see cref="ResilienceTelemetry" /> for why registering a policy in a container is taken as
    ///     asking to be able to see it.
    /// </summary>
    public bool? Telemetry { get; set; }

    /// <summary>
    ///     Whether the registered policy writes log records: <c>"Off"</c>, <c>"Default"</c> or
    ///     <c>"Verbose"</c> (case-insensitive). At <c>"Default"</c>, the policy writes nothing above
    ///     <see cref="Microsoft.Extensions.Logging.LogLevel.Trace" /> while the dependency is healthy -
    ///     see <see cref="ResilienceLogging" />.
    ///     <para>
    ///         A string is used instead of an enum to ensure that typos produce a message naming the valid
    ///         values rather than a binder stack trace. A string is used instead of a <see cref="bool" /> because
    ///         logging has multiple levels; for instance, <c>"Logging": "Verbose"</c> provides the necessary
    ///         detail to diagnose why a call is not being retried.
    ///     </para>
    /// </summary>
    public string? Logging { get; set; }

    /// <summary>
    ///     Projects onto a policy. Applies <see cref="Preset" /> first when it is set, then every
    ///     property that is not null.
    /// </summary>
    /// <param name="baseline">
    ///     What to start from when <see cref="Preset" /> says nothing. Null means
    ///     <see cref="Resilience.Default" />.
    /// </param>
    /// <returns>The policy. Not validated - the caller validates, so a bad section fails at registration.</returns>
    /// <exception cref="ResilienceConfigurationException">
    ///     <see cref="Preset" /> names something that is not a preset, or a section uses a value that
    ///     used to be an off switch and is now spelled <c>"Enabled": false</c>.
    /// </exception>
    public Resilience ToPolicy(Resilience? baseline = null)
    {
        var policy = ResolvePreset() ?? baseline ?? Resilience.Default;

        if (Attempts is { } attempts)
            policy = policy with { Attempts = attempts };

        if (Deadline is { } deadline)
            policy = policy with { Deadline = deadline };

        if (AttemptTimeout is { } attemptTimeout)
            policy = policy with { AttemptTimeout = attemptTimeout };

        if (UseAmbientDeadline is { } ambient)
            policy = policy with { UseAmbientDeadline = ambient };

        if (Adaptive is { } adaptive)
            policy = policy with { Adaptive = adaptive };

        if (Name is { } name)
            policy = policy with { Name = name };

        if (Backoff is { } backoff)
            policy = policy with { Backoff = backoff.ToBackoff(policy.Backoff) };

        if (Budget is { } budget && budget.ToBudget(policy.Time) is { } resolved)
            policy = policy with { Budget = resolved };

        if (Breaker is { } breaker)
            policy = policy with { Breaker = breaker.Enabled is false ? null : breaker.ToBreaker(Name ?? policy.Name, policy.Time, Adaptive) };

        if (Hedge is { } hedge)
            policy = policy with { Hedge = hedge.Enabled is false ? null : hedge.ToHedge() };

        if (AttemptCeiling is { } ceiling)
            policy = policy with { AttemptCeiling = ceiling.Enabled is false ? null : ceiling.ToAttemptCeiling() };

        return policy;
    }

    private Resilience? ResolvePreset()
    {
        if (Preset is null)
            return null;

        return Preset.ToUpperInvariant() switch
        {
            "NONE" => Resilience.None,
            "DEFAULT" => Resilience.Default,
            "HTTP" => Resilience.Http,
            _ => throw new ResilienceConfigurationException(
                [$"Preset must be one of None, Default or Http; it is \"{Preset}\"."]),
        };
    }
}

/// <summary>
///     The one message every retired off switch produces, so a configuration file written against the
///     old spelling fails at registration naming the new one rather than failing later on a value that
///     no longer means what it used to.
/// </summary>
internal static class RetiredOffSwitch
{
    /// <summary>Builds the exception, naming the section, the key that used to mean "off", and what to write instead.</summary>
    /// <param name="section">The section the key lives in, as it is written in JSON.</param>
    /// <param name="key">The key that used to be the off switch.</param>
    /// <param name="reading">What the retired value would have to mean if it were taken literally.</param>
    /// <returns>The exception, for the caller to throw.</returns>
    internal static ResilienceConfigurationException For(string section, string key, string reading) =>
        new(
        [
            $"\"{section}\": {{ \"{key}\": 0 }} is no longer how a section turns {section} off; " +
            $"write \"{section}\": {{ \"Enabled\": false }} instead. {reading}",
        ]);
}

/// <summary>
///     The bindable shape of a <see cref="NResilience.Backoff" /> curve.
///     <para>
///         A section rather than five flat properties, so its keys are the property names of
///         <see cref="NResilience.Backoff" /> itself and there is no second spelling to learn. A policy
///         always has a curve, so there is no <c>Enabled</c>: the section patches the one the base
///         policy carried, and anything it does not mention keeps that policy's value.
///     </para>
/// </summary>
public sealed class BackoffOptions
{
    /// <summary>The first delay after a <see cref="VerdictKind.Transient" /> failure.</summary>
    public TimeSpan? TransientBase { get; set; }

    /// <summary>The first delay after a <see cref="VerdictKind.Throttled" /> failure, which starts higher because the dependency has said so.</summary>
    public TimeSpan? ThrottledBase { get; set; }

    /// <summary>The ceiling on any single backoff delay.</summary>
    public TimeSpan? MaximumDelay { get; set; }

    /// <summary>The multiplier applied per attempt. 2 doubles; 1 makes the backoff constant.</summary>
    public double? Factor { get; set; }

    /// <summary>How much of the computed delay is randomized. <see cref="NResilience.Jitter.Full" /> is the default and the right answer for almost everyone.</summary>
    public Jitter? Jitter { get; set; }

    /// <summary>
    ///     Measures <see cref="TransientBase" /> from the dependency's own recent latency instead of
    ///     taking it as the constant above. Off unless the section says so, which is
    ///     <c>"Backoff": { "MeasuredBase": { "Multiple": 1 } }</c>.
    /// </summary>
    public MeasuredBaseOptions? MeasuredBase { get; set; }

    /// <summary>Whether this section names anything that changes the shape of the curve rather than only its randomness.</summary>
    private bool HasCurve =>
        TransientBase is not null
        || ThrottledBase is not null
        || MaximumDelay is not null
        || Factor is not null

        // A measured base is only carried by an exponential curve, so naming one is a reason to
        // rebuild a Constant or Custom baseline into an exponential exactly as naming a knob is.
        || MeasuredBase is not null;

    /// <summary>Patches a curve with whatever this section named.</summary>
    /// <param name="baseline">The curve the base policy carried.</param>
    /// <returns>The curve, with every unmentioned knob left as it was.</returns>
    /// <remarks>
    ///     Patching only makes sense against an exponential curve, so a Constant or Custom baseline
    ///     whose section sets a knob gets a fresh exponential built on the shipped defaults, and that is
    ///     the documented behavior. Jitter on its own is a modifier rather than a reason to rebuild, so
    ///     a section naming only <see cref="Jitter" /> leaves a Constant curve constant.
    /// </remarks>
    internal Backoff ToBackoff(Backoff baseline)
    {
        if (!HasCurve)
            return Jitter is { } only ? baseline with { Jitter = only } : baseline;

        var existing = baseline.Kind == BackoffKind.Exponential ? baseline : Backoff.Default;

        return existing with
        {
            TransientBase = TransientBase ?? existing.TransientBase,
            ThrottledBase = ThrottledBase ?? existing.ThrottledBase,
            Factor = Factor ?? existing.Factor,
            MaximumDelay = MaximumDelay ?? existing.MaximumDelay,
            Jitter = Jitter ?? baseline.Jitter,
            MeasuredBase = MeasuredBase is { } measured
                ? measured.Enabled is false ? null : measured.ToMeasuredBase()
                : existing.MeasuredBase,
        };
    }
}

/// <summary>
///     The bindable shape of a <see cref="NResilience.MeasuredBase" />.
/// </summary>
/// <remarks>
///     Opt-in, unlike <see cref="AttemptCeilingOptions" />: a measured base can lengthen a delay as well
///     as shorten one, so it is not something the library turns on for a policy that did not ask. See
///     <see cref="NResilience.MeasuredBase" /> for the argument.
/// </remarks>
public sealed class MeasuredBaseOptions
{
    /// <summary>
    ///     Whether the backoff base is measured at all. <c>false</c> drops a measured base the base
    ///     policy carried, whatever else this section says.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     How many normal calls the first retry waits. Defaults to 1, and must be greater than zero -
    ///     <c>"Enabled": false</c> is how a section turns the measurement off.
    /// </summary>
    public double? Multiple { get; set; }

    /// <summary><see cref="NResilience.MeasuredBase.Quantile" />.</summary>
    public double? Quantile { get; set; }

    /// <summary><see cref="NResilience.MeasuredBase.Window" />.</summary>
    public TimeSpan? Window { get; set; }

    /// <summary><see cref="NResilience.MeasuredBase.MinimumSamples" />.</summary>
    public int? MinimumSamples { get; set; }

    /// <summary><see cref="NResilience.MeasuredBase.Spread" />.</summary>
    public double? Spread { get; set; }

    /// <summary>Converts the options to a <see cref="NResilience.MeasuredBase" /> value, using defaults for any unset properties.</summary>
    /// <returns>The configuration.</returns>
    /// <exception cref="ResilienceConfigurationException"><see cref="Multiple" /> is zero, which is the shape an off switch would take.</exception>
    internal MeasuredBase ToMeasuredBase()
    {
        if (Multiple is 0)
        {
            throw RetiredOffSwitch.For(
                "Backoff.MeasuredBase",
                nameof(Multiple),
                "Zero normal calls is not a delay anyone could mean.");
        }

        var measured = MeasuredBase.Times(Multiple ?? MeasuredBase.DefaultMultiple);

        if (Quantile is { } quantile)
            measured = measured with { Quantile = quantile };

        if (Window is { } window)
            measured = measured with { Window = window };

        if (MinimumSamples is { } samples)
            measured = measured with { MinimumSamples = samples };

        if (Spread is { } spread)
            measured = measured with { Spread = spread };

        return measured;
    }
}

/// <summary>
///     The bindable shape of a <see cref="RetryBudget" />.
///     <para>
///         A section rather than three flat properties, so the budget is turned off the way every other
///         feature is - <c>"Budget": { "Enabled": false }</c> - rather than by a zero fraction that a
///         reader has to know is special.
///     </para>
/// </summary>
public sealed class BudgetOptions
{
    /// <summary>The fraction a budget gets when the section named none.</summary>
    private const double DefaultFraction = 0.1;

    /// <summary>The floor a budget gets when the section named none.</summary>
    private const int DefaultMinimumPerSecond = 3;

    /// <summary>
    ///     Whether this policy has a retry budget at all. <c>false</c> is
    ///     <see cref="RetryBudget.None" />, whatever else this section says; <c>true</c> turns one on at
    ///     the defaults; null leaves the base policy's budget alone unless another property here names
    ///     one.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     The budget as a fraction of successful traffic: <c>0.1</c> means retries may add at most 10%
    ///     on top.
    /// </summary>
    public double? Fraction { get; set; }

    /// <summary>The floor, in retries per second, below which the fraction does not apply - so a quiet service can still retry at all.</summary>
    public int? MinimumPerSecond { get; set; }

    /// <summary>
    ///     Names a <see cref="RetryBudget.Shared(string, double, int)" /> budget, so several policies
    ///     throttle against one pool. Null - the default - gives this policy its own, which is the
    ///     blast-radius decision the library argues for.
    /// </summary>
    public string? Shared { get; set; }

    /// <summary>Builds the budget this section describes.</summary>
    /// <param name="time">
    ///     The policy's clock, which a private budget adopts. A shared budget does not: it is
    ///     process-wide and the first caller's parameters win, so a clock from one section would
    ///     silently apply to every policy naming the same string. Null means
    ///     <see cref="TimeProvider.System" />.
    /// </param>
    /// <returns>The budget, or null when the section named nothing and the base policy's should stand.</returns>
    /// <exception cref="ResilienceConfigurationException"><see cref="Fraction" /> is zero, which used to be the off switch.</exception>
    internal RetryBudget? ToBudget(TimeProvider? time = null)
    {
        if (Enabled is false)
            return RetryBudget.None;

        if (Fraction is 0)
        {
            throw RetiredOffSwitch.For(
                "Budget",
                nameof(Fraction),
                "A budget that lets retries add nothing on top of success is not a budget anyone can spend from.");
        }

        if (Shared is { } shared)
            return RetryBudget.Shared(shared, Fraction ?? DefaultFraction, MinimumPerSecond ?? DefaultMinimumPerSecond);

        if (Enabled is null && Fraction is null && MinimumPerSecond is null)
            return null;

        return RetryBudget.Of(Fraction ?? DefaultFraction, MinimumPerSecond ?? DefaultMinimumPerSecond, time ?? TimeProvider.System);
    }
}

/// <summary>
///     The bindable shape of a <see cref="NResilience.Hedge" />.
///     <para>
///         A section of its own rather than five flat properties, so that the presence of the section is
///         what turns hedging on. There is deliberately no fixed-delay setting here, for the reason
///         <see cref="NResilience.Hedge" /> gives: a constant threshold is the failure mode the adaptive
///         one exists to avoid, and it would be one JSON key away if it existed at all.
///     </para>
/// </summary>
public sealed class HedgeOptions
{
    /// <summary>
    ///     Whether this policy hedges. Hedging is off until a section asks for it, so this is only
    ///     needed to take it back off - <c>"Hedge": { "Enabled": false }</c> in a configuration layer
    ///     over one that turned it on.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     <see cref="NResilience.Hedge.Quantile" />. Defaults to 0.95, so <c>"Hedge": {}</c> is a
    ///     complete configuration.
    /// </summary>
    public double? Quantile { get; set; }

    /// <summary><see cref="NResilience.Hedge.MaximumConcurrent" />.</summary>
    public int? MaximumConcurrent { get; set; }

    /// <summary><see cref="NResilience.Hedge.MinimumSamples" />.</summary>
    public int? MinimumSamples { get; set; }

    /// <summary><see cref="NResilience.Hedge.MinimumDelay" />.</summary>
    public TimeSpan? MinimumDelay { get; set; }

    /// <summary><see cref="NResilience.Hedge.Window" />.</summary>
    public TimeSpan? Window { get; set; }

    /// <summary>
    ///     <see cref="NResilience.Hedge.SuppressAt" />. Defaults to 0.5. Unlike the off switches this
    ///     type retired, this is a real point on a real scale: <c>1</c> is "suppress only at the trip
    ///     point itself", which is the top of the range rather than a magic value.
    /// </summary>
    public double? SuppressAt { get; set; }

    /// <summary>
    ///     <see cref="NResilience.Hedge.WinRate" />. Off unless the section says so, which is
    ///     <c>"Hedge": { "WinRate": { "Floor": 0.2 } }</c>.
    /// </summary>
    public WinRateOptions? WinRate { get; set; }

    /// <summary>Projects onto the value the policy carries. Every unset property keeps its own default.</summary>
    /// <returns>The configuration.</returns>
    internal Hedge ToHedge()
    {
        var hedge = Hedge.At(Quantile ?? 0.95);

        if (MaximumConcurrent is { } concurrent)
            hedge = hedge with { MaximumConcurrent = concurrent };

        if (MinimumSamples is { } samples)
            hedge = hedge with { MinimumSamples = samples };

        if (MinimumDelay is { } delay)
            hedge = hedge with { MinimumDelay = delay };

        if (Window is { } window)
            hedge = hedge with { Window = window };

        if (SuppressAt is { } suppressAt)
            hedge = hedge with { SuppressAt = suppressAt };

        if (WinRate is { } feedback)
            hedge = hedge with { WinRate = feedback.ToWinRate() };

        return hedge;
    }
}

/// <summary>
///     The bindable shape of a <see cref="NResilience.WinRate" />.
/// </summary>
/// <remarks>
///     Opt-in, like <see cref="MeasuredBaseOptions" /> and unlike the rest of
///     <see cref="HedgeOptions" />: it is a control loop over a control loop, and its failure mode - the
///     dependency whose tail no second attempt can route around is exactly the one it retreats from - is
///     not something the library decides on a caller's behalf. See <see cref="NResilience.WinRate" />.
/// </remarks>
public sealed class WinRateOptions
{
    /// <summary>
    ///     Whether hedges are held back when they stop winning. <c>false</c> drops a loop the base policy
    ///     carried, whatever else this section says.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     The fraction of hedges that has to win. Defaults to 0.2, and must be in <c>(0, 1)</c> -
    ///     <c>"Enabled": false</c> is how a section turns the feedback off.
    /// </summary>
    public double? Minimum { get; set; }

    /// <summary><see cref="NResilience.WinRate.Window" />.</summary>
    public TimeSpan? Window { get; set; }

    /// <summary><see cref="NResilience.WinRate.MinimumSamples" />.</summary>
    public int? MinimumSamples { get; set; }

    /// <summary><see cref="NResilience.WinRate.MinimumAllowance" />.</summary>
    public double? MinimumAllowance { get; set; }

    /// <summary>Projects onto the value the policy carries, or null when the section turned it off.</summary>
    /// <returns>The configuration, or null.</returns>
    /// <exception cref="ResilienceConfigurationException"><see cref="Minimum" /> is zero, which is the shape an off switch would take.</exception>
    internal WinRate? ToWinRate()
    {
        if (Enabled is false)
            return null;

        if (Minimum is 0)
        {
            throw RetiredOffSwitch.For(
                "Hedge.WinRate",
                nameof(Minimum),
                "A win rate no hedge can fall below is feedback that never acts.");
        }

        var feedback = WinRate.AtLeast(Minimum ?? WinRate.DefaultMinimum);

        if (Window is { } window)
            feedback = feedback with { Window = window };

        if (MinimumSamples is { } samples)
            feedback = feedback with { MinimumSamples = samples };

        if (MinimumAllowance is { } allowance)
            feedback = feedback with { MinimumAllowance = allowance };

        return feedback;
    }
}

/// <summary>
///     The bindable shape of an <see cref="NResilience.AttemptCeiling" />.
/// </summary>
/// <remarks>
///     There is no way to make the measured ceiling longer than <see cref="ResilienceOptions.AttemptTimeout" />.
///     This ensures the feature cannot unexpectedly increase the attempt duration.
/// </remarks>
public sealed class AttemptCeilingOptions
{
    /// <summary>
    ///     Whether the measured ceiling bounds attempts. On by default, so this is only needed to turn
    ///     it off - <c>"AttemptCeiling": { "Enabled": false }</c> leaves
    ///     <see cref="ResilienceOptions.AttemptTimeout" /> as the only per-attempt bound.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     The multiple of the measured quantile. Defaults to 3, and must be greater than 1 -
    ///     <c>"Enabled": false</c> is how a section turns the ceiling off.
    /// </summary>
    public double? Multiple { get; set; }

    /// <summary><see cref="NResilience.AttemptCeiling.Quantile" />.</summary>
    public double? Quantile { get; set; }

    /// <summary><see cref="NResilience.AttemptCeiling.Window" />.</summary>
    public TimeSpan? Window { get; set; }

    /// <summary><see cref="NResilience.AttemptCeiling.MinimumSamples" />.</summary>
    public int? MinimumSamples { get; set; }

    /// <summary><see cref="NResilience.AttemptCeiling.Floor" />.</summary>
    public TimeSpan? Floor { get; set; }

    /// <summary>Converts the options to an <see cref="NResilience.AttemptCeiling" /> value, using defaults for any unset properties.</summary>
    /// <returns>The configuration.</returns>
    /// <exception cref="ResilienceConfigurationException"><see cref="Multiple" /> is zero, which used to be the off switch.</exception>
    internal AttemptCeiling ToAttemptCeiling()
    {
        if (Multiple is 0)
        {
            throw RetiredOffSwitch.For(
                "AttemptCeiling",
                nameof(Multiple),
                "Zero times the recent p95 is not a ceiling anyone could mean.");
        }

        var ceiling = AttemptCeiling.Above(Multiple ?? 3.0);

        if (Quantile is { } quantile)
            ceiling = ceiling with { Quantile = quantile };

        if (Window is { } window)
            ceiling = ceiling with { Window = window };

        if (MinimumSamples is { } samples)
            ceiling = ceiling with { MinimumSamples = samples };

        if (Floor is { } floor)
            ceiling = ceiling with { Floor = floor };

        return ceiling;
    }
}

/// <summary>
///     The bindable shape of a <see cref="NResilience.BreakerSettings" />, for the same reason
///     <see cref="ResilienceOptions" /> exists: binding a section onto the settings record itself is
///     silently partial, and worse, binding one onto <see cref="Resilience.Breaker" /> constructs a live
///     breaker with default settings while ignoring what the section said.
/// </summary>
/// <remarks>
///     A configured breaker is created per policy and lives as long as the policy does. It is
///     deliberately <b>not</b> recreated when configuration reloads - its state is the point, and
///     discarding an open breaker because somebody edited a JSON file would hand the traffic straight
///     back to the dependency that is down. See <see cref="IResiliencePolicies" />.
/// </remarks>
public sealed class BreakerOptions
{
    /// <summary>
    ///     Whether this policy has a breaker. Breaking is off until a section asks for it, so this is
    ///     only needed to take it back off - <c>"Breaker": { "Enabled": false }</c> in a configuration
    ///     layer over one that turned it on. It is the only way to do that, because configuration
    ///     providers merge sections and never remove a key.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     <see cref="BreakerSettings.Adaptive" /> - whether this breaker measures the dependency and
    ///     trips on what it measures. On by default. Inherited from
    ///     <see cref="ResilienceOptions.Adaptive" /> when this section says nothing, so one
    ///     <c>"Adaptive": false</c> at the top of a policy covers the breaker too.
    /// </summary>
    public bool? Adaptive { get; set; }

    /// <summary><see cref="BreakerSettings.ConsecutiveFailures" />.</summary>
    public int? ConsecutiveFailures { get; set; }

    /// <summary><see cref="BreakerSettings.FailureRatio" /> - the rate-based trip, for a service with enough traffic to have a rate.</summary>
    public double? FailureRatio { get; set; }

    /// <summary>
    ///     <see cref="BreakerSettings.Failures" /> - the same rate-based trip stated as a multiple of the
    ///     dependency's own measured error rate instead of an absolute ratio, which the breaker has on
    ///     by default. A section of its own, so it is only needed to change a property -
    ///     <c>"Failures": { "Multiple": 10 }</c> - or to turn the trip off, which is
    ///     <c>"Failures": { "Enabled": false }</c>. Composes with <see cref="FailureRatio" />, which
    ///     stays the ceiling when both are set.
    /// </summary>
    public FailuresOptions? Failures { get; set; }

    /// <summary><see cref="BreakerSettings.MinimumCalls" /> - the sample below which the ratio means nothing.</summary>
    public int? MinimumCalls { get; set; }

    /// <summary>
    ///     <see cref="BreakerSettings.TripWindow" /> - the window the trip ratios are measured over.
    ///     Distinct from the <c>Window</c> inside <see cref="Failures" /> and <see cref="SlowCalls" />,
    ///     which are the baseline windows those trips measure "normal" over.
    /// </summary>
    public TimeSpan? TripWindow { get; set; }

    /// <summary><see cref="BreakerSettings.BreakDuration" />.</summary>
    public TimeSpan? BreakDuration { get; set; }

    /// <summary><see cref="BreakerSettings.MaximumBreakDuration" />.</summary>
    public TimeSpan? MaximumBreakDuration { get; set; }

    /// <summary>
    ///     <see cref="BreakerSettings.BreakJitter" /> - how much randomness the break duration carries,
    ///     so a fleet that opened together does not probe together. <c>"Equal"</c> by default.
    /// </summary>
    public Jitter? BreakJitter { get; set; }

    /// <summary>
    ///     <see cref="BreakerSettings.Recovery" /> - hand the traffic back over a ramp rather than a
    ///     cliff. A section of its own, so <c>"Recovery": {}</c> turns it on at its defaults,
    ///     <c>"Recovery": { "Length": 0.5 }</c> changes the one number, and
    ///     <c>"Recovery": { "Enabled": false }</c> turns it back off.
    /// </summary>
    public RecoveryOptions? Recovery { get; set; }

    /// <summary><see cref="BreakerSettings.HalfOpenProbes" />.</summary>
    public int? HalfOpenProbes { get; set; }

    /// <summary><see cref="BreakerSettings.ProbeSuccesses" />.</summary>
    public int? ProbeSuccesses { get; set; }

    /// <summary>
    ///     <see cref="BreakerSettings.SlowCallThreshold" /> - a call slower than this counts as a failure, because a dependency that has stopped answering in
    ///     time has failed.
    /// </summary>
    public TimeSpan? SlowCallThreshold { get; set; }

    /// <summary>
    ///     <see cref="BreakerSettings.SlowCalls" /> - the same brownout trip stated as a multiple of
    ///     measured normal latency instead of a constant, which the breaker has on by default. A section
    ///     of its own, so it is only needed to change a property - <c>"SlowCalls": { "Multiple": 5 }</c>
    ///     - or to turn the trip off, which is <c>"SlowCalls": { "Enabled": false }</c>. Setting
    ///     <see cref="SlowCallThreshold" /> as well composes rather than colliding: an attempt is slow
    ///     when it is above either threshold.
    /// </summary>
    public SlowCallsOptions? SlowCalls { get; set; }

    /// <summary><see cref="BreakerSettings.SlowCallRatio" />.</summary>
    public double? SlowCallRatio { get; set; }

    /// <summary>Builds the live breaker.</summary>
    /// <param name="name">The name it reports itself under. Usually the policy's.</param>
    /// <param name="time">
    ///     The clock, usually the policy's. Null means <see cref="TimeProvider.System" />. A section
    ///     cannot name a clock, so the breaker a section describes runs on whatever clock the policy
    ///     it is attached to runs on, and one <see cref="Resilience.Time" /> drives both.
    /// </param>
    /// <param name="adaptive">
    ///     The policy's <see cref="ResilienceOptions.Adaptive" />, used when this section names none of
    ///     its own. Null leaves <see cref="BreakerSettings.Adaptive" /> at its default.
    /// </param>
    /// <returns>A new breaker, closed.</returns>
    /// <remarks>
    ///     <see cref="Enabled" /> is not consulted here - a method that builds a breaker cannot return
    ///     "no breaker". <see cref="ResilienceOptions.ToPolicy(Resilience?)" /> checks it before calling
    ///     this.
    /// </remarks>
    internal Breaker ToBreaker(string? name = null, TimeProvider? time = null, bool? adaptive = null)
    {
        var settings = time is null ? new BreakerSettings() : new BreakerSettings { Time = time };

        // This section's own answer wins; the policy's is the fallback, because a section that says
        // nothing about measurement should follow the policy it belongs to.
        if ((Adaptive ?? adaptive) is { } measures)
            settings = settings with { Adaptive = measures };

        if (ConsecutiveFailures is { } consecutive)
            settings = settings with { ConsecutiveFailures = consecutive };

        if (FailureRatio is { } ratio)
            settings = settings with { FailureRatio = ratio };

        if (Failures is { } relative)
            settings = settings with { Failures = relative.Enabled is false ? null : relative.ToFailures() };

        if (MinimumCalls is { } minimum)
            settings = settings with { MinimumCalls = minimum };

        if (TripWindow is { } tripWindow)
            settings = settings with { TripWindow = tripWindow };

        if (BreakDuration is { } breakDuration)
            settings = settings with { BreakDuration = breakDuration };

        if (MaximumBreakDuration is { } maximumBreak)
            settings = settings with { MaximumBreakDuration = maximumBreak };

        if (BreakJitter is { } jitter)
            settings = settings with { BreakJitter = jitter };

        if (Recovery is { } ramp)
            settings = settings with { Recovery = ramp.Enabled is false ? null : ramp.ToRecovery() };

        if (HalfOpenProbes is { } probes)
            settings = settings with { HalfOpenProbes = probes };

        if (ProbeSuccesses is { } successes)
            settings = settings with { ProbeSuccesses = successes };

        if (SlowCallThreshold is { } slow)
            settings = settings with { SlowCallThreshold = slow };

        if (SlowCalls is { } slowCalls)
            settings = settings with { SlowCalls = slowCalls.Enabled is false ? null : slowCalls.ToSlowCalls() };

        if (SlowCallRatio is { } slowRatio)
            settings = settings with { SlowCallRatio = slowRatio };

        return new Breaker(settings with { Name = name });
    }
}

/// <summary>
///     The bindable shape of a <see cref="NResilience.Failures" />.
///     <para>
///         A section rather than four flat properties, so <c>"Failures": { "Multiple": 5 }</c> is a
///         complete configuration, and <c>"Enabled": false</c> is how a section turns the trip off.
///     </para>
/// </summary>
public sealed class FailuresOptions
{
    /// <summary>
    ///     Whether the relative failure trip is armed. On by default, so this is only needed to turn it
    ///     off.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     <see cref="NResilience.Failures.Multiple" />. Defaults to 5, so <c>"Failures": {}</c> is a
    ///     complete configuration, and must be greater than 1 - <c>"Enabled": false</c> is how a section
    ///     turns the trip off.
    /// </summary>
    public double? Multiple { get; set; }

    /// <summary><see cref="NResilience.Failures.Window" />.</summary>
    public TimeSpan? Window { get; set; }

    /// <summary><see cref="NResilience.Failures.MinimumSamples" />.</summary>
    public int? MinimumSamples { get; set; }

    /// <summary><see cref="NResilience.Failures.Floor" />.</summary>
    public double? Floor { get; set; }

    /// <summary>Projects onto the value the breaker carries. Every unset property keeps its own default.</summary>
    /// <returns>The configuration.</returns>
    /// <exception cref="ResilienceConfigurationException"><see cref="Multiple" /> is zero, which used to be the off switch.</exception>
    internal Failures ToFailures()
    {
        if (Multiple is 0)
        {
            throw RetiredOffSwitch.For(
                "Failures",
                nameof(Multiple),
                "Zero times the recent error rate is not a trip point anyone could mean.");
        }

        var failures = Failures.Above(Multiple ?? 5.0);

        if (Window is { } window)
            failures = failures with { Window = window };

        if (MinimumSamples is { } samples)
            failures = failures with { MinimumSamples = samples };

        if (Floor is { } floor)
            failures = failures with { Floor = floor };

        return failures;
    }
}

/// <summary>
///     The bindable shape of a <see cref="NResilience.Recovery" />.
///     <para>
///         A section rather than flat properties, so <c>"Recovery": {}</c> is a complete configuration
///         and <c>"Enabled": false</c> is how a section that is present turns the ramp off again. The
///         keys are <see cref="NResilience.Recovery" />'s own property names.
///     </para>
/// </summary>
public sealed class RecoveryOptions
{
    /// <summary>
    ///     Whether the traffic comes back over a ramp. The ramp is off until a section asks for it, so
    ///     this is only needed to take it back off.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     <see cref="NResilience.Recovery.Length" /> - how long the ramp lasts, as a fraction of the
    ///     break just served. Defaults to 0.25, and must be above zero - <c>"Enabled": false</c> is how
    ///     a section turns the ramp off.
    /// </summary>
    public double? Length { get; set; }

    /// <summary><see cref="NResilience.Recovery.MinimumLength" />.</summary>
    public TimeSpan? MinimumLength { get; set; }

    /// <summary><see cref="NResilience.Recovery.MaximumLength" />.</summary>
    public TimeSpan? MaximumLength { get; set; }

    /// <summary><see cref="NResilience.Recovery.InitialFraction" /> - the fraction of calls the ramp starts by admitting.</summary>
    public double? InitialFraction { get; set; }

    /// <summary>Projects onto the value the breaker carries. Every unset property keeps its own default.</summary>
    /// <returns>The configuration.</returns>
    /// <exception cref="ResilienceConfigurationException"><see cref="Length" /> is zero, which used to be the off switch.</exception>
    internal Recovery ToRecovery()
    {
        if (Length is 0)
        {
            throw RetiredOffSwitch.For(
                "Recovery",
                nameof(Length),
                "A ramp lasting none of the break is not a ramp anyone could mean.");
        }

        var recovery = Recovery.Over(Length ?? 0.25);

        if (MinimumLength is { } minimum)
            recovery = recovery with { MinimumLength = minimum };

        if (MaximumLength is { } maximum)
            recovery = recovery with { MaximumLength = maximum };

        if (InitialFraction is { } initial)
            recovery = recovery with { InitialFraction = initial };

        return recovery;
    }
}

/// <summary>
///     The bindable shape of a <see cref="NResilience.SlowCalls" />.
///     <para>
///         A section rather than four flat properties, so <c>"SlowCalls": { "Multiple": 3 }</c> is a
///         complete configuration, and <c>"Enabled": false</c> is how a section turns the trip off.
///     </para>
/// </summary>
public sealed class SlowCallsOptions
{
    /// <summary>
    ///     Whether the brownout trip is armed. On by default, so this is only needed to turn it off.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     <see cref="NResilience.SlowCalls.Multiple" />. Defaults to 3, so <c>"SlowCalls": {}</c> is a
    ///     complete configuration, and must be greater than 1 - <c>"Enabled": false</c> is how a section
    ///     turns the trip off.
    /// </summary>
    public double? Multiple { get; set; }

    /// <summary><see cref="NResilience.SlowCalls.Quantile" />.</summary>
    public double? Quantile { get; set; }

    /// <summary><see cref="NResilience.SlowCalls.Window" />.</summary>
    public TimeSpan? Window { get; set; }

    /// <summary><see cref="NResilience.SlowCalls.MinimumSamples" />.</summary>
    public int? MinimumSamples { get; set; }

    /// <summary>Projects onto the value the breaker carries. Every unset property keeps its own default.</summary>
    /// <returns>The configuration.</returns>
    /// <exception cref="ResilienceConfigurationException"><see cref="Multiple" /> is zero, which used to be the off switch.</exception>
    internal SlowCalls ToSlowCalls()
    {
        if (Multiple is 0)
        {
            throw RetiredOffSwitch.For(
                "SlowCalls",
                nameof(Multiple),
                "Zero times normal latency is not a threshold anyone could mean.");
        }

        var slow = SlowCalls.Above(Multiple ?? 3.0);

        if (Quantile is { } quantile)
            slow = slow with { Quantile = quantile };

        if (Window is { } window)
            slow = slow with { Window = window };

        if (MinimumSamples is { } samples)
            slow = slow with { MinimumSamples = samples };

        return slow;
    }
}
