using System.Globalization;

namespace NResilience.Testing;

/// <summary>
///     One measurement across a set of seeds: what it usually is, and how far it moved.
///     <para>
///         A simulation is exact, and a single run of one is still a sample. Arrival gaps, latency
///         draws, failure draws and backoff jitter all come off the seed, so the numbers a tuning
///         decision turns on - the worst second's amplification, the time to recover, how many times
///         the breaker tripped - move from seed to seed even when the averages do not. A band is the
///         honest form of those numbers, and a wide one is the model saying the answer is not robust.
///     </para>
/// </summary>
/// <param name="median">The middle value, by nearest rank - a value the run actually produced.</param>
/// <param name="minimum">The lowest value across the seeds.</param>
/// <param name="maximum">The highest value across the seeds.</param>
/// <remarks>
///     Deliberately not a record: two bands are compared with <see cref="Separates" />, which is the
///     question worth asking, rather than for equality, which on a pair of doubles measured from
///     different runs is a question with no useful answer.
/// </remarks>
/// <seealso cref="SimulationBand" />
public readonly struct Band(double median, double minimum, double maximum)
{
    /// <summary>The highest value across the seeds.</summary>
    public double Maximum { get; } = maximum;

    /// <summary>The middle value, by nearest rank - a value one of the runs actually produced.</summary>
    public double Median { get; } = median;

    /// <summary>The lowest value across the seeds.</summary>
    public double Minimum { get; } = minimum;

    /// <summary>How far the measurement moved across the seeds. Zero is a number the seed does not touch.</summary>
    public double Spread => Maximum - Minimum;

    /// <summary>
    ///     Whether this band and another are far enough apart to call a winner. False when they
    ///     overlap, which is the answer "these two policies are indistinguishable over these seeds" -
    ///     and being able to report that is the whole reason to run more than one.
    /// </summary>
    /// <param name="other">The other band.</param>
    /// <returns>Whether the two bands are disjoint.</returns>
    public bool Separates(Band other) => Minimum > other.Maximum || Maximum < other.Minimum;

    /// <summary>The band as <c>median (minimum-maximum)</c>, invariant-formatted to three decimals.</summary>
    /// <returns>The band.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Median:F3} ({Minimum:F3}-{Maximum:F3})");
}

/// <summary>
///     One duration across a set of seeds. The <see cref="Band" /> of a measurement that is a time
///     rather than a ratio - a latency quantile, or how long the caller took to recover.
/// </summary>
/// <param name="median">The middle value, by nearest rank - a value the run actually produced.</param>
/// <param name="minimum">The shortest value across the seeds.</param>
/// <param name="maximum">The longest value across the seeds.</param>
/// <seealso cref="SimulationBand" />
public readonly struct TimeBand(TimeSpan median, TimeSpan minimum, TimeSpan maximum)
{
    /// <summary>The longest value across the seeds.</summary>
    public TimeSpan Maximum { get; } = maximum;

    /// <summary>The middle value, by nearest rank - a value one of the runs actually produced.</summary>
    public TimeSpan Median { get; } = median;

    /// <summary>The shortest value across the seeds.</summary>
    public TimeSpan Minimum { get; } = minimum;

    /// <summary>How far the measurement moved across the seeds.</summary>
    public TimeSpan Spread => Maximum - Minimum;

    /// <summary>Whether this band and another are far enough apart to call a winner.</summary>
    /// <param name="other">The other band.</param>
    /// <returns>Whether the two bands are disjoint.</returns>
    public bool Separates(TimeBand other) => Minimum > other.Maximum || Maximum < other.Minimum;

    /// <summary>The band as <c>median (minimum-maximum)</c>.</summary>
    /// <returns>The band.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Median} ({Minimum}-{Maximum})");
}
