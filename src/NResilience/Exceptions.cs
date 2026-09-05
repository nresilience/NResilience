namespace NResilience;

/// <summary>
///     What the library throws to end a call: the attempt log and why it stopped, under one name so a
///     caller does not need a three-arm type switch to reach either.
///     <para>
///         Implemented by <see cref="CallRejectedException" />, <see cref="DeadlineExceededException" />
///         and <see cref="AttemptTimeoutException" /> - the three exceptions that mean "this operation
///         is over". <see cref="RateLimitedException" /> is not one of them: it is thrown by <i>your</i>
///         code, inside an attempt, and is classified and retried like any other failure.
///         <see cref="ResilienceConfigurationException" /> is not one either - it reports a policy that
///         cannot run, which is a startup failure with no call behind it.
///     </para>
///     <para>
///         An interface rather than a base class, because
///         <see cref="DeadlineExceededException" /> and <see cref="AttemptTimeoutException" /> derive
///         from <see cref="TimeoutException" /> on purpose and a caller that catches
///         <see cref="TimeoutException" /> should keep catching them.
///     </para>
///     <para>
///         For an exception the library did <i>not</i> invent - the one your callback threw, which
///         <see cref="Resilience.RunAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)" />
///         rethrows unchanged - use <see cref="AttemptLog.Of(Exception)" />, which reads the log the
///         executor attaches to any exception it lets through.
///     </para>
/// </summary>
/// <example>
///     <code>
/// catch (Exception e) when (e is IResilienceFailure failure)
/// {
///     logger.LogWarning("{Reason} after {Count} attempt(s)", failure.Reason, failure.Attempts.Count);
///     throw;
/// }
/// </code>
/// </example>
public interface IResilienceFailure
{
    /// <summary>Everything that happened before the operation stopped.</summary>
    AttemptLog Attempts { get; }

    /// <summary>Why it stopped.</summary>
    StopReason Reason { get; }
}

/// <summary>
///     An operation that produced no answer anything threw, and no answer the policy would accept.
///     Two shapes, told apart by <see cref="Reason" />:
///     <list type="bullet">
///         <item>
///             <b>A guard refused it</b> - a tripped <see cref="NResilience.Breaker" />
///             (<see cref="StopReason.DependencyUnavailable" />) or an exhausted
///             <see cref="RetryBudget" /> (<see cref="StopReason.BudgetExhausted" />). The call was
///             never attempted, or was stopped part-way through.
///         </item>
///         <item>
///             <b>A verdict stopped it</b> - the classifier or an <see cref="Resilience.Admit" />
///             hook refused every result, and nothing threw
///             (<see cref="StopReason.Permanent" />, <see cref="StopReason.AttemptsExhausted" />).
///             The dependency was reached; what came back was not acceptable. A streaming call
///             whose first element the classifier refused arrives here rather than yielding that
///             element, because an element carries no status of its own and a truncated stream
///             would be indistinguishable from a short successful one.
///         </item>
///     </list>
///     <para>
///         A guard's refusal arrives no sooner than the guarded-rejection pause, which is deliberate: a
///         cheap rejection inside a caller's polling loop is a CPU spin. <see cref="RetryAfter" /> is
///         there so a caller that schedules its own polling does not have to guess. A verdict-driven
///         stop waits for neither, and carries no hint - there is nothing to come back to.
///     </para>
/// </summary>
public sealed class CallRejectedException : Exception, IResilienceFailure
{
    /// <summary>Creates a rejection.</summary>
    /// <param name="reason">Why the call was refused.</param>
    /// <param name="attempts">Whatever had already happened.</param>
    /// <param name="retryAfter">A hint for callers that schedule their own polling.</param>
    /// <param name="innerException">
    ///     What the last attempt threw before the guard stopped the operation, when there was an earlier
    ///     attempt at all. A rejection reports itself rather than that exception, because the call this
    ///     one describes was never made.
    /// </param>
    public CallRejectedException(StopReason reason, AttemptLog attempts, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base($"The call was rejected: {reason}.", innerException)
    {
        Reason = reason;
        Attempts = attempts;
        RetryAfter = retryAfter;
    }

    /// <summary>
    ///     Creates the verdict-driven rejection: nothing refused the call and nothing threw - what
    ///     came back was simply not acceptable. It takes its message rather than composing one,
    ///     because the useful sentence differs by <see cref="StopReason" /> and the caller is what
    ///     knows which. No <see cref="RetryAfter" />: a hint answers "when should I come back",
    ///     and a result the policy refuses is not a question about timing.
    /// </summary>
    /// <param name="reason">Why the operation stopped.</param>
    /// <param name="attempts">Whatever had already happened.</param>
    /// <param name="message">What was refused, in the shape that fits <paramref name="reason" />.</param>
    internal CallRejectedException(StopReason reason, AttemptLog attempts, string message)
        : base(message)
    {
        Reason = reason;
        Attempts = attempts;
    }

    /// <summary>Creates a rejection with a message.</summary>
    /// <param name="message">The message.</param>
    public CallRejectedException(string message)
        : base(message)
    {
        Reason = StopReason.DependencyUnavailable;
        Attempts = AttemptLog.Empty;
    }

    /// <summary>Creates a rejection with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public CallRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = StopReason.DependencyUnavailable;
        Attempts = AttemptLog.Empty;
    }

