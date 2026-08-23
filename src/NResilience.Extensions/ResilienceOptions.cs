namespace NResilience.Extensions;

/// <summary>
/// The bindable shape of a policy: flat, mutable, and made of primitives, <see cref="TimeSpan"/>s
/// and enums.
/// <para>
/// This type exists because binding a configuration section directly to <see cref="Resilience"/>
/// is <b>silently partial</b>. Measured against
/// <c>Microsoft.Extensions.Configuration.Binder</c> 10.0.0 on both target frameworks: simple
/// <c>init</c> scalars such as <c>Attempts</c> and <c>Deadline</c> bind, but <c>Backoff:Max</c> is
/// dropped because the cap is a computed property, <c>Classify</c> is ignored (leaving a policy
/// that does not retry a 503), and <c>Breaker:ConsecutiveFailures</c> constructs a live circuit
/// breaker with default settings while ignoring the configured value.
/// </para>
/// <para>
/// Partial binding can lead to unexpected behavior; for example, if <c>Backoff:Jitter</c> is
/// applied but <c>Backoff:Max</c> is ignored, the configuration appears to work while the
/// resulting policy is incorrect. To eliminate this class of silent failures, the binding
/// target is a DTO and <see cref="ToPolicy(Resilience?)"/> performs the projection manually.
/// All three failures are gated by <c>Binding_onto_the_record_is_silently_partial</c>.
/// </para>
/// <para>
/// All properties are nullable. A null value indicates that the property should not be overridden,
/// preserving the value of the base policy.
/// </para>
/// </summary>
/// <example>
/// <code language="json">
/// {
///   "Resilience": {
///     "api":     { "Preset": "Http", "Attempts": 3, "Deadline": "00:00:10", "AttemptTimeout": "00:00:03" },
///     "reports": { "Preset": "Http", "Attempts": 5, "Deadline": "00:05:00" }
///   }
/// }
/// </code>
/// </example>
/// <remarks>
/// <see cref="Resilience.Classify"/>, <see cref="Resilience.BeforeAttempt"/> and
/// <see cref="Resilience.OnEvent"/> are not bindable and are not represented here: a classifier is
/// a lambda and JSON cannot hold one. The <c>configure</c> callback on every registration is where
/// they go, and it runs after this projection so it always wins.
/// </remarks>
public sealed class ResilienceOptions
{
    /// <summary>
    /// The starting point: <c>"None"</c>, <c>"Default"</c> or <c>"Http"</c>, matching the static
    /// properties on <see cref="Resilience"/>. Case-insensitive.
    /// <para>
    /// A discriminator the binder has no concept of, so <see cref="ToPolicy(Resilience?)"/>
    /// resolves it explicitly. Null means the base policy the registration supplied, or
    /// <see cref="Resilience.Default"/>.
    /// </para>
    /// </summary>
    public string? Preset { get; set; }

    /// <summary>A name for the policy, used in diagnostics and in every telemetry tag. Defaults to the registration name.</summary>
    public string? Name { get; set; }

    /// <summary><see cref="Resilience.Attempts"/> - TOTAL attempts including the first.</summary>
    public int? Attempts { get; set; }

    /// <summary><see cref="Resilience.Deadline"/>. Use <c>"-00:00:00.0010000"</c> - <see cref="Timeout.InfiniteTimeSpan"/> - for no bound.</summary>
    public TimeSpan? Deadline { get; set; }

    /// <summary><see cref="Resilience.AttemptTimeout"/>.</summary>
    public TimeSpan? AttemptTimeout { get; set; }

    /// <summary>The first delay after a <see cref="VerdictKind.Transient"/> failure.</summary>
    public TimeSpan? TransientBaseDelay { get; set; }

    /// <summary>The first delay after a <see cref="VerdictKind.Throttled"/> failure, which starts higher because the dependency has said so.</summary>
    public TimeSpan? ThrottledBaseDelay { get; set; }

    /// <summary>The ceiling on any single backoff delay.</summary>
    public TimeSpan? MaxDelay { get; set; }

    /// <summary>The multiplier applied per attempt. 2 doubles; 1 makes the backoff constant.</summary>
    public double? BackoffFactor { get; set; }

    /// <summary>How much of the computed delay is randomized. <see cref="NResilience.Jitter.Full"/> is the default and the right answer for almost everyone.</summary>
    public Jitter? Jitter { get; set; }

