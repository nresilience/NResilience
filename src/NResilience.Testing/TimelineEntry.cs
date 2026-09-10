using System.Globalization;

namespace NResilience.Testing;

/// <summary>
///     One event a simulation raised, and the virtual time it was raised at.
///     <para>
///         The recording a run produces when <see cref="Simulation.Recording" /> asks for one. A
///         <see cref="CallEvent" /> on its own says what happened; the time beside it is what makes a
///         sequence of them a timeline - which attempt the breaker opened between, how long the
///         backoff actually delayed, how far into the brownout the attempt ceiling adapted.
///     </para>
/// </summary>
/// <param name="at">How far into the run the event was raised, on the virtual clock.</param>
/// <param name="callEvent">The event.</param>
/// <example>
///     <code>
/// foreach (var entry in report.Timeline!)
///     Console.WriteLine(entry);
/// </code>
/// </example>
/// <remarks>
///     Deliberately not a record: a <see cref="CallEvent" /> carries an exception and a result, so
///     value equality over one would compare two references and call the answer a measurement. A
///     timeline is read and asserted on line by line, which <see cref="ToString" /> is for.
/// </remarks>
/// <seealso cref="SimulationReport.Timeline" />
public readonly struct TimelineEntry(TimeSpan at, CallEvent callEvent)
{
    /// <summary>How far into the run the event was raised, on the virtual clock.</summary>
    public TimeSpan At { get; } = at;

    /// <summary>The event.</summary>
    public CallEvent Event { get; } = callEvent;

    /// <summary>
    ///     The entry as one line: the virtual time, then the event's own layout. Invariant-formatted,
    ///     so a whole timeline is a fixed block of text that two runs from the same seed agree on.
    /// </summary>
    /// <returns>The line.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{At:hh\\:mm\\:ss\\.fffffff}  {Event}");
}
