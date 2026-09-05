namespace NResilience;

/// <summary>
///     What happened. One enum, so a listener can <c>switch</c> on it rather than subscribe to a
///     family of differently-shaped argument types.
/// </summary>
/// <remarks>
///     Byte-backed because it is stored in <see cref="CallEvent" />, which is copied by value into
///     every listener on every event. The backing type of an enum in this library follows where the
///     enum is <i>stored</i>, not whether it is public: the cold ones - <see cref="BreakerState" />,
///     <see cref="Jitter" />, <see cref="BackoffKind" /> - stay <c>int</c>-backed, because they live
///     in per-policy configuration that is copied once. Declaring the width here rather than
///     narrowing at the storage site also makes "a 256th member does not fit" a compile error
///     instead of a test.
/// </remarks>
public enum CallEventKind : byte
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

    /// <summary>
    ///     A second copy of an attempt that had not come back yet has been started.
    ///     <see cref="CallEvent.AttemptNumber" /> is the copy, and <see cref="CallEvent.Delay" /> is the
    ///     latency threshold that triggered it - which is the adaptive quantile, so watching this number
    ///     move during an incident is how you tell a brownout from a tail.
    /// </summary>
    HedgeStarted,

    /// <summary>
    ///     A hedge produced the answer, so the attempt it was started alongside was the slow one and the
    ///     caller saw the shorter of the two. Not terminal: <see cref="Succeeded" /> follows.
    /// </summary>
    HedgeWon,

    /// <summary>
    ///     An attempt was cancelled because a sibling answered first.
    ///     <see cref="CallEvent.Duration" /> is how long it had been running.
    ///     <para>
    ///         This is the cost side of the ledger. Counting these against
    ///         <see cref="HedgeStarted" /> is how you see what the extra load bought.
    ///     </para>
    /// </summary>
    HedgeDiscarded,

    /// <summary>
    ///     The measured per-attempt ceiling moved. <see cref="CallEvent.Delay" /> is the new ceiling, and
    ///     it is the whole output of <see cref="Resilience.AttemptCeiling" /> - nothing else in the process can
    ///     report it.
    ///     <para>
    ///         Raised only when the measured term is what bounds the attempt, and only when the number
    ///         differs from the last one raised for this policy instance, so the rate follows how much
    ///         the estimate moves rather than how much traffic there is. A policy whose ceiling is
    ///         pinned to <see cref="Resilience.AttemptTimeout" /> - because the estimate is cold, or
    ///         because the dependency has slowed until the clamp is what wins - raises nothing, and that
    ///         silence is itself the signal.
    ///     </para>
    /// </summary>
    AttemptCeilingAdapted,

    /// <summary>
    ///     The measured backoff base moved. <see cref="CallEvent.Delay" /> is the new base - the
    ///     measurement after <see cref="MeasuredBase.Spread" /> has clamped it, which is what the curve
    ///     actually uses - and it is the whole output of <see cref="Backoff.MeasuredBase" />.
    ///     <para>
    ///         Raised on the retry decision, and only when the number differs from the last one raised
    ///         for this policy instance, so the rate follows how much the estimate moves rather than how
    ///         many retries there are. A policy whose estimate is still cold, and one whose previous
    ///         attempt was throttled rather than transient, raise nothing.
    ///     </para>
    /// </summary>
    BackoffBaseAdapted,

    /// <summary>
    ///     A call got slow enough to hedge and the hedge was held back.
    ///     <see cref="CallEvent.AttemptNumber" /> is the copy that would have started, and
    ///     <see cref="CallEvent.Delay" /> is the latency threshold that fired - the same two numbers
    ///     <see cref="HedgeStarted" /> carries, so the pair count against each other directly.
    ///     <para>
    ///         Two things raise it, and the arithmetic tells them apart:
    ///         <see cref="Hedge.SuppressAt" /> once the dependency's error rate has climbed towards its
    ///         breaker's trip point, and <see cref="Hedge.WinRate" /> once hedges have stopped winning
    ///         often enough to be worth their load. Neither is a failure - a suppressed hedge is load
    ///         this process decided not to add - but a policy that suppresses most of what it arms is
    ///         one whose <see cref="Hedge" /> is no longer buying anything, and that is worth seeing.
    ///     </para>
    ///     <para>
    ///         Not raised when the retry budget refuses to fund the hedge, and not raised for a hedge
    ///         that was never armed - an open breaker, a concurrency ceiling, or a deadline too close to
    ///         fit another attempt. Those are bounds on the call rather than judgments about hedging.
    ///     </para>
    /// </summary>
    HedgeSuppressed,
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
///     <para>
///         Its size is what a raised event costs to copy, so <see cref="Delay" /> and
///         <see cref="NResilience.Verdict.RetryAfter" /> inside <see cref="Verdict" /> - both of which
///         would naturally be nullable value types - are stored biased-by-one behind properties of the
///         original type. <see cref="Reason" /> needs no such trick: <see cref="StopReason" /> is
///         byte-backed, so <c>StopReason?</c> is two bytes and packs into what would otherwise be
///         padding. The struct measures 64 bytes rather than 88.
///     </para>
/// </remarks>
public readonly struct CallEvent
{
    /// <summary>
    ///     <see cref="Delay" /> in ticks, biased by one so that <c>0</c> means "no pause". The same
    ///     encoding <see cref="NResilience.Verdict" /> uses for its pushback, and for the same reason: a
    ///     <c>TimeSpan?</c> field measures 16 bytes where this measures 8, and this struct is copied by
    ///     value into every listener on every event.
    /// </summary>
    private readonly long _delayPlusOne;

    /// <summary>
    ///     <see cref="Kind" /> and <see cref="Reason" />, declared together and ahead of the reference
    ///     fields on purpose: both are byte-wide - <see cref="StopReason" /> is byte-backed, so
    ///     <c>StopReason?</c> measures two bytes - and the runtime's layout only folds them into the
    ///     padding after <see cref="_delayPlusOne" /> when they are adjacent. Split them across the
    ///     declaration order and the struct measures 72 bytes rather than 64. Measured, not reasoned.
    /// </summary>
    private readonly CallEventKind _kind;

    /// <inheritdoc cref="_kind" />
    private readonly StopReason? _reason;

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
        _kind = kind;
        PolicyName = policyName;
        AttemptNumber = attemptNumber;
        Verdict = verdict;
        Duration = duration;
        _delayPlusOne = delay is { } pause ? Math.Max(pause.Ticks, 0) + 1 : 0;
        Exception = exception;
        Result = result;
        _reason = reason;
    }

    /// <summary>What happened.</summary>
    public CallEventKind Kind => _kind;

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
    public TimeSpan? Delay => _delayPlusOne == 0 ? null : TimeSpan.FromTicks(_delayPlusOne - 1);

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
    public StopReason? Reason => _reason;

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
