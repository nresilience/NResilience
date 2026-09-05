namespace NResilience;

/// <summary>
///     The first retry delay, expressed relative to how long a call to this dependency normally takes
///     rather than as a constant somebody has to guess.
///     <para>
///         <c>MeasuredBase.Times(1)</c> means "wait about as long as a normal call takes before trying
///         again". That number ports across dependencies, across environments and across a dependency's
///         own capacity changes; <c>100 ms</c> does not. Against a dependency whose median is three
///         seconds a 100 ms base is not backoff at all - the retry lands while the first attempt's work
///         is very likely still queued somewhere. Against one whose median is two milliseconds it spends
///         100 ms of the deadline doing nothing.
///     </para>
///     <para>
///         <b>This applies to the transient base only.</b>
///         <see cref="Backoff.ThrottledBase" /> stays the constant it was configured as - see the
///         remarks.
///     </para>
///     <para>
///         <b>It is clamped to a band around the configured base</b>, in both directions, by
///         <see cref="Spread" />. Unlike <see cref="AttemptCeiling" /> this estimate is not
///         tighten-only: a longer backoff during a brownout is arguably correct, and it also lengthens
///         every call's wall-clock time during the incident. So the constant the operator wrote stays
///         the anchor and the measurement moves within a factor of it.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var api = Resilience.Http with
/// {
///     Backoff = Backoff.Measured(1.0),   // first retry waits about one normal call
/// };
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>Why the transient base only.</b> A throttled base is not a latency question. A rate
///         limiter that answers in two milliseconds is telling you about its token bucket's refill
///         interval, and nothing about that interval is visible in how fast it said no; deriving the
///         wait from the latency would send a hostile retry rate at the one dependency that has
///         explicitly asked for less. Where the server does know, it says so, and
///         <see cref="Verdict.RetryAfter" /> already wins over every curve here.
///     </para>
///     <para>
///         <b>Why a low quantile.</b> <see cref="Quantile" /> is capped at <c>0.5</c> for the reason
///         <see cref="SlowCalls.Quantile" /> is: this is a measure of what healthy looks like, and a
///         baseline read from the tail moves with the very degradation it is supposed to be measured
///         against. Here that would compound - a slower dependency would produce a longer backoff, which
///         holds the caller's deadline longer, which is exactly the cost <see cref="Spread" /> exists to
///         bound.
///     </para>
///     <para>
///         <b>Only successful attempts are sampled</b>, so a wave of fast failures cannot collapse the
///         base to nothing and turn backoff into a retry storm.
///     </para>
///     <para>
///         Every property but <see cref="Multiple" /> has a working default, so <c>MeasuredBase.Times(1)</c>
///         is a complete configuration and <c>MeasuredBase.Times(1) with { Window = ... }</c> is the way to
///         change one. The defaults are supplied on read rather than by a constructor, for the reason
///         <see cref="Hedge" /> gives: a struct's default instance is the one thing a constructor cannot
///         reach.
///     </para>
/// </remarks>
public readonly record struct MeasuredBase
{
    /// <summary>
    ///     The multiple used when <see cref="Times" /> is called without one: one normal call. Long enough
    ///     that the first attempt's work has plausibly cleared the dependency, short enough that a
    ///     three-attempt policy still fits inside a deadline sized for one.
    /// </summary>
    public const double DefaultMultiple = 1.0;

    /// <summary>
    ///     The quantile that counts as normal when <see cref="Quantile" /> was not set: the median.
    ///     Low enough that a total brownout has to occupy half the baseline before it moves the number.
    /// </summary>
    private const double DefaultQuantile = 0.5;

    /// <summary>
    ///     The highest quantile that can still be called a baseline. Above this the estimate tracks the
    ///     degradation rather than the health, and the backoff grows with the incident.
    /// </summary>
    private const double MaxQuantile = 0.5;

    /// <summary>How many samples the baseline needs, when <see cref="MinimumSamples" /> was not set.</summary>
    private const int DefaultMinimumSamples = 20;

    /// <summary>How far the measured base may move from the configured one, when <see cref="Spread" /> was not set.</summary>
    private const double DefaultSpread = 10.0;

    private readonly int? _minimumSamples;
    private readonly double? _quantile;
    private readonly double? _spread;
    private readonly TimeSpan? _window;

    /// <summary>
    ///     How many normal calls the first retry waits. <c>1</c> means the base delay is the recent
    ///     median of a successful call to this dependency.
    ///     <para>
    ///         Must be greater than zero. This is the one number an operator supplies, and it is
    ///         dimensionless on purpose: "wait one normal call" is a judgment that survives being copied
    ///         to another dependency, and "100 ms" is not.
    ///     </para>
    /// </summary>
    public double Multiple { get; init; }

    /// <summary>
    ///     The quantile of recent successful latency that counts as normal. Default <c>0.5</c>, the
    ///     median, and capped there.
    ///     <para>
    ///         Lower is steadier under degradation, at the cost of a base derived from the fast half of
    ///         the traffic. Raising it is what the cap exists to prevent.
    ///     </para>
    /// </summary>
    public double Quantile
    {
        get => _quantile ?? DefaultQuantile;
        init => _quantile = value;
    }

    /// <summary>
    ///     How much history the baseline covers. Default 5 minutes, the same span
    ///     <see cref="SlowCalls.Window" /> and <see cref="AttemptCeiling.Window" /> use.
    ///     <para>
    ///         Deliberately long. It is the memory of what healthy looked like, and a base that follows
    ///         the last thirty seconds is a base that grows during exactly the minute the caller's
    ///         deadline is under most pressure.
    ///     </para>
    /// </summary>
    public TimeSpan Window
    {
        get => _window ?? TimeSpan.FromMinutes(5);
        init => _window = value;
    }

    /// <summary>
    ///     How many recent successful calls the baseline needs before it moves anything. Default 20,
    ///     matching <see cref="AttemptCeiling.MinimumSamples" /> and <see cref="Hedge.MinimumSamples" />.
    ///     <para>
    ///         Below it there is no baseline, and the retry waits <see cref="Backoff.TransientBase" />
    ///         unchanged. A cold process does not guess a delay; it waits until it has
    ///         one.
    ///     </para>
    /// </summary>
    public int MinimumSamples
    {
        get => _minimumSamples ?? DefaultMinimumSamples;
        init => _minimumSamples = value;
    }

    /// <summary>
    ///     How far the measured base may move from <see cref="Backoff.TransientBase" />, as a factor in
    ///     either direction. Default 10, so a 100 ms configured base yields a measured base between
    ///     10 ms and 1 s.
    ///     <para>
    ///         This is the guardrail, and it is symmetric because both ends do damage: a base that
    ///         collapses toward zero is a retry storm against a dependency that is already failing, and
    ///         one that grows without bound spends a deadline the caller wanted for attempts on waiting
    ///         between them. Must be greater than 1.
    ///     </para>
    /// </summary>
    public double Spread
    {
        get => _spread ?? DefaultSpread;
        init => _spread = value;
    }

    /// <summary>
    ///     Value equality over the <i>effective</i> configuration, so a value that names a default
    ///     explicitly equals one that left it alone.
    /// </summary>
    /// <param name="other">The other configuration.</param>
    /// <returns>True when both would behave identically.</returns>
    public bool Equals(MeasuredBase other) =>
        Multiple.Equals(other.Multiple)
        && Quantile.Equals(other.Quantile)
        && Window == other.Window
        && MinimumSamples == other.MinimumSamples
        && Spread.Equals(other.Spread);

    /// <summary>The way to configure an adaptive backoff base.</summary>
    /// <param name="multiple">How many normal calls the first retry waits. Must be greater than zero.</param>
    /// <returns>The configuration.</returns>
    public static MeasuredBase Times(double multiple = DefaultMultiple) => new() { Multiple = multiple };

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Multiple, Quantile, Window, MinimumSamples, Spread);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Multiple:0.##}x p{Quantile * 100:0.##} over {Window.TotalSeconds:0.#}s " +
        $"(min {MinimumSamples} samples, within {Spread:0.##}x the configured base)";

    /// <summary>
    ///     Collects everything wrong with this configuration on its own, in the shape
    ///     <see cref="Resilience.Validate" /> reports problems. What is wrong with it only in
    ///     combination with the surrounding curve is checked in <see cref="Backoff.Validate" />.
    /// </summary>
    /// <param name="problems">The list to add to.</param>
    internal void Validate(List<string> problems)
    {
        if (double.IsNaN(Multiple) || double.IsInfinity(Multiple) || Multiple <= 0)
        {
            problems.Add(
                $"MeasuredBase.Multiple must be greater than zero; it is {Multiple}. " +
                "Use MeasuredBase.Times(1) for a first retry that waits about one normal call.");
        }

        if (double.IsNaN(Quantile) || Quantile <= 0 || Quantile > MaxQuantile)
        {
            problems.Add(
                $"MeasuredBase.Quantile must be in (0, {MaxQuantile}]; it is {Quantile}. " +
                "The baseline has to be a low quantile: a high one moves with the brownout it is " +
                "supposed to be measured against, and the backoff then grows with the incident.");
        }

        if (Window <= TimeSpan.Zero)
            problems.Add($"MeasuredBase.Window must be positive; it is {Window}.");

        if (MinimumSamples < 1)
            problems.Add($"MeasuredBase.MinimumSamples must be at least 1; it is {MinimumSamples}.");

        if (double.IsNaN(Spread) || double.IsInfinity(Spread) || Spread <= 1)
        {
            problems.Add(
                $"MeasuredBase.Spread must be greater than 1; it is {Spread}. " +
                "It is the factor the measured base may move from the configured one in either " +
                "direction, and a spread of 1 pins it to the constant it was supposed to replace.");
        }
    }

    /// <summary>
    ///     The base delay this configuration implies for a measured baseline, clamped to
    ///     <see cref="Spread" /> either side of the configured base. Saturates rather than overflowing
    ///     on an absurd <see cref="Multiple" />.
    /// </summary>
    /// <param name="configured">The base the curve was configured with, which anchors the clamp.</param>
    /// <param name="normal">The measured baseline.</param>
    /// <returns>The base delay the first retry uses.</returns>
    internal TimeSpan BaseFor(TimeSpan configured, TimeSpan normal)
    {
        var measured = normal.Ticks * Multiple;
        var floor = configured.Ticks / Spread;
        var ceiling = configured.Ticks * Spread;

        if (measured < floor)
            measured = floor;

        if (measured > ceiling)
            measured = ceiling;

        if (!double.IsFinite(measured) || measured <= 0)
            return TimeSpan.Zero;

        return measured >= long.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)measured);
    }
}