    /// <summary>
    /// The retry budget as a fraction of successful traffic: <c>0.1</c> means retries may add at
    /// most 10% on top. Zero turns the budget off.
    /// </summary>
    public double? BudgetFraction { get; set; }

    /// <summary>The floor, in retries per second, below which the fraction does not apply - so a quiet service can still retry at all.</summary>
    public int? BudgetMinimumPerSecond { get; set; }

    /// <summary>
    /// Names a <see cref="RetryBudget.Shared(string, double, int)"/> budget, so several policies
    /// throttle against one pool. Null - the default - gives this policy its own, which is the
    /// blast-radius decision the design argues for.
    /// </summary>
    public string? SharedBudget { get; set; }

    /// <summary>The circuit breaker, or null for no breaking. A breaker is a live object; this is the configuration one is built from.</summary>
    public BreakerOptions? Breaker { get; set; }

    /// <summary>
    /// Whether the registered policy records to <see cref="ResilienceTelemetry"/>. On by default,
    /// which is the one place this library is not pay-for-play - see
    /// <see cref="ResilienceTelemetry"/> for why registering a policy in a container is taken as
    /// asking to be able to see it.
    /// </summary>
    public bool? Telemetry { get; set; }

    /// <summary>
    /// Whether the registered policy writes log records: <c>"Off"</c>, <c>"Default"</c> or
    /// <c>"Verbose"</c> (case-insensitive). At <c>"Default"</c>, the policy writes nothing above
    /// <see cref="Microsoft.Extensions.Logging.LogLevel.Trace"/> while the dependency is healthy -
    /// see <see cref="ResilienceLogging"/>.
    /// <para>
    /// A string is used instead of an enum to ensure that typos produce a message naming the valid
    /// values rather than a binder stack trace. A string is used instead of a <see cref="bool"/> because
    /// logging has multiple levels; for instance, <c>"Logging": "Verbose"</c> provides the necessary
    /// detail to diagnose why a call is not being retried.
    /// </para>
    /// </summary>
    public string? Logging { get; set; }

    /// <summary>
    /// Projects onto a policy. Applies <see cref="Preset"/> first when it is set, then every
    /// property that is not null.
    /// </summary>
    /// <param name="baseline">
    /// What to start from when <see cref="Preset"/> says nothing. Null means
    /// <see cref="Resilience.Default"/>.
    /// </param>
    /// <returns>The policy. Not validated - the caller validates, so a bad section fails at registration.</returns>
    /// <exception cref="ResilienceConfigurationException"><see cref="Preset"/> names something that is not a preset.</exception>
    public Resilience ToPolicy(Resilience? baseline = null)
    {
        Resilience policy = ResolvePreset() ?? baseline ?? Resilience.Default;

        if (Attempts is { } attempts)
        {
            policy = policy with { Attempts = attempts };
        }

        if (Deadline is { } deadline)
        {
            policy = policy with { Deadline = deadline };
        }

        if (AttemptTimeout is { } attemptTimeout)
        {
            policy = policy with { AttemptTimeout = attemptTimeout };
        }

        if (Name is { } name)
        {
            policy = policy with { Name = name };
        }

        if (HasBackoff)
        {
            // Rebuilt rather than patched: Backoff's base delays are not readable off the struct,
            // because the shipped shape is a factory rather than a bag of knobs. Anything the
            // section did not mention falls back to the factory's own default, which is the same
            // value Backoff.Default carries.
            policy = policy with
            {
                Backoff = Backoff.Exponential(
                    TransientBaseDelay,
                    ThrottledBaseDelay,
                    BackoffFactor ?? 2.0,
                    MaxDelay) with
                { Jitter = Jitter ?? policy.Backoff.Jitter },
            };
        }
        else if (Jitter is { } jitter)
        {
            policy = policy with { Backoff = policy.Backoff with { Jitter = jitter } };
        }

        if (BuildBudget() is { } budget)
        {
            policy = policy with { Budget = budget };
        }

        if (Breaker is { } breaker)
        {
            policy = policy with { Breaker = breaker.ToBreaker(Name ?? policy.Name) };
        }

        return policy;
    }

    private bool HasBackoff =>
        TransientBaseDelay is not null
        || ThrottledBaseDelay is not null
        || MaxDelay is not null
        || BackoffFactor is not null;

