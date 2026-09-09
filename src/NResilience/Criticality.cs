namespace NResilience;

/// <summary>
///     How much this call matters, on the four-level scale Google's overload chapter has used for a
///     decade and Envoy carries through xDS.
///     <para>
///         The library does exactly two things with it, both inside the executor where user code
///         cannot reach: it declines to spend a depleted retry budget on <see cref="Sheddable" />
///         work, and it never hedges it. It does not shed, it does not reorder, and it does not
///         decide what a level means for a given service - that stays with
///         <see cref="Resilience.Admit" />, which can now read a value that traveled with the request
///         rather than guessing one.
///     </para>
/// </summary>
/// <remarks>
///     Ordered from least to most important, so <c>&lt;</c> and <c>&gt;</c> read the way the names do,
///     and byte-backed for the reason <see cref="CallEventKind" /> is: it is stored in an
///     <see cref="AsyncLocal{T}" /> slot and read on the retry path.
/// </remarks>
public enum Criticality : byte
{
    /// <summary>
    ///     Batch, backfill, prefetch. Nobody is waiting, and this is the first work to go when
    ///     something has to.
    /// </summary>
    Sheddable,

    /// <summary>
    ///     Degrades a feature rather than the request: a recommendation strip, a supplementary
    ///     lookup the page renders without.
    /// </summary>
    SheddablePlus,

    /// <summary>
    ///     The default when nothing said, and the ceiling on anything read off the wire. A request a
    ///     user or another service is waiting on.
    /// </summary>
    Critical,

    /// <summary>
    ///     A user is waiting and there is no fallback. Set by local code only -
    ///     <see cref="AmbientCriticality.TryParse" /> clamps an inbound value at
    ///     <see cref="Critical" />, because a caller that could escalate itself would.
    /// </summary>
    CriticalPlus,
}

/// <summary>
///     How much the current logical call matters, and the two halves of putting that across a service
///     boundary: reading a header into an ambient level, and writing one back out.
///     <para>
///         Criticality has to travel, or the service three hops down sheds the checkout and serves the
///         backfill. That is the same argument <see cref="AmbientDeadline" /> makes, and this is
///         deliberately the same shape: an <see cref="AsyncLocal{T}" />, a <see cref="Scope" /> struct
///         to restore it, and an opt-in property on the policy - because reading one is not free and
///         most calls have nothing to read.
///     </para>
/// </summary>
/// <example>
///     <code>
/// // Inbound: the level a caller sent us, for the length of this request.
/// using var scope = AmbientCriticality.Begin(Criticality.Sheddable);
/// 
/// // A policy that opted in will not hedge inside that scope, and will not spend a depleted retry
/// // budget there either.
/// var policy = Resilience.Default with { UseAmbientCriticality = true };
/// </code>
/// </example>
public static class AmbientCriticality
{
    /// <summary>
    ///     The header the HTTP integration reads and writes by default, carrying one of the four
    ///     <see cref="Criticality" /> names.
    /// </summary>
    /// <remarks>
    ///     There is no standard for this on plain HTTP; Envoy carries the equivalent in xDS metadata
    ///     rather than in a header. This name is the one both halves of the library agree on, and both
    ///     halves let you change it.
    /// </remarks>
    public const string Header = "X-Criticality";

    /// <summary>
    ///     One instance per level, so publishing one costs no allocation of its own.
    ///     <para>
    ///         An <see cref="AsyncLocal{T}" /> stores its value as an object, so a bare
    ///         <c>AsyncLocal&lt;Criticality&gt;</c> would box the byte on every
    ///         <see cref="Begin" /> - once per request in the middleware that publishes it. Four
    ///         boxes made once at startup remove that, and the <see cref="ExecutionContext" /> copy
    ///         the assignment itself causes is the only cost left.
    ///     </para>
    /// </summary>
    private static readonly Level[] Levels =
    [
        new(Criticality.Sheddable),
        new(Criticality.SheddablePlus),
        new(Criticality.Critical),
        new(Criticality.CriticalPlus),
    ];

    private static readonly AsyncLocal<Level?> Ambient = new();

