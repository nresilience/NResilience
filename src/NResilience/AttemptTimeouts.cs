namespace NResilience;

/// <summary>
///     A per-attempt ceiling expressed relative to how long a call to this dependency normally takes,
///     rather than as a constant somebody has to guess.
///     <para>
///         <c>AttemptTimeouts.Above(3)</c> means "three times the recent p95". That number ports across
///         dependencies, across environments and across a dependency's own capacity changes;
///         <c>10 s</c> does not, which is why <see cref="Resilience.AttemptTimeout" /> is a number every
///         operator has to pick per dependency, before that dependency has ever run in production, and
///         re-pick every time it changes.
///     </para>
///     <para>
///         <b>This can only tighten.</b> The effective ceiling is
///         <c>min(<see cref="Resilience.AttemptTimeout" />, time left on the deadline, measured)</c>, so
///         the configured constant stops being a guess about normal latency and becomes what it should
///         always have been: the point beyond which the caller does not care. The measured term does the
///         work inside it, and the worst it can do is stop shortening.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var api = Resilience.Http with
/// {
///     AttemptTimeout = TimeSpan.FromSeconds(5),   // the ceiling. Never exceeded.
///     Timeouts = AttemptTimeouts.Above(3),        // and usually far below it: 3x the recent p95.
/// };
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>Why a high quantile, and why a long window.</b> A timeout wants the tail - the question is
///         "how long does a call that is going to succeed take, at worst", and reading that from the
///         median would cancel half the healthy calls. It also wants that tail to resist moving, which is
///         why the window is ten times <see cref="BreakerSettings.Window" />'s default: a short window
///         would let one bad minute raise the ceiling for the next one.
///     </para>
///     <para>
///         A high quantile is contaminated by a brownout sooner than a low one, and for
///         <see cref="Hedge" /> that is the feature while for <see cref="SlowCalls" /> it would be fatal.
///         Here it is neither, because of the clamp: contamination can only push the measured term
///         <i>up</i>, and up is where <see cref="Resilience.AttemptTimeout" /> is already waiting. A
///         degrading dependency therefore converges on the behaviour the policy has today rather than on
///         a ceiling nobody chose.
///     </para>
///     <para>
///         <b>Only successful attempts are sampled</b>, which makes the feature self-correcting in the
///         one direction that matters. A ceiling tight enough to cancel calls that would have succeeded
///         starves its own estimator: the window falls below <see cref="MinimumSamples" />, the measured
///         term disappears, and the policy reverts to <see cref="Resilience.AttemptTimeout" /> until
///         successes accumulate again.
///     </para>
///     <para>
///         Every property but <see cref="Multiple" /> has a working default, so
///         <c>AttemptTimeouts.Above(3)</c> is a complete configuration and
///         <c>AttemptTimeouts.Above(3) with { Window = ... }</c> is the way to change one. The defaults
///         are supplied on read rather than by a constructor, for the reason <see cref="Hedge" /> gives:
///         a struct's default instance is the one thing a constructor cannot reach.
///     </para>
/// </remarks>
public readonly record struct AttemptTimeouts
{
    /// <summary>
    ///     The quantile of recent successful latency the ceiling is measured from when
    ///     <see cref="Quantile" /> was not set: the p95.
    /// </summary>
    private const double DefaultQuantile = 0.95;

    /// <summary>
    ///     The lowest quantile that can still be called a tail. Below it the estimate is describing the
    ///     body of the distribution, and <see cref="Multiple" /> would have to absorb the entire spread
    ///     between the body and the tail to avoid cancelling healthy calls - which is a threshold
    ///     somebody is guessing again, with an extra step.
    /// </summary>
    private const double MinQuantile = 0.5;

    /// <summary>
    ///     The highest quantile worth asking a windowed estimate for. Above it the answer rests on a
    ///     handful of samples per slice, and the ceiling would step around with the noise.
    /// </summary>
    private const double MaxQuantile = 0.99;

    /// <summary>How many samples the estimate needs, when <see cref="MinimumSamples" /> was not set.</summary>
    private const int DefaultMinimumSamples = 20;

    private readonly TimeSpan? _floor;
    private readonly int? _minimumSamples;
    private readonly double? _quantile;
    private readonly TimeSpan? _window;

    /// <summary>
    ///     How many times the measured tail an attempt is allowed to take. <c>3</c> means an attempt is
    ///     cancelled once it has run three times as long as the recent p95 of successful calls.
    ///     <para>
    ///         Must be greater than 1. This is the one number an operator supplies, and it is
    ///         dimensionless on purpose: "three times slower than this dependency's worst normal call"
    ///         is a judgment that survives being copied to another dependency, and "10 s" is not.
    ///     </para>
    /// </summary>
    public double Multiple { get; init; }

    /// <summary>
    ///     The quantile of recent successful latency the ceiling is measured from. Default <c>0.95</c>,
    ///     and it must be between <c>0.5</c> and <c>0.99</c>.
    ///     <para>
    ///         Higher tolerates a longer tail before cancelling anything, at the cost of an estimate
    ///         resting on fewer samples. Lower is steadier and needs a larger <see cref="Multiple" /> to
    ///         avoid cancelling calls that were merely on the slow side of normal.
    ///     </para>
    /// </summary>
    public double Quantile
    {
        get => _quantile ?? DefaultQuantile;
        init => _quantile = value;
    }

    /// <summary>
    ///     How much history the estimate covers. Default 5 minutes - ten times
    ///     <see cref="BreakerSettings.Window" />'s default, and the same span
    ///     <see cref="SlowCalls.Window" /> uses.
    ///     <para>
    ///         Deliberately long. It is the memory of what a slow-but-healthy call looked like, and a
    ///         ceiling that follows the last thirty seconds is a ceiling that rises during exactly the
    ///         minute you wanted it to hold.
    ///     </para>
    /// </summary>
    public TimeSpan Window
    {
        get => _window ?? TimeSpan.FromMinutes(5);
        init => _window = value;
    }

    /// <summary>
    ///     How many recent successful calls the estimate needs before it bounds anything. Default 20,
    ///     matching <see cref="BreakerSettings.MinimumCalls" /> and <see cref="Hedge.MinimumSamples" />.
    ///     <para>
    ///         Below it there is no estimate, and the attempt gets
    ///         <see cref="Resilience.AttemptTimeout" /> exactly as it does today. A cold process does not
    ///         guess a ceiling; it waits until it has one.
    ///     </para>
    /// </summary>
    public int MinimumSamples
    {
        get => _minimumSamples ?? DefaultMinimumSamples;
        init => _minimumSamples = value;
    }

    /// <summary>
    ///     A floor under the measured ceiling. Default 50 ms.
    ///     <para>
    ///         The mirror of <see cref="Hedge.MinimumDelay" />, and it exists for the same reason: a
    ///         dependency whose p95 is 300 µs would otherwise have every attempt cancelled at 900 µs, so
    ///         one GC pause becomes a failed call. The floor is the "do not bother" line.
    ///     </para>
    /// </summary>
    public TimeSpan Floor
    {
        get => _floor ?? TimeSpan.FromMilliseconds(50);
        init => _floor = value;
    }

    /// <summary>The way to configure an adaptive attempt ceiling.</summary>
    /// <param name="multiple">How many times the measured tail an attempt may take. Must be greater than 1.</param>
    /// <returns>The configuration.</returns>
    public static AttemptTimeouts Above(double multiple = 3.0) => new() { Multiple = multiple };

    /// <summary>
    ///     Value equality over the <i>effective</i> configuration, so a value that names a default
    ///     explicitly equals one that left it alone.
    /// </summary>
    /// <param name="other">The other configuration.</param>
    /// <returns>True when both would behave identically.</returns>
    public bool Equals(AttemptTimeouts other) =>
        Multiple.Equals(other.Multiple)
        && Quantile.Equals(other.Quantile)
        && Window == other.Window
        && MinimumSamples == other.MinimumSamples
        && Floor == other.Floor;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Multiple, Quantile, Window, MinimumSamples, Floor);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Multiple:0.##}x p{Quantile * 100:0.##} over {Window.TotalSeconds:0.#}s " +
        $"(min {MinimumSamples} samples, floor {Floor.TotalMilliseconds:0.#}ms)";

    /// <summary>
    ///     Collects everything wrong with this configuration on its own, in the shape
    ///     <see cref="Resilience.Validate" /> reports problems. What is wrong with it only in
    ///     combination with the surrounding policy is checked there.
    /// </summary>
    /// <param name="problems">The list to add to.</param>
    internal void Validate(List<string> problems)
    {
        if (double.IsNaN(Multiple) || double.IsInfinity(Multiple) || Multiple <= 1)
        {
            problems.Add(
                $"Timeouts.Multiple must be greater than 1; it is {Multiple}. " +
                "Use AttemptTimeouts.Above(3) for an attempt allowed three times the recent p95.");
        }

        if (double.IsNaN(Quantile) || Quantile < MinQuantile || Quantile > MaxQuantile)
        {
            problems.Add(
                $"Timeouts.Quantile must be in [{MinQuantile}, {MaxQuantile}]; it is {Quantile}. " +
                "A ceiling has to be measured from the tail: the median describes the calls you want " +
                "to keep, and a quantile above the cap rests on too few samples per slice to be steady.");
        }

        if (Window <= TimeSpan.Zero)
            problems.Add($"Timeouts.Window must be positive; it is {Window}.");

        if (MinimumSamples < 1)
            problems.Add($"Timeouts.MinimumSamples must be at least 1; it is {MinimumSamples}.");

        if (Floor <= TimeSpan.Zero)
            problems.Add($"Timeouts.Floor must be positive; it is {Floor}.");
    }

    /// <summary>
    ///     The ceiling this configuration implies for a measured tail, before the floor and the
    ///     configured <see cref="Resilience.AttemptTimeout" /> are applied. Saturates rather than
    ///     overflowing on an absurd <see cref="Multiple" />.
    /// </summary>
    /// <param name="tail">The measured quantile.</param>
    /// <returns>The duration an attempt is allowed to run for.</returns>
    internal TimeSpan CeilingFor(TimeSpan tail)
    {
        var ticks = tail.Ticks * Multiple;

        return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }
}
