namespace NResilience;

/// <summary>
///     When to start a second copy of an attempt that has not come back yet.
///     <para>
///         Hedging trades a little extra load for a much shorter tail. A call that is taking longer than
///         almost every other call to the same dependency is probably not going to finish quickly, and a
///         second attempt starting now will often beat it - so the caller sees the p99 of two draws
///         rather than the p99 of one.
///     </para>
///     <para>
///         The threshold is <b>always a live quantile of recent latency</b>, never a constant, and that
///         is the whole safety argument. A constant threshold turns a tail-latency tool into a load
///         generator: when the dependency browns out so that every call exceeds the constant, every call
///         hedges and the library doubles the traffic to a service that is already in trouble. A
///         quantile of the recent distribution cannot do that - a brownout moves the quantile with it,
///         so the fraction of calls that hedge stays at roughly <c>1 - Quantile</c> whatever the
///         dependency is doing. There is deliberately no <c>Hedge.After(TimeSpan)</c>.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var api = Resilience.Http with
/// {
///     Attempts = 3,              // at most 3 calls reach the dependency, whatever the shape
///     Hedge = Hedge.At(0.95),    // the 2nd may start before the 1st comes back
/// };
/// </code>
/// </example>
/// <remarks>
///     Every property but <see cref="Quantile" /> has a working default, so
///     <c>Hedge.At(0.95)</c> is a complete configuration and <c>Hedge.At(0.95) with { MaxConcurrent = 3 }</c>
///     is the way to change one. The defaults are supplied on read rather than by a constructor, because
///     a struct's default instance is the one thing a constructor cannot reach - so
///     <c>new Hedge { Quantile = 0.99 }</c> behaves the same as <c>Hedge.At(0.99)</c> instead of
///     configuring a hedge with no concurrency and no minimum sample count. The backing fields are
///     nullable rather than sentinel-valued so that an explicit zero stays an explicit zero, and
///     <see cref="Equals(Hedge)" /> compares the effective values so "left alone" and "set to the
///     default" are the same configuration.
/// </remarks>
public readonly record struct Hedge
{
    /// <summary>
    ///     How many samples the latency estimate needs before any hedge fires, when
    ///     <see cref="MinimumSamples" /> was not set. Matches <see cref="BreakerSettings.MinimumCalls" />,
    ///     for the same reason: below it, the number is noise rather than an estimate.
    /// </summary>
    private const int DefaultMinimumSamples = 20;

    /// <summary>How many attempts may be in flight at once, when <see cref="MaxConcurrent" /> was not set.</summary>
    private const int DefaultMaxConcurrent = 2;

    /// <summary>
    ///     How far towards the breaker's own trip point the error rate may climb before hedging stops,
    ///     when <see cref="SuppressAt" /> was not set. Half way: a dependency failing at half the rate
    ///     that would open the breaker is one this process should not be sending extra load to.
    /// </summary>
    private const double DefaultSuppressAt = 0.5;

    private readonly int? _maxConcurrent;
    private readonly int? _minimumSamples;
    private readonly TimeSpan? _minimumDelay;
    private readonly double? _suppressAt;
    private readonly TimeSpan? _window;

    /// <summary>
    ///     The quantile of recent latency a hedge fires at. <c>0.95</c> hedges the slowest 5% of calls
    ///     and so costs about 5% extra traffic; <c>0.99</c> hedges 1% and shortens a smaller part of the
    ///     tail.
    ///     <para>
    ///         This is the load multiplier, stated as the thing an operator actually wants to bound.
    ///         Must be at least <c>0.5</c>: below that, "hedge" stops describing what is happening.
    ///     </para>
    /// </summary>
    public double Quantile { get; init; }

    /// <summary>
    ///     How many attempts may be in flight at once, counting the first. Default 2, minimum 2 - a
    ///     hedge that may not overlap anything is not a hedge.
    ///     <para>
    ///         This bounds concurrency, not the number of calls: <see cref="Resilience.Attempts" /> stays
    ///         the total number of calls that reach the dependency, whether they run one after another or
    ///         at the same time.
    ///     </para>
    /// </summary>
    public int MaxConcurrent
    {
        get => _maxConcurrent ?? DefaultMaxConcurrent;
        init => _maxConcurrent = value;
    }

    /// <summary>
    ///     How many recent calls the latency estimate needs before a hedge can fire at all. Default 20.
    ///     <para>
    ///         A cold process does not guess a threshold; it waits until it has one. Until then a hedged
    ///         policy behaves exactly like the same policy without <see cref="Hedge" /> configured.
    ///     </para>
    /// </summary>
    public int MinimumSamples
    {
        get => _minimumSamples ?? DefaultMinimumSamples;
        init => _minimumSamples = value;
    }

    /// <summary>
    ///     A floor under the hedge delay. Default 10 ms.
    ///     <para>
    ///         A dependency whose p95 is 300 µs would otherwise have every call hedged after 300 µs,
    ///         which spends the extra traffic on calls that were never slow in any sense a caller cares
    ///         about. The floor is the "do not bother" line.
    ///     </para>
    /// </summary>
    public TimeSpan MinimumDelay
    {
        get => _minimumDelay ?? TimeSpan.FromMilliseconds(10);
        init => _minimumDelay = value;
    }

    /// <summary>
    ///     How much history the latency estimate covers. Default 30 s, the same as
    ///     <see cref="BreakerSettings.TripWindow" />.
    ///     <para>
    ///         Shorter follows a dependency's changing latency sooner and holds fewer samples to estimate
    ///         from; longer is steadier and slower to react. The estimate is at most a quarter of this
    ///         behind the traffic.
    ///     </para>
    /// </summary>
    public TimeSpan Window
    {
        get => _window ?? TimeSpan.FromSeconds(30);
        init => _window = value;
    }

    /// <summary>
    ///     How far towards the breaker's trip point the error rate may climb before hedging is
    ///     suppressed, as a fraction of that trip point. Default <c>0.5</c>.
    ///     <para>
    ///         Gate 3 of hedging is "the breaker is closed", and the gap between closed and healthy is
    ///         enormous: a breaker's default trip is five consecutive failures, so a dependency returning
    ///         errors on 40% of calls sits closed while this process hedges every slow one. Hedging costs
    ///         about <c>1 - Quantile</c> extra load, and load is the last thing a dependency failing that
    ///         often needs. This is the line between "closed" and "healthy enough to hedge".
    ///     </para>
    ///     <para>
    ///         The trip point is the breaker's own, so this number inherits its guardrails: the measured
    ///         baseline that <see cref="BreakerSettings.Failures" /> multiplies, the absolute floor under
    ///         it, and <see cref="BreakerSettings.FailureRatio" /> as the ceiling when it is set. Must be
    ///         in <c>(0, 1]</c>; <c>1</c> suppresses only at the rate that opens the breaker anyway,
    ///         which is how you turn this off.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Needs a <see cref="Resilience.Breaker" /> - it is the object that measures the error rate.
    ///     Without one, nothing is suppressed. The failure mode is stated on the feature page: a
    ///     dependency with one bad shard is both failing and exactly the case hedging routes around, and
    ///     this gate turns hedging off for it. Raise the number, or set it to <c>1</c>, when the errors
    ///     you see are the kind a second attempt answers.
    /// </remarks>
    public double SuppressAt
    {
        get => _suppressAt ?? DefaultSuppressAt;
        init => _suppressAt = value;
    }

    /// <summary>
    ///     The way to configure hedging. There is deliberately no fixed-delay form - see the type's own
    ///     documentation for why that omission is the feature.
    /// </summary>
    /// <param name="quantile">
    ///     The quantile of recent latency to hedge at, between 0.5 and 1 exclusive. This is also the
    ///     extra load: hedging at 0.95 costs about 5%.
    /// </param>
    /// <param name="maxConcurrent">How many attempts may be in flight at once, counting the first.</param>
    /// <returns>The configuration.</returns>
    public static Hedge At(double quantile = 0.95, int maxConcurrent = DefaultMaxConcurrent) =>
        new() { Quantile = quantile, MaxConcurrent = maxConcurrent };

    /// <summary>
    ///     Value equality over the <i>effective</i> configuration, so a hedge that names a default
    ///     explicitly equals one that left it alone.
    /// </summary>
    /// <param name="other">The other configuration.</param>
    /// <returns>True when both would behave identically.</returns>
    public bool Equals(Hedge other) =>
        Quantile.Equals(other.Quantile)
        && MaxConcurrent == other.MaxConcurrent
        && MinimumSamples == other.MinimumSamples
        && MinimumDelay == other.MinimumDelay
        && Window == other.Window
        && SuppressAt.Equals(other.SuppressAt);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Quantile, MaxConcurrent, MinimumSamples, MinimumDelay, Window, SuppressAt);

    /// <inheritdoc />
    public override string ToString() =>
        $"p{Quantile * 100:0.##} (max {MaxConcurrent} in flight, min {MinimumSamples} samples, " +
        $"floor {MinimumDelay.TotalMilliseconds:0.#}ms, window {Window.TotalSeconds:0.#}s, " +
        $"suppressed at {SuppressAt:0.##} of the trip point)";

    /// <summary>
    ///     Collects everything wrong with this configuration, in the shape
    ///     <see cref="Resilience.Validate" /> reports problems.
    /// </summary>
    /// <param name="problems">The list to add to.</param>
    internal void Validate(List<string> problems)
    {
        if (double.IsNaN(Quantile) || Quantile < 0.5 || Quantile >= 1)
        {
            problems.Add(
                $"Hedge.Quantile must be at least 0.5 and less than 1; it is {Quantile}. " +
                "Use Hedge.At(0.95) for the slowest 5% of calls.");
        }

        if (MaxConcurrent < 2)
            problems.Add($"Hedge.MaxConcurrent must be at least 2; it is {MaxConcurrent}. A hedge that cannot overlap anything is not a hedge.");

        if (MinimumSamples < 1)
            problems.Add($"Hedge.MinimumSamples must be at least 1; it is {MinimumSamples}.");

        if (MinimumDelay < TimeSpan.Zero)
            problems.Add($"Hedge.MinimumDelay must not be negative; it is {MinimumDelay}.");

        if (Window <= TimeSpan.Zero)
            problems.Add($"Hedge.Window must be positive; it is {Window}.");

        if (double.IsNaN(SuppressAt) || SuppressAt <= 0 || SuppressAt > 1)
        {
            problems.Add(
                $"Hedge.SuppressAt must be in (0, 1]; it is {SuppressAt}. " +
                "It is a fraction of the breaker's trip point, and 1 is how you turn the suppression off.");
        }
    }
}
