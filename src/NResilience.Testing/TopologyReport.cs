using System.Globalization;
using System.Text;
using NResilience.Testing.Internal;

namespace NResilience.Testing;

/// <summary>
///     What a graph run measured: one report per call, and one per service.
///     <para>
///         A single-dependency run has one answer because it has one edge. A graph has an answer per
///         edge, and the interesting finding is almost always the gap between two of them - the bank
///         saw 1.9 attempts per call while the checkout saw 99.4% availability, or the checkout fell
///         over while every individual policy behaved exactly as configured.
///     </para>
/// </summary>
/// <seealso cref="Topology" />
public sealed class TopologyReport
{
    private readonly IReadOnlyDictionary<Edge, SimulationReport> _edges;

    private readonly IReadOnlyDictionary<string, SimulationReport> _services;

    internal TopologyReport(
        int seed,
        TimeSpan duration,
        IReadOnlyList<Edge> edges,
        IReadOnlyList<string> entries,
        IReadOnlyList<string> services,
        IReadOnlyDictionary<Edge, SimulationReport> edgeReports,
        IReadOnlyDictionary<string, SimulationReport> serviceReports)
    {
        Seed = seed;
        Duration = duration;
        Edges = edges;
        Entries = entries;
        Services = services;

        _edges = edgeReports;
        _services = serviceReports;
    }

    /// <summary>How long the run offered load for.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Every call in the graph, in the order they were declared.</summary>
    public IReadOnlyList<Edge> Edges { get; }

    /// <summary>The engine that produced this report. Determinism is a promise about one version.</summary>
    public string EngineVersion { get; } = Engine.Version;

    /// <summary>The services traffic was offered at.</summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>Every service and leaf in the graph, callers before the leaves they call.</summary>
    public IReadOnlyList<string> Services { get; }

    /// <summary>The seed the run was drawn from. The same one reproduces this report exactly.</summary>
    public int Seed { get; }

    /// <summary>
    ///     What one call measured, as an ordinary <see cref="SimulationReport" />.
    ///     <para>
    ///         <see cref="SimulationReport.Calls" /> is how many times the caller invoked this call,
    ///         <see cref="SimulationReport.Reached" /> is how many attempts got to the callee, and
    ///         <see cref="SimulationReport.LoadMultiplier" /> between them is the number the callee's
    ///         owner asks for.
    ///     </para>
    /// </summary>
    /// <param name="caller">The service making the call.</param>
    /// <param name="callee">The service or leaf being called.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentException">The graph has no such call.</exception>
    public SimulationReport On(string caller, string callee) =>
        _edges.TryGetValue(new Edge(caller, callee), out var report)
            ? report
            : throw new ArgumentException($"The topology has no call from '{caller}' to '{callee}'.", nameof(caller));

    /// <summary>
    ///     What one service measured, as an ordinary <see cref="SimulationReport" />.
    ///     <para>
    ///         <see cref="SimulationReport.Calls" /> is how many requests it served, from load offered
    ///         at it and from attempts that reached it, and <see cref="SimulationReport.Availability" />
    ///         is the fraction it answered. <see cref="SimulationReport.Reached" /> is how many attempts
    ///         it sent downstream across every call it makes, so its
    ///         <see cref="SimulationReport.LoadMultiplier" /> is fan-out and retries together - what
    ///         this service costs the rest of the graph per request it is asked to serve. A leaf sends
    ///         nothing on, so its multiplier is one.
    ///     </para>
    /// </summary>
    /// <param name="service">The service or leaf.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentException">The graph has no such service.</exception>
    public SimulationReport At(string service) =>
        _services.TryGetValue(service, out var report)
            ? report
            : throw new ArgumentException($"The topology has no service called '{service}'.", nameof(service));

    /// <summary>
    ///     The whole run as a fixed block of text, invariant-formatted: every entry, then every call.
    ///     Two runs from the same seed produce the same string.
    /// </summary>
    /// <returns>The report.</returns>
    public override string ToString()
    {
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        text.Append(culture, $"Topology seed {Seed} over {Duration}").AppendLine();

        foreach (var entry in Entries)
        {
            var report = At(entry);

            text.Append(culture, $"  {entry}").AppendLine();
            text.Append(culture, $"    Requests       {report.Calls}").AppendLine();
            text.Append(culture, $"    Availability   {report.Availability:F4}").AppendLine();
            text.Append(culture, $"    Latency p50    {report.Latency(0.50)}").AppendLine();
            text.Append(culture, $"    Latency p99    {report.Latency(0.99)}").AppendLine();
        }

        foreach (var edge in Edges)
        {
            var report = On(edge.Caller, edge.Callee);

            text.Append(culture, $"  {edge}").AppendLine();
            text.Append(culture, $"    Calls          {report.Calls}").AppendLine();
            text.Append(culture, $"    Availability   {report.Availability:F4}").AppendLine();
            text.Append(culture, $"    LoadMultiplier {report.LoadMultiplier:F3}").AppendLine();
            text.Append(culture, $"    Amplification  {report.Amplification:F3}").AppendLine();
            text.Append(culture, $"    BreakerOpens   {report.BreakerOpens}").AppendLine();
            text.Append(culture, $"    TimeToRecover  {report.TimeToRecover?.ToString() ?? "never"}").AppendLine();
        }

        return text.ToString();
    }
}
