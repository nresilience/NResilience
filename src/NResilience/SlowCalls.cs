namespace NResilience;

/// <summary>
///     What counts as a slow call, expressed relative to how long a call to this dependency normally
///     takes rather than as a constant somebody has to guess.
///     <para>
///         <c>SlowCalls.Above(3)</c> means "three times slower than normal". That number ports across
///         dependencies, across environments and across a dependency's own capacity changes;
///         <c>800 ms</c> does not, which is why <see cref="BreakerSettings.SlowCallThreshold" /> is a
///         number every operator has to pick per dependency, before that dependency has ever run in
///         production, and re-pick every time it changes.
///     </para>
///     <para>
///         <b>This can only trip sooner.</b> When <see cref="BreakerSettings.SlowCallThreshold" /> is
///         also set the two compose, and an attempt is slow when it is above either threshold - the
///         same rule <see cref="Failures" /> and <see cref="BreakerSettings.FailureRatio" /> follow.
///     </para>
///     <para>
///         <b>Normal is measured over a much longer window than the breaker trips over</b>, and it is a
///         <i>low</i> quantile of that window. Both halves are load-bearing, and getting either wrong
///         produces a breaker that cannot open at all - see the type's remarks.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var breaker = new Breaker(new BreakerSettings
/// {
///     SlowCalls = SlowCalls.Above(3),   // slow = 3x the recent median
///     SlowCallRatio = 0.5,              // half the window being slow trips it
///     MinimumCalls = 20,
/// });
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>Why a low quantile.</b> <see cref="Quantile" /> is capped at <c>0.5</c> on purpose. A
///         brownout that makes every call slow starts contaminating the baseline as soon as it accounts
///         for more than <c>1 - Quantile</c> of the window, so the median survives until half the window
///         is brownout and the p99 survives until 1% of it is. A baseline read from a high quantile
///         moves with the very degradation it is supposed to measure, within one slice, and the breaker
///         then never sees a slow call again.
///     </para>
///     <para>
///         <b>Why a longer window.</b> The trip needs <see cref="BreakerSettings.SlowCallRatio" /> of
///         <see cref="BreakerSettings.TripWindow" /> to fill with slow calls <i>before</i> the baseline
///         moves. That is a race between two clocks, and
///         <see cref="BreakerSettings.Validate" /> refuses configurations that lose it: the baseline must
///         take at least twice as long to contaminate as the trip window takes to fill. At the defaults -
///         a 5-minute baseline at the median against a 30-second window at half slow - it wins by a
///         factor of ten.
///     </para>
///     <para>
///         Every property but <see cref="Multiple" /> has a working default, so <c>SlowCalls.Above(3)</c>
///         is a complete configuration and <c>SlowCalls.Above(3) with { Window = ... }</c> is the way to
///         change one. The defaults are supplied on read rather than by a constructor, for the reason
///         <see cref="Hedge" /> gives: a struct's default instance is the one thing a constructor cannot
///         reach.
///     </para>
/// </remarks>
public readonly record struct SlowCalls
{
    /// <summary>
    ///     The quantile that counts as normal when <see cref="Quantile" /> was not set: the median.
    ///     Low enough that a total brownout has to occupy half the baseline before it moves the number.
    /// </summary>
    private const double DefaultQuantile = 0.5;

    /// <summary>
    ///     The highest quantile that can still be called a baseline. Above this the estimate tracks the
    ///     degradation instead of the health, and the slow-call trip becomes unreachable by construction.
    /// </summary>
    private const double MaxQuantile = 0.5;

    /// <summary>How many samples the baseline needs, when <see cref="MinimumSamples" /> was not set.</summary>
    private const int DefaultMinimumSamples = 20;

    private readonly int? _minimumSamples;
    private readonly double? _quantile;
    private readonly TimeSpan? _window;

    /// <summary>
    ///     How much slower than normal an attempt has to be to count as slow. <c>3</c> means an attempt
    ///     that took three times the baseline.
    ///     <para>
    ///         Must be greater than 1. This is the one number an operator supplies, and it is
    ///         dimensionless on purpose: "3x slower than usual" is a judgment that survives being copied
    ///         to another dependency, and "800 ms" is not.
    ///     </para>
    /// </summary>
    public double Multiple { get; init; }

    /// <summary>
    ///     The quantile of recent successful latency that counts as normal. Default <c>0.5</c>, the
    ///     median, and capped there.
    ///     <para>
    ///         Lower is more conservative: the p25 survives a brownout occupying three quarters of the
    ///         baseline window, at the cost of calling a threshold "normal" that a quarter of healthy
    ///         calls already beat. Raising it is what the cap exists to prevent.
    ///     </para>
    /// </summary>
    public double Quantile
    {
        get => _quantile ?? DefaultQuantile;
        init => _quantile = value;
    }

    /// <summary>
    ///     How much history the baseline covers. Default 5 minutes - ten times
    ///     <see cref="BreakerSettings.TripWindow" />'s default.
    ///     <para>
    ///         This is deliberately long. It is the memory of what healthy looked like, and it has to
    ///         outlast the degradation it is measuring; the trip window is what reacts quickly.
    ///     </para>
    /// </summary>
    public TimeSpan Window
    {
        get => _window ?? TimeSpan.FromMinutes(5);
        init => _window = value;
    }

    /// <summary>
    ///     How many recent successful calls the baseline needs before the slow-call trip is armed at
    ///     all. Default 20, matching <see cref="BreakerSettings.MinimumCalls" />.
    ///     <para>
    ///         Below it there is no baseline, so nothing is slow and the breaker falls back to its other
    ///         trip conditions. A cold process does not guess a threshold; it waits until it has one.
    ///     </para>
    /// </summary>
    public int MinimumSamples
    {
        get => _minimumSamples ?? DefaultMinimumSamples;
        init => _minimumSamples = value;
    }

    /// <summary>
    ///     Value equality over the <i>effective</i> configuration, so a value that names a default
    ///     explicitly equals one that left it alone.
    /// </summary>
    /// <param name="other">The other configuration.</param>
    /// <returns>True when both would behave identically.</returns>
    public bool Equals(SlowCalls other) =>
        Multiple.Equals(other.Multiple)
        && Quantile.Equals(other.Quantile)
        && Window == other.Window
        && MinimumSamples == other.MinimumSamples;

    /// <summary>The way to configure an adaptive slow-call threshold.</summary>
    /// <param name="multiple">How much slower than normal counts as slow. Must be greater than 1.</param>
    /// <returns>The configuration.</returns>
    public static SlowCalls Above(double multiple = 3.0) => new() { Multiple = multiple };

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Multiple, Quantile, Window, MinimumSamples);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Multiple:0.##}x p{Quantile * 100:0.##} over {Window.TotalSeconds:0.#}s (min {MinimumSamples} samples)";

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
                $"SlowCalls.Multiple must be greater than 1; it is {Multiple}. " +
                "Use SlowCalls.Above(3) for an attempt three times slower than normal.");
        }

        if (double.IsNaN(Quantile) || Quantile <= 0 || Quantile > MaxQuantile)
        {
            problems.Add(
                $"SlowCalls.Quantile must be in (0, {MaxQuantile}]; it is {Quantile}. " +
                "The baseline has to be a low quantile: a high one moves with the brownout it is " +
                "supposed to be measuring, and the slow-call trip then never fires.");
        }

        if (Window <= TimeSpan.Zero)
            problems.Add($"SlowCalls.Window must be positive; it is {Window}.");

        if (MinimumSamples < 1)
            problems.Add($"SlowCalls.MinimumSamples must be at least 1; it is {MinimumSamples}.");
    }

    /// <summary>
    ///     The threshold this configuration implies for a measured baseline, saturating rather than
    ///     overflowing on an absurd <see cref="Multiple" />.
    /// </summary>
    /// <param name="normal">The measured baseline.</param>
    /// <returns>The duration at or above which an attempt is slow.</returns>
    internal TimeSpan ThresholdFor(TimeSpan normal)
    {
        var ticks = normal.Ticks * Multiple;

        return ticks >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }
}