    /// <summary>Creates a rejection.</summary>
    public CallRejectedException()
        : this("The call was rejected.")
    {
    }

    /// <summary>Why the operation stopped: which guard refused it, or which verdict ended it.</summary>
    public StopReason Reason { get; }

    /// <summary>Whatever had already happened when the operation stopped.</summary>
    public AttemptLog Attempts { get; }

    /// <summary>When to come back, when the refusal carried a hint.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>The wall-clock budget for the whole operation ran out.</summary>
public sealed class DeadlineExceededException : TimeoutException, IResilienceFailure
{
    /// <summary>Creates the exception.</summary>
    /// <param name="deadline">The budget that was exceeded.</param>
    /// <param name="attempts">Everything that happened before it ran out.</param>
    /// <param name="innerException">The last failure, if there was one.</param>
    public DeadlineExceededException(TimeSpan deadline, AttemptLog attempts, Exception? innerException = null)
        : base($"The operation exceeded its {deadline.TotalSeconds:0.###}s deadline after {attempts.Count} attempt(s).", innerException)
    {
        Deadline = deadline;
        Attempts = attempts;
    }

    /// <summary>Creates the exception.</summary>
    public DeadlineExceededException()
        : this(TimeSpan.Zero, AttemptLog.Empty)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public DeadlineExceededException(string message)
        : base(message)
    {
        Attempts = AttemptLog.Empty;
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public DeadlineExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
        Attempts = AttemptLog.Empty;
    }

    /// <summary>The budget that was exceeded.</summary>
    public TimeSpan Deadline { get; }

    /// <summary>Everything that happened before it ran out.</summary>
    public AttemptLog Attempts { get; }

