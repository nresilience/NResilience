namespace NResilience.Internal;

/// <summary>
///     The outbound half of criticality propagation: the header to write and the level to write in
///     it, both fixed for the whole call.
///     <para>
///         Unlike <see cref="DeadlineStamp" /> there is nothing to recompute per attempt - a level is
///         a statement about the work, and the work does not become less important because its first
///         attempt failed. It is a stamp rather than two strings so the two propagated values travel
///         through <see cref="HttpCall" /> the same way.
///     </para>
/// </summary>
/// <param name="header">The header to write.</param>
/// <param name="value">The level, as <see cref="AmbientCriticality.Format" /> spells it.</param>
internal readonly struct CriticalityStamp(string header, string value)
{
    internal string Header => header;

    internal string Value => value;
}
