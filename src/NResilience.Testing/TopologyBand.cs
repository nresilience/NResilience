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
/// <seealso cref="Topology.RunAll" />
public sealed class TopologyBand
{
    private readonly TopologyReport[] _reports;

    internal TopologyBand(TopologyReport[] reports)
    {
        _reports = reports;
        Seeds = [.. reports.Select(report => report.Seed)];
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
