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
    ///     A circuit breaker refused the call: the dependency is unavailable.
    ///     <see cref="CallEvent.Delay" /> carries the pause the refusal serves before it is reported.
    ///     Terminal.
    /// </summary>
    RejectedByBreaker,

    /// <summary>
    ///     The retry budget refused to fund another attempt: this client is retrying too hard.
    ///     <see cref="CallEvent.Delay" /> carries the pause the refusal serves before it is reported.
    ///     Terminal.
    /// </summary>
    RejectedByBudget,

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
    ///         <see cref="RejectedByBreaker" />, <see cref="RejectedByBudget" />,
    ///         <see cref="DeadlineExceeded" /> or this. A listener counting logical
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
    ///     guarded-rejection pause on the two rejection kinds. Null on every other kind.
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
    ///     Why the call stopped, on the terminal kinds - <see cref="CallEventKind.Succeeded" />,
    ///     <see cref="CallEventKind.NotRetried" />, <see cref="CallEventKind.RejectedByBreaker" />,
    ///     <see cref="CallEventKind.RejectedByBudget" /> and
    ///     <see cref="CallEventKind.DeadlineExceeded" />. Null on every other kind, because nothing has
    ///     stopped yet.
    ///     <para>
    ///         The two refusals carry the reason the kind already names -
    ///         <see cref="StopReason.DependencyUnavailable" /> on
    ///         <see cref="CallEventKind.RejectedByBreaker" /> and
    ///         <see cref="StopReason.BudgetExhausted" /> on
    ///         <see cref="CallEventKind.RejectedByBudget" /> - so a listener that switches on
    ///         <see cref="Kind" /> never has to read this field to tell "the dependency is down" from
    ///         "we are retrying too hard".
    ///     </para>
    /// </summary>
    public StopReason? Reason { get; }

    /// <summary>
    ///     True for the two refusals - <see cref="CallEventKind.RejectedByBreaker" /> and
    ///     <see cref="CallEventKind.RejectedByBudget" /> - for a listener that treats them alike.
    /// </summary>
    public bool IsRejection => Kind is CallEventKind.RejectedByBreaker or CallEventKind.RejectedByBudget;

    /// <summary>
    ///     True for the kinds that end a call. Exactly one of these is raised per call.
    /// </summary>
    public bool IsTerminal =>
        Kind is CallEventKind.Succeeded or CallEventKind.NotRetried or CallEventKind.DeadlineExceeded
            or CallEventKind.Exhausted or CallEventKind.RejectedByBreaker or CallEventKind.RejectedByBudget;

    /// <summary>
    ///     Creates a <see cref="CallEvent" /> for testing an <see cref="Resilience.OnEvent" /> listener
    ///     in isolation. The parameters mirror what the executor raises.
    /// </summary>
    /// <param name="kind">What happened.</param>
    /// <param name="policyName">The policy name the listener routes on.</param>
    /// <param name="attemptNumber">The 1-based attempt this event is about.</param>
    /// <param name="verdict">How the most recent attempt was classified.</param>
    /// <param name="duration">The attempt's duration on <see cref="CallEventKind.Attempt" />, the call's on every other kind.</param>
    /// <param name="delay">The pause about to be served, on the kinds that serve one.</param>
    /// <param name="exception">What the most recent attempt threw.</param>
    /// <param name="result">What the most recent attempt returned.</param>
    /// <param name="reason">Why the call stopped, on the terminal kinds.</param>
    /// <returns>The event.</returns>
    /// <remarks>
    ///     The executor uses the constructor directly, so nothing on the hot path routes through this.
    ///     Every parameter but <paramref name="kind" /> is defaulted, so a test names only the fields it
    ///     asserts on.
    /// </remarks>
    public static CallEvent Create(
        CallEventKind kind,
        string? policyName = null,
        int attemptNumber = 1,
        Verdict verdict = default,
        TimeSpan duration = default,
        TimeSpan? delay = null,
        Exception? exception = null,
        object? result = null,
        StopReason? reason = null)
        => new(kind, policyName, attemptNumber, verdict, duration, delay, exception, result, reason);

    /// <inheritdoc />
    public override string ToString()
    {
        var name = PolicyName is null ? string.Empty : $"[{PolicyName}] ";
        var delay = Delay is { } pause ? $" +{pause.TotalMilliseconds:0.#}ms" : string.Empty;
        var error = Exception is null ? string.Empty : $" {Exception.GetType().Name}";

        return $"{name}{Kind} #{AttemptNumber} {Verdict.Kind}{error} ({Duration.TotalMilliseconds:0.#}ms){delay}";
    }
}
