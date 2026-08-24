namespace NResilience;

/// <summary>
///     What happened. One enum, so a listener can <c>switch</c> on it rather than subscribe to a
///     family of differently-shaped argument types.
/// </summary>
public enum CallEventKind
{
    /// <summary>
    ///     An attempt finished, whatever the verdict. Exactly one of these fires per attempt, and it
    ///     is the only event whose <see cref="CallEvent.Duration" /> is the attempt's rather than the
    ///     call's.
    /// </summary>
    Attempt,

    /// <summary>
    ///     A retry has been decided on and its backoff is about to be served.
    ///     <see cref="CallEvent.Delay" /> carries the delay and
    ///     <see cref="CallEvent.AttemptNumber" /> is the attempt that is about to run.
    /// </summary>
    Retrying,

    /// <summary>The call succeeded. Terminal.</summary>
    Succeeded,

    /// <summary>
    ///     The outcome was classified <see cref="VerdictKind.Permanent" />, so it was not retried.
    ///     Terminal.
    ///     <para>
    ///         This is the event that makes <see cref="Classifier.Default" /> not retrying an unrecognized
    ///         exception type visible rather than mysterious: <see cref="CallEvent.Exception" /> names the
    ///         type that was not recognized.
    ///     </para>
    /// </summary>
    NotRetried,

    /// <summary>
    ///     A guard refused to make the call: a breaker is open, or the retry budget would not fund the
    ///     retry. <see cref="CallEvent.Delay" /> carries the pause the refusal serves before it is
    ///     reported. Terminal.
    /// </summary>
    Rejected,

    /// <summary>The wall-clock budget for the whole operation ran out. Terminal.</summary>
    DeadlineExceeded,

    /// <summary>
    ///     An attempt timed out and the callback kept running well past it - so the work is still
    ///     going, unobserved, after the policy stopped waiting for it.
    ///     <para>
    ///         This is the single most-hit footgun in the ecosystem, and it is a property of the callback
    ///         rather than of the policy: a timeout cannot kill work that ignores its cancellation token.
    ///         The library cannot fix uncooperative code, but it can tell you which call did it.
    ///     </para>
    /// </summary>
    OrphanedWork,

    /// <summary>A circuit breaker tripped.</summary>
    BreakerOpened,

    /// <summary>A circuit breaker recovered and is taking traffic again.</summary>
    BreakerClosed,

    /// <summary>A circuit breaker's break duration elapsed and this call became its probe.</summary>
    BreakerHalfOpened,

    /// <summary>
    ///     This request is already inside a retrying client, so retrying it again multiplies the load
    ///     on the dependency. Raised by the HTTP handler; nothing else can detect it.
    /// </summary>
    NestedRetry,

    /// <summary>
    ///     The last attempt failed and there were no attempts left. Terminal.
    ///     <para>
    ///         This is the ordinary way a retried call gives up, and it exists so that <i>every</i> call
    ///         ends with exactly one terminal event - <see cref="Succeeded" />, <see cref="NotRetried" />,
    ///         <see cref="Rejected" />, <see cref="DeadlineExceeded" /> or this. A listener counting logical
    ///         operations can only be trusted if the count includes the failures, and those are the calls
    ///         worth counting.
    ///     </para>
    /// </summary>
    Exhausted,
}

