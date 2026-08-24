namespace NResilience;

/// <summary>
///     A call that was never attempted, or that a guard stopped part-way through: a tripped
///     <see cref="NResilience.Breaker" /> (<see cref="StopReason.DependencyUnavailable" />) or an
///     exhausted <see cref="RetryBudget" /> (<see cref="StopReason.BudgetExhausted" />).
///     <para>
///         It arrives no sooner than the guarded-rejection pause, which is deliberate: a cheap rejection
///         inside a caller's polling loop is a CPU spin. <see cref="RetryAfter" /> is there so a caller that
///         schedules its own polling does not have to guess.
///     </para>
/// </summary>
public sealed class CallRejectedException : Exception
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

    /// <summary>Why the call was refused.</summary>
    public StopReason Reason { get; }

    /// <summary>Whatever had already happened when the call was refused.</summary>
    public AttemptLog Attempts { get; }

    /// <summary>When to come back, when the refusal carried a hint.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>The wall-clock budget for the whole operation ran out.</summary>
public sealed class DeadlineExceededException : TimeoutException
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
}

/// <summary>
///     One attempt exceeded its own ceiling. Classified <see cref="VerdictKind.Transient" /> by the
///     executor itself, never by a user predicate - disambiguating the library's own timeout from
///     caller cancellation is the classic bug in timeout implementations, and it is not something a
///     classifier should be able to get wrong.
/// </summary>
public sealed class AttemptTimeoutException : TimeoutException
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
/// </summary>
public sealed class RateLimitedException : Exception
{
    /// <summary>Creates a refusal.</summary>
    /// <param name="limiter">The limiter that refused, for diagnostics.</param>
    /// <param name="retryAfter">When the limiter said a permit would be available, if it said.</param>
    /// <param name="innerException">The cause, when the limiter surfaced one.</param>
    public RateLimitedException(string? limiter = null, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(Describe(limiter, retryAfter), innerException)
    {
        Limiter = limiter;
        RetryAfter = retryAfter;
    }

    /// <summary>Creates a refusal.</summary>
    public RateLimitedException()
        : this(null, null, null)
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