    private RetryBudget? BuildBudget()
    {
        if (SharedBudget is { } shared)
        {
            return RetryBudget.Shared(shared, BudgetFraction ?? 0.1, BudgetMinimumPerSecond ?? 3);
        }

        if (BudgetFraction is null && BudgetMinimumPerSecond is null)
        {
            return null;
        }

        // Zero is the off switch rather than a fraction to validate: "retries may add at most 0% on
        // top of success" is not a budget anyone can spend from, and rejecting it would make the
        // only obvious way to say "no budget" in JSON an error.
        if (BudgetFraction is 0)
        {
            return RetryBudget.None;
        }

        return RetryBudget.Of(BudgetFraction ?? 0.1, BudgetMinimumPerSecond ?? 3);
    }

    private Resilience? ResolvePreset()
    {
        if (Preset is null)
        {
            return null;
        }

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
/// The bindable shape of a <see cref="NResilience.BreakerSettings"/>, for the same reason
/// <see cref="ResilienceOptions"/> exists: binding a section onto the settings record itself is
/// silently partial, and worse, binding one onto <see cref="Resilience.Breaker"/> constructs a live
/// breaker with default settings while ignoring what the section said.
/// </summary>
/// <remarks>
/// A configured breaker is created per policy and lives as long as the policy does. It is
/// deliberately <b>not</b> recreated when configuration reloads - its state is the point, and
/// discarding an open breaker because somebody edited a JSON file would hand the traffic straight
/// back to the dependency that is down. See <see cref="IResiliencePolicies"/>.
/// </remarks>
public sealed class BreakerOptions
{
    /// <summary><see cref="BreakerSettings.ConsecutiveFailures"/>.</summary>
    public int? ConsecutiveFailures { get; set; }

    /// <summary><see cref="BreakerSettings.FailureRatio"/> - the rate-based trip, for a service with enough traffic to have a rate.</summary>
    public double? FailureRatio { get; set; }

    /// <summary><see cref="BreakerSettings.MinimumCalls"/> - the sample below which the ratio means nothing.</summary>
    public int? MinimumCalls { get; set; }

    /// <summary><see cref="BreakerSettings.Window"/>.</summary>
    public TimeSpan? Window { get; set; }

    /// <summary><see cref="BreakerSettings.BreakDuration"/>.</summary>
    public TimeSpan? BreakDuration { get; set; }

    /// <summary><see cref="BreakerSettings.MaxBreakDuration"/>.</summary>
    public TimeSpan? MaxBreakDuration { get; set; }

    /// <summary><see cref="BreakerSettings.HalfOpenProbes"/>.</summary>
    public int? HalfOpenProbes { get; set; }

    /// <summary><see cref="BreakerSettings.ProbeSuccesses"/>.</summary>
    public int? ProbeSuccesses { get; set; }

    /// <summary><see cref="BreakerSettings.SlowCallThreshold"/> - a call slower than this counts as a failure, because a dependency that has stopped answering in time has failed.</summary>
    public TimeSpan? SlowCallThreshold { get; set; }

    /// <summary><see cref="BreakerSettings.SlowCallRatio"/>.</summary>
    public double? SlowCallRatio { get; set; }

    /// <summary>Builds the live breaker.</summary>
    /// <param name="name">The name it reports itself under. Usually the policy's.</param>
    /// <returns>A new breaker, closed.</returns>
    public Breaker ToBreaker(string? name = null)
    {
        var settings = new BreakerSettings();

        if (ConsecutiveFailures is { } consecutive)
        {
            settings = settings with { ConsecutiveFailures = consecutive };
        }

        if (FailureRatio is { } ratio)
        {
            settings = settings with { FailureRatio = ratio };
        }

        if (MinimumCalls is { } minimum)
        {
            settings = settings with { MinimumCalls = minimum };
        }

        if (Window is { } window)
        {
            settings = settings with { Window = window };
        }

        if (BreakDuration is { } breakDuration)
        {
            settings = settings with { BreakDuration = breakDuration };
        }

        if (MaxBreakDuration is { } maxBreak)
        {
            settings = settings with { MaxBreakDuration = maxBreak };
        }

        if (HalfOpenProbes is { } probes)
        {
            settings = settings with { HalfOpenProbes = probes };
        }

        if (ProbeSuccesses is { } successes)
        {
            settings = settings with { ProbeSuccesses = successes };
        }

        if (SlowCallThreshold is { } slow)
        {
            settings = settings with { SlowCallThreshold = slow };
        }

        if (SlowCallRatio is { } slowRatio)
        {
            settings = settings with { SlowCallRatio = slowRatio };
        }

        return new Breaker(settings) { Name = name };
    }
}