    /// <summary>
    ///     How much this call matters. <see cref="Criticality.Critical" /> when nothing said, which is
    ///     the guardrail: a default that sheds is a default that loses requests during the first
    ///     incident after an upgrade.
    /// </summary>
    public static Criticality Current => Ambient.Value?.Value ?? Criticality.Critical;

    /// <summary>
    ///     Publishes a criticality for the current logical call, and everything it awaits.
    /// </summary>
    /// <param name="criticality">How much the call matters.</param>
    /// <returns>A scope that restores the previous ambient criticality when disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="criticality" /> is not one of the four levels.</exception>
    public static Scope Begin(Criticality criticality)
    {
        if (criticality > Criticality.CriticalPlus)
            throw new ArgumentOutOfRangeException(nameof(criticality), criticality, "Criticality must be one of the four declared levels.");

        var previous = Ambient.Value;
        Ambient.Value = Levels[(int)criticality];
        return new Scope(previous);
    }

    /// <summary>
    ///     Reads a criticality header. The format is one of the four <see cref="Criticality" /> names,
    ///     matched without regard to case, and anything else is no criticality at all.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <param name="criticality">The level the caller claimed, clamped at <see cref="Criticality.Critical" />.</param>
    /// <returns>True when <paramref name="value" /> carried a level this side will believe.</returns>
    /// <remarks>
    ///     <b><see cref="Criticality.CriticalPlus" /> cannot arrive from the wire.</b> A caller that
    ///     could escalate itself would escalate itself, and then the level means nothing - so an
    ///     inbound <c>CriticalPlus</c> parses successfully and comes back as
    ///     <see cref="Criticality.Critical" />. Only local code, through <see cref="Begin" />, reaches
    ///     the top level.
    ///     <para>
    ///         Failure is silent, as it is for a deadline header: an unreadable level leaves the call
    ///         at <see cref="Criticality.Critical" />, which is what it would have been without the
    ///         header.
    ///     </para>
    /// </remarks>
    public static bool TryParse(string? value, out Criticality criticality)
    {
        criticality = Criticality.Critical;

        if (string.IsNullOrEmpty(value))
            return false;

        // Four ordinal comparisons rather than Enum.TryParse, which accepts the numeric forms as well
        // - "0" would arrive from the wire as Sheddable, and a header this process did not write is
        // caller-controlled input that should have exactly one spelling.
        if (value.Equals(nameof(Criticality.Sheddable), StringComparison.OrdinalIgnoreCase))
            criticality = Criticality.Sheddable;
        else if (value.Equals(nameof(Criticality.SheddablePlus), StringComparison.OrdinalIgnoreCase))
            criticality = Criticality.SheddablePlus;
        else if (!value.Equals(nameof(Criticality.Critical), StringComparison.OrdinalIgnoreCase)
                 && !value.Equals(nameof(Criticality.CriticalPlus), StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    ///     Writes a criticality header value: the level's name, as
    ///     <see cref="TryParse" /> spells it.
    /// </summary>
    /// <param name="criticality">The level to write.</param>
    /// <returns>The header value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="criticality" /> is not one of the four levels.</exception>
    public static string Format(Criticality criticality) => criticality switch
    {
        Criticality.Sheddable => nameof(Criticality.Sheddable),
        Criticality.SheddablePlus => nameof(Criticality.SheddablePlus),
        Criticality.Critical => nameof(Criticality.Critical),
        Criticality.CriticalPlus => nameof(Criticality.CriticalPlus),
        _ => throw new ArgumentOutOfRangeException(nameof(criticality), criticality, "Criticality must be one of the four declared levels."),
    };

    /// <summary>
    ///     Restores the ambient criticality the scope replaced. A struct, one field wide, for the
    ///     reason <see cref="AmbientDeadline.Scope" /> is: the middleware that publishes a level does
    ///     so on every request.
    /// </summary>
    public readonly struct Scope : IDisposable
    {
        private readonly Level? _previous;

        internal Scope(Level? previous)
        {
            _previous = previous;
        }

        /// <summary>Restores the previous ambient criticality.</summary>
        public void Dispose() => Ambient.Value = _previous;
    }

    /// <summary>One published level. A class because that is what an <see cref="AsyncLocal{T}" /> stores.</summary>
    internal sealed class Level(Criticality value)
    {
        internal Criticality Value { get; } = value;
    }
}
