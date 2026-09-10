using System.Globalization;
using System.Text;
using NResilience.Testing.Internal;

namespace NResilience.Testing;

/// <summary>
///     What a simulation measured. Every number is a fact about the run, and the same seed produces
///     the same report - which is what makes one of these an assertion rather than an anecdote.
/// </summary>
/// <example>
///     <code>
/// Assert.True(report.LoadMultiplier &lt;= 1.2);
/// </code>
/// </example>
/// <seealso cref="Simulate" />
public sealed class SimulationReport
{
    private readonly int[] _kinds;

    private readonly long[] _latencies;

    internal SimulationReport(
        int seed,
        TimeSpan duration,
        int calls,
        int succeeded,
        int reached,
        double amplification,
        TimeSpan? timeToRecover,
        long[] latencies,
        int[] kinds,
        TimelineEntry[]? timeline)
    {
        Seed = seed;
        Duration = duration;
        Calls = calls;
        Succeeded = succeeded;
        Reached = reached;
        Amplification = amplification;
        TimeToRecover = timeToRecover;

        Timeline = timeline;

        _latencies = latencies;
        _kinds = kinds;

        Array.Sort(_latencies);
    }

    /// <summary>
    ///     The worst one-second window's load multiplier, which is the number the dependency actually
    ///     feels. <see cref="LoadMultiplier" /> averaged over five minutes can look harmless while one
    ///     second of it was three times the offered rate, and the second is what tips a dependency over.
    /// </summary>
    public double Amplification { get; }

    /// <summary>The fraction of calls that ended in success, from 0 to 1.</summary>
    public double Availability => Calls == 0 ? 0 : (double)Succeeded / Calls;

    /// <summary>How many times a breaker tripped during the run.</summary>
    public int BreakerOpens => CountOf(CallEventKind.BreakerOpened);

    /// <summary>How many calls the caller made. Attempts are <see cref="Reached" />.</summary>
    public int Calls { get; }

    /// <summary>How long the run offered load for. Calls in flight at the end were allowed to finish.</summary>
    public TimeSpan Duration { get; }

    /// <summary>
    ///     The engine that produced this report, as its version - <c>1.4.0</c>, or <c>1.4.0-beta.1</c>.
    ///     <para>
    ///         Determinism is a promise about one version of the library. Two reports are comparable
    ///         when this matches; comparing across versions measures the upgrade rather than the
    ///         configuration, which is a useful thing to do deliberately and a misleading thing to do
    ///         by accident. Deliberately absent from <see cref="ToString" />, so the block of text a
    ///         determinism test pins does not change every release.
    ///     </para>
    /// </summary>
    public string EngineVersion { get; } = Engine.Version;

    /// <summary>How many calls did not end in success.</summary>
    public int Failed => Calls - Succeeded;

    /// <summary>
    ///     Attempts that reached the dependency divided by calls the caller made. One is a policy that
    ///     never retried anything; two is a policy that doubled its dependency's traffic.
    ///     <para>
    ///         This is the headline number, and it is the one a dependency's owner asks for. A retry
    ///         budget's whole job is to keep it near one while the dependency is unwell.
    ///     </para>
    /// </summary>
    public double LoadMultiplier => Calls == 0 ? 0 : (double)Reached / Calls;

    /// <summary>Attempts that reached the dependency, across every call.</summary>
    public int Reached { get; }

    /// <summary>The seed the run was drawn from. The same one reproduces this report exactly.</summary>
    public int Seed { get; }

    /// <summary>
    ///     Every event the run raised, in order, each with the virtual time it was raised at - or null
    ///     when the run was not asked to record one.
    ///     <para>
    ///         The counts a report carries say what a run cost. The timeline says how it got there:
    ///         which attempt the breaker opened between, what the backoff actually delayed by, how far
    ///         into the brownout the attempt ceiling adapted. It is byte-identical from a seed the same
    ///         way every other number here is, so it can be asserted on rather than only read.
    ///     </para>
    /// </summary>
    /// <seealso cref="Simulation.Recording" />
    public IReadOnlyList<TimelineEntry>? Timeline { get; }

    /// <summary>How many calls ended in success.</summary>
    public int Succeeded { get; }

    /// <summary>
    ///     How long after the last impairment ended before the caller saw a full second of nothing but
    ///     successes. Null when the dependency was never impaired, or when the run ended before the
    ///     caller recovered - and a null of the second kind is the finding.
    /// </summary>
    public TimeSpan? TimeToRecover { get; }

    /// <summary>
    ///     How many events of one kind the run raised. The whole telemetry surface is available this
    ///     way, so a claim about hedges suppressed or budget refusals is a number rather than an
    ///     argument.
    /// </summary>
    /// <param name="kind">The event kind.</param>
    /// <returns>The count.</returns>
    public int CountOf(CallEventKind kind)
    {
        var index = (int)kind;

        return (uint)index < (uint)_kinds.Length ? _kinds[index] : 0;
    }

    /// <summary>
    ///     Caller-observed latency at a quantile: the whole call, including every retry and every
    ///     backoff it served, which is the number the caller's caller experiences.
    ///     <para>
    ///         Computed over every call in the run by nearest rank, not from a sliding window - a report
    ///         is read once at the end and can afford to keep everything.
    ///     </para>
    /// </summary>
    /// <param name="quantile">The quantile, from 0 to 1.</param>
    /// <returns>The latency, or zero when the run made no calls.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quantile" /> is outside 0 to 1.</exception>
    public TimeSpan Latency(double quantile)
    {
        if (double.IsNaN(quantile) || quantile < 0 || quantile > 1)
            throw new ArgumentOutOfRangeException(nameof(quantile), quantile, "The quantile must be between 0 and 1.");

        if (_latencies.Length == 0)
            return TimeSpan.Zero;

        var rank = (int)Math.Ceiling(quantile * _latencies.Length) - 1;

        return TimeSpan.FromTicks(_latencies[Math.Clamp(rank, 0, _latencies.Length - 1)]);
    }

    /// <summary>
    ///     The report as a fixed block of text, invariant-formatted, one measurement per line. Two runs
    ///     from the same seed produce the same string, which is the cheapest way to assert that a
    ///     simulation is deterministic.
    /// </summary>
    /// <returns>The report.</returns>
    public override string ToString()
    {
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        text.Append(culture, $"Simulation seed {Seed} over {Duration}").AppendLine();
        text.Append(culture, $"  Calls            {Calls}").AppendLine();
        text.Append(culture, $"  Succeeded        {Succeeded}").AppendLine();
        text.Append(culture, $"  Failed           {Failed}").AppendLine();
        text.Append(culture, $"  Availability     {Availability:F4}").AppendLine();
        text.Append(culture, $"  LoadMultiplier   {LoadMultiplier:F3}").AppendLine();
        text.Append(culture, $"  Amplification    {Amplification:F3}").AppendLine();
        text.Append(culture, $"  Latency p50      {Latency(0.50)}").AppendLine();
        text.Append(culture, $"  Latency p99      {Latency(0.99)}").AppendLine();
        text.Append(culture, $"  BreakerOpens     {BreakerOpens}").AppendLine();
        text.Append(culture, $"  TimeToRecover    {TimeToRecover?.ToString() ?? "never"}").AppendLine();

        return text.ToString();
    }
}
