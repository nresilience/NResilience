namespace NResilience.Extensions;

/// <summary>
///     How much of a healthy policy's per-call logging to keep, and for how long after an incident to
///     keep all of it.
///     <para>
///         <c>LogSampling.OneIn(20)</c> means "while nothing is wrong, one call in twenty is worth a
///         record; once something is wrong, write everything for a minute". The records worth having are
///         the ones from during an incident, and the cost is the ones from steady state - which is the
///         trade <see cref="ResilienceLogProfile" /> cannot make, because it is chosen once at
///         registration and an incident does not send a redeploy.
///     </para>
/// </summary>
/// <example>
///     <code>
/// services.AddResilienceLogging(o => o.Sampling = LogSampling.OneIn(20));
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>What is sampled.</b> Only the records whose volume is proportional to traffic: the
///         per-attempt records, the per-call records, and the three hedge records. Incidents, first
///         sightings, breaker transitions, the adapted-estimate records and policy resolution are never
///         sampled - they are already one line per event rather than one line per call, and each of them
///         is a fact no other record carries.
///     </para>
///     <para>
///         <b>What opens the window.</b> A breaker opening, and a call refused by a breaker or by the
///         retry budget - the three records that say this process has stopped being healthy and will
///         stop saying so when it recovers. Not the footguns and not the first-sighting exception type:
///         those recur for the life of the process, and a window they hold open is sampling turned off
///         without saying so. The window is opened from the event rather than from the written record,
///         so an incident whose warning the sink is not carrying still turns the sampling off.
///     </para>
///     <para>
///         <b>The failure mode.</b> Sampling drops records; unlike the rejection repeat window it does
///         not demote them, and no count of what it dropped reaches the log. A trace that has to be
///         complete - one call followed attempt by attempt through a reproduction - is not what this is
///         for, and <see cref="KeepOneIn" /> of 1 is the way to turn it off for the run. What it is for
///         is the steady state before the incident, where the metrics already count what the records
///         would have said.
///     </para>
///     <para>
///         Every property but <see cref="KeepOneIn" /> has a working default, so
///         <c>LogSampling.OneIn(20)</c> is a complete configuration and
///         <c>LogSampling.OneIn(20) with { IncidentWindow = ... }</c> is the way to change one. The
///         defaults are supplied on read rather than by a constructor, for the reason
///         <see cref="Hedge" /> gives: a struct's default instance is the one thing a constructor cannot
///         reach.
///     </para>
/// </remarks>
public readonly record struct LogSampling
{
    /// <summary>
    ///     The share of healthy traffic records kept when <see cref="OneIn" /> is called without one.
    ///     One in twenty: enough that a working policy still proves it is working, few enough that the
    ///     steady state costs a twentieth of what it did.
    /// </summary>
    internal const int DefaultKeepOneIn = 20;

    /// <summary>How many of each record are kept in full, when <see cref="MinimumSamples" /> was not set.</summary>
    private const int DefaultMinimumSamples = 20;

    /// <summary>How long an incident keeps everything, when <see cref="IncidentWindow" /> was not set.</summary>
    private static readonly TimeSpan DefaultIncidentWindow = TimeSpan.FromMinutes(1);

    private readonly TimeSpan? _incidentWindow;
    private readonly int? _minimumSamples;

    /// <summary>
    ///     One traffic record in this many is kept while the policy is healthy. <c>20</c> keeps 5%.
    ///     <para>
    ///         Must be at least 1, and 1 is no sampling at all. This is the one number an operator
    ///         supplies, and it is dimensionless on purpose: "a twentieth of the steady state is enough
    ///         to see it working" survives being copied to a dependency with a hundred times the
    ///         traffic, where a records-per-second budget would not.
    ///     </para>
    /// </summary>
    public int KeepOneIn { get; init; }

    /// <summary>
    ///     How long after an incident every record is kept. Default 1 minute.
    ///     <para>
    ///         Measured from the most recent incident record rather than from the first, so a breaker
    ///         that is still refusing calls keeps the window open. <see cref="TimeSpan.Zero" /> samples
    ///         through an incident too, which is the configuration for a process whose log volume is the
    ///         problem being solved.
    ///     </para>
    /// </summary>
    public TimeSpan IncidentWindow
    {
        get => _incidentWindow ?? DefaultIncidentWindow;
        init => _incidentWindow = value;
    }

    /// <summary>
    ///     How many of each record are written in full before sampling starts. Default 20.
    ///     <para>
    ///         The cold-start rule, and here it is what keeps the feature invisible to the people who
    ///         would be hurt by it: a development run, an integration test and a job that makes nine
    ///         calls an hour all log exactly as they did before. Counted per record, so a policy that
    ///         has succeeded ten thousand times still names the first twenty of an exception it has just
    ///         started seeing.
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
    public bool Equals(LogSampling other) =>
        KeepOneIn == other.KeepOneIn
        && IncidentWindow == other.IncidentWindow
        && MinimumSamples == other.MinimumSamples;

    /// <summary>The way to configure log sampling.</summary>
    /// <param name="keepOneIn">One traffic record in this many is kept while the policy is healthy. Must be at least 1.</param>
    /// <returns>The configuration.</returns>
    public static LogSampling OneIn(int keepOneIn = DefaultKeepOneIn) => new() { KeepOneIn = keepOneIn };

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(KeepOneIn, IncidentWindow, MinimumSamples);

    /// <inheritdoc />
    public override string ToString() =>
        $"1 in {KeepOneIn} while healthy (first {MinimumSamples} of each, " +
        $"all of them for {IncidentWindow.TotalSeconds:0.#}s after an incident)";

    /// <summary>
    ///     Collects everything wrong with this configuration, in the shape
    ///     <see cref="Resilience.Validate" /> reports problems.
    /// </summary>
    /// <param name="problems">The list to add to.</param>
    internal void Validate(List<string> problems)
    {
        if (KeepOneIn < 1)
        {
            problems.Add(
                $"LogSampling.KeepOneIn must be at least 1; it is {KeepOneIn}. " +
                "It is how many traffic records one kept record stands for, so 0 is not \"keep none\" - " +
                "use ResilienceLogProfile.Off for that, or 1 for no sampling.");
        }

        if (IncidentWindow < TimeSpan.Zero)
            problems.Add($"LogSampling.IncidentWindow cannot be negative; it is {IncidentWindow}.");

        if (MinimumSamples < 0)
            problems.Add($"LogSampling.MinimumSamples cannot be negative; it is {MinimumSamples}.");
    }
}