    /// <summary>Always <see cref="StopReason.DeadlineExceeded" />; this exception has one meaning.</summary>
    public StopReason Reason => StopReason.DeadlineExceeded;
}

/// <summary>
///     One attempt exceeded its own ceiling. Classified <see cref="VerdictKind.Transient" /> by the
///     executor itself, never by a user predicate - disambiguating the library's own timeout from
///     caller cancellation is the classic bug in timeout implementations, and it is not something a
///     classifier should be able to get wrong.
/// </summary>
public sealed class AttemptTimeoutException : TimeoutException, IResilienceFailure
{
    /// <summary>Creates the exception.</summary>
    /// <param name="timeout">The ceiling the attempt exceeded.</param>
    /// <param name="innerException">The cancellation the timeout produced.</param>
    public AttemptTimeoutException(TimeSpan timeout, Exception? innerException)
        : base($"The attempt exceeded its {timeout.TotalSeconds:0.###}s timeout.", innerException)
    {
        Timeout = timeout;
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="timeout">The ceiling the attempt exceeded.</param>
    public AttemptTimeoutException(TimeSpan timeout)
        : this(timeout, null)
    {
    }

    /// <summary>Creates the exception.</summary>
    public AttemptTimeoutException()
        : this(TimeSpan.Zero, null)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public AttemptTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AttemptTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The ceiling the attempt exceeded.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    ///     Everything that happened, set when this is the exception a call ends on. Unlike
    ///     <see cref="DeadlineExceededException" />, this exception is constructed while the attempt is
    ///     still running, so the log can only be attached afterwards.
    /// </summary>
    public AttemptLog Attempts { get; internal set; } = AttemptLog.Empty;

    /// <summary>
    ///     Why the call stopped on this timeout, set beside <see cref="Attempts" /> when it is the
    ///     exception a call ends on. Normally <see cref="StopReason.AttemptsExhausted" />: the last
    ///     attempt the policy allowed ran out of time.
    /// </summary>
    /// <remarks>
    ///     Reported rather than derived, because the same exception is also raised for an attempt the
    ///     executor goes on to retry - one that never ends a call keeps the default. A timeout that
    ///     exhausted the whole deadline is a <see cref="DeadlineExceededException" /> instead, so
    ///     <see cref="StopReason.DeadlineExceeded" /> never appears here.
    /// </remarks>
    public StopReason Reason { get; internal set; } = StopReason.AttemptsExhausted;
}

/// <summary>
///     Local admission control refused to start the attempt: a rate limiter, a concurrency limit, or
///     anything else in this process that said no before the call left it. Nothing reached the
///     dependency.
///     <para>
///         Classified <see cref="Verdict.Limited" /> by the executor itself, never by a user predicate, for
///         the same reason <see cref="AttemptTimeoutException" /> is: a classifier that turned this into
///         <see cref="VerdictKind.Transient" /> would feed the breaker evidence about a dependency that was
///         never called, and open a circuit against a healthy service because this process throttled
///         itself.
///     </para>
///     <para>
///         It lives here, in the core package, rather than beside any particular limiter. That is what lets
///         <i>any</i> limiter - the platform's, a distributed one, a hand-rolled semaphore - compose with
///         the backoff curve, the breaker's evidence rule and the retry budget without the core taking a
///         dependency on any of them.
///     </para>
///     <para>
///         This type is not required to get that treatment. Any exception a classifier maps to
///         <see cref="Verdict.Limited" /> reaches the executor's general exception-classification path
///         and is handled identically - see "Building a custom guard" in the admission control deep
///         dive. Throwing this type directly is simply the shortest path when a name like "rate
///         limiter" already fits.
///     </para>
/// </summary>
public sealed class RateLimitedException : Exception
{
    /// <summary>Creates a refusal, with the generated message describing it.</summary>
    /// <param name="retryAfter">When the limiter said a permit would be available, if it said.</param>
    /// <param name="limiter">The limiter that refused, for diagnostics.</param>
    /// <param name="innerException">The cause, when the limiter surfaced one.</param>
    /// <remarks>
    ///     <paramref name="retryAfter" /> comes first so that this overload cannot be reached by a
    ///     single string argument. <c>new RateLimitedException("api")</c> is the message overload
    ///     below and nothing else, which is what a reader of that line expects; name the argument -
    ///     <c>new RateLimitedException(limiter: "api")</c> - to set <see cref="Limiter" />.
    /// </remarks>
    public RateLimitedException(TimeSpan? retryAfter = null, string? limiter = null, Exception? innerException = null)
        : base(Describe(limiter, retryAfter), innerException)
    {
        Limiter = limiter;
        RetryAfter = retryAfter;
    }

    /// <summary>Creates a refusal.</summary>
    public RateLimitedException()
        : this(retryAfter: null)
    {
    }

    /// <summary>Creates a refusal with a message.</summary>
    /// <param name="message">The message.</param>
    public RateLimitedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a refusal with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public RateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The limiter that refused, when it was named. Null otherwise.</summary>
    public string? Limiter { get; }

    /// <summary>When to come back, when the limiter said. Honored verbatim by the backoff.</summary>
    public TimeSpan? RetryAfter { get; }

    private static string Describe(string? limiter, TimeSpan? retryAfter)
    {
        var who = limiter is null ? "A rate limiter" : $"Rate limiter '{limiter}'";

        return retryAfter is { } after
            ? $"{who} refused the attempt; a permit is expected in {after.TotalSeconds:0.###}s."
            : $"{who} refused the attempt.";
    }
}

/// <summary>
///     A policy that cannot be executed. Lists <b>every</b> problem at once, because fixing
///     configuration one error per run is a tax with no purpose.
/// </summary>
public sealed class ResilienceConfigurationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="problems">Every problem found.</param>
    public ResilienceConfigurationException(IReadOnlyList<string> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Creates the exception.</summary>
    public ResilienceConfigurationException()
        : this([])
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public ResilienceConfigurationException(string message)
        : base(message)
    {
        Problems = [message];
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ResilienceConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [message];
    }

    /// <summary>Every problem found, not just the first.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string Describe(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        return problems.Count == 0
            ? "The policy is not valid."
            : "The policy is not valid:" + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", problems);
    }
}
