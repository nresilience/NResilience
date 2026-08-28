namespace NResilience.Internal;

/// <summary>
///     One in-flight attempt of a hedged call, and everything the racing loop needs to cancel it, time
///     it, and clean up after it.
///     <para>
///         A class rather than a struct because it is mutated from two places - the loop marks it
///         discarded, the leg's own body reads that - and because a hedged call allocates a task per leg
///         anyway. Nothing here is on any path a non-hedged call takes.
///     </para>
/// </summary>
/// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
internal sealed class HedgeLeg<T>
{
    /// <summary>
    ///     Set by the loop before it cancels this leg, so the leg's own exception handling can tell
    ///     "somebody else answered first" from "my attempt timeout fired". Volatile because the two are
    ///     on different threads by construction.
    /// </summary>
    private volatile bool _discarded;

    /// <summary>1-based attempt number, as the log and the events report it.</summary>
    internal int Number { get; init; }

    /// <summary>
    ///     Whether this leg was started as a copy of one already in flight. The first leg of a round is
    ///     not a hedge; the ones started alongside it are.
    /// </summary>
    internal bool Hedged { get; init; }

    /// <summary>
    ///     When this leg started, on the policy's clock. Stamped when the leg is created and stamped
    ///     again once its <see cref="Resilience.BeforeAttempt" /> hook has run, so a slow hook is not
    ///     charged to the dependency - the same point the sequential loops measure from.
    /// </summary>
    internal long StartTimestamp { get; set; }

    /// <summary>The ceiling this leg was given, which is <c>min(AttemptTimeout, time left)</c>.</summary>
    internal TimeSpan Effective { get; set; }

    /// <summary>
    ///     Whether that ceiling was a real, cancellable one - which is what makes an overrun measurable
    ///     and therefore reportable as <see cref="CallEventKind.OrphanedWork" />.
    /// </summary>
    internal bool Timed { get; set; }

    /// <summary>The pooled source driving the ceiling, or null when the leg has none. Never handed to user code.</summary>
    internal CancellationTokenSource? Timer { get; set; }

    /// <summary>
    ///     The token the callback receives: the caller's token and the ceiling, linked - and the handle
    ///     the loop cancels to discard this leg.
    ///     <para>
    ///         A hedged leg always has one, even when the policy sets no ceiling at all. That is the one
    ///         way this differs from the sequential loops, where an unbounded attempt is simply handed the
    ///         caller's token: a leg that cannot be cancelled cannot be discarded, and a race whose losers
    ///         run to completion is not a race.
    ///     </para>
    /// </summary>
    internal CancellationTokenSource? Source { get; set; }

    /// <summary>The leg's body, which is what the loop races.</summary>
    internal Task<LegOutcome<T>>? Work { get; set; }

    /// <inheritdoc cref="_discarded" />
    internal bool Discarded
    {
        get => _discarded;
        set => _discarded = value;
    }
}

/// <summary>What one leg came back with. A struct: it is returned once and read once.</summary>
/// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
/// <param name="Verdict">How the outcome was classified.</param>
/// <param name="Error">What it threw, if it threw.</param>
/// <param name="Value">What it returned, if it returned.</param>
/// <param name="HasValue">Whether <paramref name="Value" /> is an answer this leg produced.</param>
/// <param name="Duration">How long the leg ran.</param>
/// <param name="DeadlineSpent">Whether the ceiling that fired was the deadline rather than the attempt timeout.</param>
internal readonly record struct LegOutcome<T>(
    Verdict Verdict,
    Exception? Error,
    T Value,
    bool HasValue,
    TimeSpan Duration,
    bool DeadlineSpent);
