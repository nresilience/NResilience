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

    private readonly int? _maxConcurrent;
    private readonly int? _minimumSamples;
    private readonly TimeSpan? _minimumDelay;
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
    ///     <see cref="BreakerSettings.Window" />.
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
        && Window == other.Window;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Quantile, MaxConcurrent, MinimumSamples, MinimumDelay, Window);

    /// <inheritdoc />
    public override string ToString() =>
        $"p{Quantile * 100:0.##} (max {MaxConcurrent} in flight, min {MinimumSamples} samples, floor {MinimumDelay.TotalMilliseconds:0.#}ms, window {Window.TotalSeconds:0.#}s)";

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
    }
}
