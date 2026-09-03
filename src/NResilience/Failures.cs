namespace NResilience;

/// <summary>
///     What counts as too many failures, expressed relative to how often a call to this dependency
///     normally fails rather than as a constant somebody has to guess.
///     <para>
///         <c>Failures.Above(5)</c> means "five times its own recent error rate". That number ports
///         across dependencies, across environments and across a dependency's own changes;
///         <c>0.5</c> ports nowhere, which is why <see cref="BreakerSettings.FailureRatio" /> is a number
///         every operator has to pick per dependency and re-pick every time it changes. A payments API
///         whose steady state is 0.02% transient is deeply broken at 5%, and a flaky third-party search
///         backend runs at 30% transient all day; no single absolute ratio is right for both.
///     </para>
///     <para>
///         <b>This can only trip sooner.</b> When <see cref="BreakerSettings.FailureRatio" /> is also
///         set it stays the ceiling: the effective trip point is
///         <c>min(FailureRatio, max(<see cref="Floor" />, <see cref="Multiple" /> x baseline))</c>.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var breaker = new Breaker(new BreakerSettings
/// {
///     Failures = Failures.Above(5),   // too many = 5x the recent error rate
///     MinimumCalls = 20,              // ...measured over the trip window
/// });
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>Why the floor is not optional.</b> A baseline of 0.02% times a multiple of 5 is 0.1%, and
///         on a 30-second window at 20 calls that is a single failure. Without
///         <see cref="Floor" /> this feature is a breaker that opens on one error against a
///         perfectly healthy dependency. The floor is what says "below 5% absolute, nothing is wrong no
///         matter how quiet the baseline was".
///     </para>
///     <para>
///         <b>Why <see cref="MinimumSamples" /> is higher than <see cref="SlowCalls.MinimumSamples" />.</b>
///         An error rate needs more samples to estimate than a latency quantile does, because errors are
///         rare by construction: a rate estimated from 20 calls has a resolution of 5%, which is the
///         floor itself.
///     </para>
///     <para>
///         The trip window has the same problem from the other end, and the breaker answers it the same
///         way: a relative trip needs <b>at least two failures</b> in the window whatever the ratio
///         says, because at a 5% floor and <see cref="BreakerSettings.MinimumCalls" />'s default of 20 a
///         single transient error is already 5%, and one failure is not a rate. The absolute
///         <see cref="BreakerSettings.FailureRatio" /> is not held to that: a caller who wrote
///         <c>0.05</c> over 20 calls asked for exactly that reading.
///     </para>
///     <para>
///         <b>Why a longer window.</b> The baseline has to outlast the incident it is measuring. A
///         brownout contaminates it as it fills, so the trip has to fire before the baseline has risen
///         far enough to make the trip unreachable - and that is a race between two clocks, which
///         <see cref="BreakerSettings.Validate" /> refuses to lose. At the defaults - a 5-minute
///         baseline at a multiple of 5 against a 30-second trip window - it is won with the required
///         factor of two.
///     </para>
///     <para>
///         Every property but <see cref="Multiple" /> has a working default, so <c>Failures.Above(5)</c>
///         is a complete configuration and <c>Failures.Above(5) with { Window = ... }</c> is the way to
///         change one. The defaults are supplied on read rather than by a constructor, for the reason
///         <see cref="Hedge" /> gives: a struct's default instance is the one thing a constructor cannot
///         reach.
///     </para>
/// </remarks>
public readonly record struct Failures
{
    /// <summary>
    ///     The error rate below which nothing is wrong, whatever the baseline was, when
    ///     <see cref="Floor" /> was not set.
    /// </summary>
    private const double DefaultFloor = 0.05;

    /// <summary>
    ///     How many samples the baseline needs, when <see cref="MinimumSamples" /> was not set. Five
    ///     times <see cref="SlowCalls.MinimumSamples" />, because a rate is a coarser measurement than a
    ///     quantile.
    /// </summary>
    private const int DefaultMinimumSamples = 100;

    private readonly double? _absoluteFloor;
    private readonly int? _minimumSamples;
    private readonly TimeSpan? _window;

    /// <summary>
    ///     How many times its own recent error rate the dependency has to be failing at to open the
    ///     breaker. <c>5</c> means a trip window five times as bad as the last five minutes.
    ///     <para>
    ///         Must be greater than 1. This is the one number an operator supplies, and it is
    ///         dimensionless on purpose: "five times worse than usual" is a judgment that survives being
    ///         copied to another dependency, and "50% of calls" is not.
    ///     </para>
    /// </summary>
    public double Multiple { get; init; }

    /// <summary>
    ///     How much history the baseline covers. Default 5 minutes - ten times
    ///     <see cref="BreakerSettings.TripWindow" />'s default, and the same span
    ///     <see cref="SlowCalls.Window" /> uses.
    ///     <para>
    ///         Deliberately long. It is the memory of how often this dependency fails when it is
    ///         healthy, and it has to outlast the failure it is measuring; the trip window is what
    ///         reacts quickly.
    ///     </para>
    /// </summary>
    public TimeSpan Window
    {
        get => _window ?? TimeSpan.FromMinutes(5);
        init => _window = value;
    }

    /// <summary>
    ///     How many sampled calls the baseline needs before the relative trip is armed at all. Default
    ///     100.
    ///     <para>
    ///         Below it there is no baseline, so the breaker falls back to
    ///         <see cref="BreakerSettings.FailureRatio" /> if one is set and to its other trip
    ///         conditions if not. A cold process does not guess an error rate; it waits until it has
    ///         one.
    ///     </para>
    /// </summary>
    public int MinimumSamples
    {
        get => _minimumSamples ?? DefaultMinimumSamples;
        init => _minimumSamples = value;
    }

    /// <summary>
    ///     The error rate the relative trip never fires below, whatever the baseline was. Default
    ///     <c>0.05</c>.
    ///     <para>
    ///         The mirror of <see cref="AttemptCeiling.Floor" />, and it exists for the same reason: a
    ///         dependency that essentially never fails has a baseline near zero, and any multiple of
    ///         near-zero is one unlucky call. The floor is the "nothing is wrong here" line.
    ///     </para>
    /// </summary>
    public double Floor
    {
        get => _absoluteFloor ?? DefaultFloor;
        init => _absoluteFloor = value;
    }

    /// <summary>
    ///     Value equality over the <i>effective</i> configuration, so a value that names a default
    ///     explicitly equals one that left it alone.
    /// </summary>
    /// <param name="other">The other configuration.</param>
    /// <returns>True when both would behave identically.</returns>
    public bool Equals(Failures other) =>
        Multiple.Equals(other.Multiple)
        && Window == other.Window
        && MinimumSamples == other.MinimumSamples
        && Floor.Equals(other.Floor);

    /// <summary>The way to configure a relative failure trip.</summary>
    /// <param name="multiple">How many times the baseline error rate counts as too many. Must be greater than 1.</param>
    /// <returns>The configuration.</returns>
    public static Failures Above(double multiple = 5.0) => new() { Multiple = multiple };

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Multiple, Window, MinimumSamples, Floor);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Multiple:0.##}x the rate over {Window.TotalSeconds:0.#}s " +
        $"(min {MinimumSamples} samples, floor {Floor:0.##%})";

    /// <summary>
    ///     Collects everything wrong with this configuration on its own, in the shape
    ///     <see cref="BreakerSettings.Validate" /> reports problems. What is wrong with it only in
    ///     combination with the surrounding settings is checked there.
    /// </summary>
    /// <param name="problems">The list to add to.</param>
    internal void Validate(List<string> problems)
    {
        if (double.IsNaN(Multiple) || double.IsInfinity(Multiple) || Multiple <= 1)
        {
            problems.Add(
                $"Failures.Multiple must be greater than 1; it is {Multiple}. " +
                "Use Failures.Above(5) for a trip window five times worse than the recent baseline.");
        }

        if (Window <= TimeSpan.Zero)
            problems.Add($"Failures.Window must be positive; it is {Window}.");

        if (MinimumSamples < 1)
            problems.Add($"Failures.MinimumSamples must be at least 1; it is {MinimumSamples}.");

        if (double.IsNaN(Floor) || Floor <= 0 || Floor > 1)
        {
            problems.Add(
                $"Failures.Floor must be in (0, 1]; it is {Floor}. " +
                "Without a floor, a multiple of a near-zero baseline is a single failure.");
        }
    }

    /// <summary>
    ///     The trip ratio this configuration implies for a measured baseline: never below
    ///     <see cref="Floor" />, and never above 1, where the whole trip window failing is what
    ///     it takes.
    /// </summary>
    /// <param name="baseline">The measured baseline error rate.</param>
    /// <returns>The proportion of the trip window that has to fail.</returns>
    internal double ThresholdFor(double baseline) =>
        Math.Min(1, Math.Max(Floor, baseline * Multiple));
}
