using System.Globalization;
using System.Text;

namespace NResilience.Testing;

/// <summary>
///     What a set of seeds measured. The same scenario run several times over, reported as a band per
///     measurement rather than a number per measurement.
///     <para>
///         A report from one seed is a fact about one run. Read as a fact about the configuration it
///         can mislead: two policies compared on seed 42 can swap places on seed 43, and a decision
///         made on the first is noise that has been rounded to a conclusion. Runs cost milliseconds,
///         so there is no reason to decide from one of them.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var band = simulation.RunAll(1, 2, 3, 4, 5);
///
/// // Not "amplification was 1.4x" - "amplification stayed under 2x on every seed".
/// Assert.True(band.Amplification.Maximum &lt; 2.0);
/// </code>
/// </example>
/// <seealso cref="Simulation.RunAll" />
/// <seealso cref="Band" />
public sealed class SimulationBand
{
    private readonly SimulationReport[] _reports;

    internal SimulationBand(SimulationReport[] reports)
    {
        _reports = reports;
        Seeds = [.. reports.Select(report => report.Seed)];
    }

    /// <summary>The worst one-second window's load multiplier, across the seeds.</summary>
    public Band Amplification => Of(report => report.Amplification);

    /// <summary>The fraction of calls that ended in success, across the seeds.</summary>
    public Band Availability => Of(report => report.Availability);

    /// <summary>How many times a breaker tripped, across the seeds.</summary>
    public Band BreakerOpens => Of(report => report.BreakerOpens);

    /// <summary>How many calls the caller made, across the seeds.</summary>
    public Band Calls => Of(report => report.Calls);

    /// <summary>How long each run offered load for. The same for every seed - it is an input.</summary>
    public TimeSpan Duration => _reports[0].Duration;

    /// <summary>
    ///     The engine that produced these reports. The same for every seed, and the version the whole
    ///     band is only comparable to another band within.
    /// </summary>
    public string EngineVersion => _reports[0].EngineVersion;

    /// <summary>Attempts that reached the dependency per call the caller made, across the seeds.</summary>
    public Band LoadMultiplier => Of(report => report.LoadMultiplier);

    /// <summary>
    ///     How many of the runs recovered at all - saw a full second of nothing but successes after the
    ///     last impairment ended. A count below <see cref="Seeds" />.Count is the finding, and the
    ///     reason <see cref="TimeToRecover" /> is a band over the runs that did rather than over all of
    ///     them.
    /// </summary>
    public int Recovered => _reports.Count(report => report.TimeToRecover is not null);

    /// <summary>The individual reports, in the order the seeds were given.</summary>
    public IReadOnlyList<SimulationReport> Reports => _reports;

    /// <summary>The seeds the band was run from, in the order they were given.</summary>
    public IReadOnlyList<int> Seeds { get; }

    /// <summary>
    ///     How long after the last impairment ended before the caller recovered, across the runs that
    ///     did. Null when the dependency was never impaired, or when no run recovered before its end -
    ///     and a null of the second kind, alongside a <see cref="Recovered" /> of zero, is the finding.
    /// </summary>
    public TimeBand? TimeToRecover
    {
        get
        {
            var recovered = _reports
                .Select(report => report.TimeToRecover)
                .OfType<TimeSpan>()
                .Select(time => time.Ticks)
                .ToArray();

            return recovered.Length == 0 ? null : TimeOf(recovered);
        }
    }

    /// <summary>How many events of one kind the runs raised, across the seeds.</summary>
    /// <param name="kind">The event kind.</param>
    /// <returns>The band.</returns>
    public Band CountOf(CallEventKind kind) => Of(report => report.CountOf(kind));

    /// <summary>Caller-observed latency at a quantile, across the seeds.</summary>
    /// <param name="quantile">The quantile, from 0 to 1.</param>
    /// <returns>The band.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quantile" /> is outside 0 to 1.</exception>
    public TimeBand Latency(double quantile) =>
        TimeOf([.. _reports.Select(report => report.Latency(quantile).Ticks)]);

    /// <summary>
    ///     The band as a fixed block of text, invariant-formatted, one measurement per line and the
    ///     same layout <see cref="SimulationReport.ToString" /> uses. The same seeds produce the same
    ///     string.
    /// </summary>
    /// <returns>The band.</returns>
    public override string ToString()
    {
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        text.Append(culture, $"Simulation over {Duration}, {Seeds.Count} seeds from {Seeds[0]}").AppendLine();
        text.Append(culture, $"  Calls            {Calls}").AppendLine();
        text.Append(culture, $"  Availability     {Availability}").AppendLine();
        text.Append(culture, $"  LoadMultiplier   {LoadMultiplier}").AppendLine();
        text.Append(culture, $"  Amplification    {Amplification}").AppendLine();
        text.Append(culture, $"  Latency p50      {Latency(0.50)}").AppendLine();
        text.Append(culture, $"  Latency p99      {Latency(0.99)}").AppendLine();
        text.Append(culture, $"  BreakerOpens     {BreakerOpens}").AppendLine();
        text.Append(culture, $"  TimeToRecover    {TimeToRecover?.ToString() ?? "never"}").AppendLine();
        text.Append(culture, $"  Recovered        {Recovered} of {Seeds.Count}").AppendLine();

        return text.ToString();
    }

    /// <summary>
    ///     The middle value by nearest rank, which is a value one of the runs actually produced rather
    ///     than an average of two that neither did. The same convention
    ///     <see cref="SimulationReport.Latency" /> uses for its quantiles, so a median of medians means
    ///     one thing across the whole report surface.
    /// </summary>
    private static int Middle(int count) => Math.Clamp((int)Math.Ceiling(0.5 * count) - 1, 0, count - 1);

    private static TimeBand TimeOf(long[] ticks)
    {
        Array.Sort(ticks);

        return new TimeBand(
            TimeSpan.FromTicks(ticks[Middle(ticks.Length)]),
            TimeSpan.FromTicks(ticks[0]),
            TimeSpan.FromTicks(ticks[^1]));
    }

    private Band Of(Func<SimulationReport, double> measure)
    {
        var values = new double[_reports.Length];

        for (var i = 0; i < _reports.Length; i++)
            values[i] = measure(_reports[i]);

        Array.Sort(values);

        return new Band(values[Middle(values.Length)], values[0], values[^1]);
    }
}
