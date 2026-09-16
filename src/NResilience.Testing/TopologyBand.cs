namespace NResilience.Testing;

/// <summary>
///     What a set of seeds measured over a graph: a <see cref="SimulationBand" /> per call and per
///     service, rather than a number per call and per service.
///     <para>
///         The argument <see cref="SimulationBand" /> makes, and more of it. A graph has more places
///         for one draw to decide an answer, and the numbers that move most are the ones furthest from
///         the load - a brownout three hops down reaches the entry through two policies that each
///         round the timing differently.
///     </para>
/// </summary>
/// <seealso cref="Topology.RunAll(int[])" />
public sealed class TopologyBand
{
    private readonly TopologyReport[] _reports;

    /// <summary>
    ///     Forms a band over reports the caller already has.
    ///     <para>
    ///         A band is an aggregation over reports and nothing else, so a caller that ran the seeds
    ///         itself - in batches, to answer with the first few while the rest are still running, or
    ///         reusing a seed it had already run - can form the same band <see cref="Topology.RunAll(int[])" />
    ///         would have. Every number is computed from the reports given, so a band assembled this
    ///         way is identical to one run in a single call and is not an approximation of it.
    ///     </para>
    /// </summary>
    /// <param name="reports">The reports, in the order the seeds should be read. At least one, and no repeats.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reports" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="reports" /> is empty, names a seed twice, or mixes reports of different
    ///     graphs - a band whose reports disagree about what the graph contains could not answer
    ///     <see cref="On" /> or <see cref="At" /> for all of them.
    /// </exception>
    public TopologyBand(IEnumerable<TopologyReport> reports)
        : this(Checked(reports))
    {
    }

    private TopologyBand(TopologyReport[] reports)
    {
        _reports = reports;
        Seeds = [.. reports.Select(report => report.Seed)];
    }

    /// <summary>Checks that a caller-assembled set of reports is a band rather than a pile.</summary>
    /// <param name="reports">The reports to check.</param>
    /// <returns>The reports, as an array this band owns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reports" /> is null.</exception>
    /// <exception cref="ArgumentException">The reports are empty, repeat a seed, or describe different graphs.</exception>
    private static TopologyReport[] Checked(IEnumerable<TopologyReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var checkedReports = reports.ToArray();

        if (checkedReports.Length == 0)
            throw new ArgumentException("A band needs at least one report.", nameof(reports));

        if (checkedReports.Distinct().Count() != checkedReports.Length
            || new HashSet<int>(checkedReports.Select(report => report.Seed)).Count != checkedReports.Length)
        {
            throw new ArgumentException(
                "A band cannot hold the same seed twice - the repeat would narrow the band rather than widen it.",
                nameof(reports));
        }

        var first = checkedReports[0];

        foreach (var report in checkedReports)
        {
            if (!report.Edges.SequenceEqual(first.Edges) || !report.Services.SequenceEqual(first.Services))
                throw new ArgumentException("Every report in a band has to come from the same graph.", nameof(reports));
        }

        return checkedReports;
    }

    /// <summary>How long each run offered load for. The same for every seed - it is an input.</summary>
    public TimeSpan Duration => _reports[0].Duration;

    /// <summary>Every call in the graph, in the order they were declared.</summary>
    public IReadOnlyList<Edge> Edges => _reports[0].Edges;

    /// <summary>The engine that produced these reports.</summary>
    public string EngineVersion => _reports[0].EngineVersion;

    /// <summary>The services traffic was offered at.</summary>
    public IReadOnlyList<string> Entries => _reports[0].Entries;

    /// <summary>The individual reports, in the order the seeds were given.</summary>
    public IReadOnlyList<TopologyReport> Reports => _reports;

    /// <summary>The seeds the band was run from, in the order they were given.</summary>
    public IReadOnlyList<int> Seeds { get; }

    /// <summary>Every service and leaf in the graph.</summary>
    public IReadOnlyList<string> Services => _reports[0].Services;

    /// <summary>What one call measured across the seeds.</summary>
    /// <param name="caller">The service making the call.</param>
    /// <param name="callee">The service or leaf being called.</param>
    /// <returns>The band.</returns>
    /// <exception cref="ArgumentException">The graph has no such call.</exception>
    public SimulationBand On(string caller, string callee) =>
        new([.. _reports.Select(report => report.On(caller, callee))]);

    /// <summary>What one service measured across the seeds.</summary>
    /// <param name="service">The service or leaf.</param>
    /// <returns>The band.</returns>
    /// <exception cref="ArgumentException">The graph has no such service.</exception>
    public SimulationBand At(string service) =>
        new([.. _reports.Select(report => report.At(service))]);
}