/// <summary>
///     One thing that happened during one call. This is the whole telemetry surface: one event type,
///     one delegate, <see cref="Resilience.OnEvent" /> on the policy.
/// </summary>
/// <example>
///     <code>
/// var api = Resilience.Http with
/// {
///     OnEvent = e => logger.LogWarning("{Kind} attempt {N}: {Verdict} in {Ms}ms",
///                                      e.Kind, e.AttemptNumber, e.Verdict.Kind, e.Duration.TotalMilliseconds),
/// };
/// </code>
/// </example>
/// <remarks>
///     A struct passed by value to an <see cref="Action{T}" />, so raising an event allocates nothing -
///     which is what makes leaving a listener attached in production affordable. The one exception is
///     <see cref="Result" />, which boxes a value-type result and is populated only when a listener is
///     actually attached.
/// </remarks>
public readonly struct CallEvent
{
    internal CallEvent(
        CallEventKind kind,
        string? policyName,
        int attemptNumber,
        Verdict verdict,
        TimeSpan duration,
        TimeSpan? delay,
        Exception? exception,
        object? result,
        StopReason? reason)
    {
        Kind = kind;
        PolicyName = policyName;
        AttemptNumber = attemptNumber;
        Verdict = verdict;
        Duration = duration;
        Delay = delay;
        Exception = exception;
        Result = result;
        Reason = reason;
    }

    /// <summary>What happened.</summary>
    public CallEventKind Kind { get; }

    /// <summary><see cref="Resilience.Name" />, so one listener can serve many policies.</summary>
    public string? PolicyName { get; }

    /// <summary>
    ///     1-based. The attempt this event is about - the one that just finished, or, for
    ///     <see cref="CallEventKind.Retrying" /> and the events raised before an attempt runs, the one
    ///     that is about to.
    /// </summary>
    public int AttemptNumber { get; }

    /// <summary>
    ///     How the most recent attempt was classified. <see cref="NResilience.Verdict.Ok" /> when
    ///     nothing has run yet.
    /// </summary>
    public Verdict Verdict { get; }

    /// <summary>
    ///     For <see cref="CallEventKind.Attempt" />, how long that attempt took. For every other kind,
    ///     how long the call has been running.
    ///     <para>
    ///         The rule is deliberately that blunt: every other event is preceded by an
    ///         <see cref="CallEventKind.Attempt" /> event carrying the attempt's own duration, so making
    ///         each terminal event report call-scoped elapsed time adds the number a listener cannot
    ///         otherwise compute instead of repeating one it already has.
    ///     </para>
    /// </summary>
    public TimeSpan Duration { get; }

    /// <summary>
    ///     The pause about to be served: the backoff on <see cref="CallEventKind.Retrying" />, the
    ///     guarded-rejection pause on <see cref="CallEventKind.Rejected" />. Null on every other kind.
    /// </summary>
    public TimeSpan? Delay { get; }

    /// <summary>What the most recent attempt threw, or null when it returned.</summary>
    public Exception? Exception { get; }

    /// <summary>
    ///     What the most recent attempt returned, or null when it threw or returned nothing.
    ///     <para>
    ///         This is the honest answer to "log every retry with the status code that caused it": a
    ///         genuinely cross-cutting listener has no <c>T</c> to be generic over, so the value arrives
    ///         as <see cref="object" />. It boxes value types - and it is populated only when a listener is
    ///         attached, so the cost is zero for everyone who is not asking for it.
    ///     </para>
    /// </summary>
    public object? Result { get; }

    /// <summary>
    ///     Why the call stopped, on the four terminal kinds - <see cref="CallEventKind.Succeeded" />,
    ///     <see cref="CallEventKind.NotRetried" />, <see cref="CallEventKind.Rejected" /> and
    ///     <see cref="CallEventKind.DeadlineExceeded" />. Null on every other kind, because nothing has
    ///     stopped yet.
    ///     <para>
    ///         <see cref="CallEventKind.Rejected" /> covers two different refusals - an open breaker
    ///         (<see cref="StopReason.DependencyUnavailable" />) and an exhausted retry budget
    ///         (<see cref="StopReason.BudgetExhausted" />) - and telling them apart is the difference
    ///         between "the dependency is down" and "we are retrying too hard". A stateless listener
    ///         cannot infer it from the other fields, so the executor states it.
    ///     </para>
    /// </summary>
    public StopReason? Reason { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var name = PolicyName is null ? string.Empty : $"[{PolicyName}] ";
        var delay = Delay is { } pause ? $" +{pause.TotalMilliseconds:0.#}ms" : string.Empty;
        var error = Exception is null ? string.Empty : $" {Exception.GetType().Name}";

        return $"{name}{Kind} #{AttemptNumber} {Verdict.Kind}{error} ({Duration.TotalMilliseconds:0.#}ms){delay}";
    }
}
