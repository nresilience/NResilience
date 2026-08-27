namespace NResilience.Testing;

/// <summary>
///     Faults and latency, injected at a rate you choose. This is how you find out what your policy
///     does before the dependency decides to show you.
///     <para>
///         Chaos wraps the <b>callback</b>, not the policy, and that placement is the point: an injected
///         fault travels through the classifier, the breaker, the retry budget and the attempt log
///         exactly like a real one. A game day therefore exercises the machinery you actually ship
///         rather than a parallel path that only exists in tests.
///     </para>
///     <para>
///         It is a record, so <c>with</c> derives a variant the same way it does for a policy, and a
///         profile can be held in a <c>static readonly</c> field or bound from configuration.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var chaos = new Chaos { Enabled = true, FaultRate = 0.1, LatencyRate = 0.2, Latency = TimeSpan.FromSeconds(2) };
///
/// var result = await policy.TryRunAsync(chaos.Inject(ct => dependency.CallAsync(ct)), cancellationToken);
/// </code>
/// </example>
/// <remarks>
///     This type lives in <c>NResilience.Testing</c> on purpose. Running a game day in production is a
///     legitimate thing to want, and doing it means taking a package named <c>Testing</c> as a runtime
///     dependency - a deliberate act that shows up in a project file diff. <see cref="Enabled" />
///     defaults to false on top of that, so a profile bound from a configuration section that does not
///     mention it is inert.
/// </remarks>
public sealed record Chaos
{
    /// <summary>Injects nothing. What <c>Inject</c> hands your callback straight back for.</summary>
    public static Chaos None { get; } = new();

    /// <summary>
    ///     The master switch, off by default.
    ///     <para>
    ///         While this is false, <c>Inject</c> returns the callback you passed it, unwrapped - so an
    ///         inert profile costs one branch at composition time and nothing at all per call.
    ///     </para>
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    ///     The fraction of calls that fail, from 0 to 1. The roll is independent of
    ///     <see cref="LatencyRate" />, so a call can be both slow and doomed - which is the shape most
    ///     real degradations take.
    /// </summary>
    public double FaultRate { get; init; }

    /// <summary>
    ///     What a failing call throws. Null injects an <see cref="IOException" />, which
    ///     <see cref="Classifier.Default" /> and <see cref="Classifier.Http" /> both call
    ///     <see cref="VerdictKind.Transient" />.
    ///     <para>
    ///         That default is chosen rather than convenient. An exception type the classifier does not
    ///         recognize is <see cref="VerdictKind.Permanent" />, so injecting one would produce a chaos
    ///         run in which nothing is ever retried - the feature silently testing none of the machinery
    ///         it exists to test.
    ///     </para>
    /// </summary>
    public Func<Exception>? Fault { get; init; }

    /// <summary>The fraction of calls that are slowed, from 0 to 1.</summary>
    public double LatencyRate { get; init; }

    /// <summary>
    ///     How much slower a slowed call is.
    ///     <para>
    ///         Served on the attempt's own cancellation token, so an injected delay longer than
    ///         <see cref="Resilience.AttemptTimeout" /> is cut short by it. That is what makes this the
    ///         way to test a timeout: the delay and the bound meet on the real path.
    ///     </para>
    /// </summary>
    public TimeSpan Latency { get; init; }

    /// <summary>
    ///     Asked before every roll. Return false to leave this call alone, for a blast radius narrower
    ///     than a rate can express - one tenant, one region, one shard, or a window of wall-clock time.
    ///     <para>
    ///         Called on the calling thread and expected to be cheap. An exception it throws is not
    ///         caught, and reaches your callback's caller as a chaos-injected failure of the least useful
    ///         kind.
    ///     </para>
    /// </summary>
    public Func<bool>? Gate { get; init; }

    /// <summary>
    ///     Fixes the random stream, so a test that asserts on how many calls were injected is
    ///     repeatable. Null draws from the clock.
    ///     <para>
    ///         Each <c>Inject</c> call and each <see cref="ChaosHandler" /> draws its own stream from this
    ///         seed, so two of them derived from one profile do not interleave. Within one stream the
    ///         sequence is fixed; which concurrent caller receives which draw is not.
    ///     </para>
    /// </summary>
    public int? Seed { get; init; }

    /// <summary>The clock the injected latency is served against. Set this to a fake clock in a test.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    ///     Checks the profile and throws <see cref="ResilienceConfigurationException" /> listing every
    ///     problem at once, the same way <see cref="Resilience.Validate" /> does.
    /// </summary>
    /// <exception cref="ResilienceConfigurationException">The profile cannot be used.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        CheckRate(FaultRate, nameof(FaultRate), problems);
        CheckRate(LatencyRate, nameof(LatencyRate), problems);

        if (Latency < TimeSpan.Zero)
            problems.Add($"Latency must not be negative; it is {Latency}.");

        if (LatencyRate > 0 && Latency == TimeSpan.Zero)
            problems.Add("LatencyRate is set but Latency is zero, so nothing would be slowed.");

        if (Time is null)
            problems.Add("Time must not be null.");

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }

    /// <summary>Runs <see cref="Validate" /> and returns this profile, so a bad one throws where it is written.</summary>
    /// <returns>This profile.</returns>
    /// <exception cref="ResilienceConfigurationException">The profile cannot be used.</exception>
    public Chaos Validated()
    {
        Validate();
        return this;
    }

    private static void CheckRate(double value, string name, List<string> problems)
    {
        if (double.IsNaN(value) || value < 0 || value > 1)
            problems.Add($"{name} must be between 0 and 1; it is {value}.");
    }
}
