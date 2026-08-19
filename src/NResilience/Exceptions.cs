namespace NResilience;

/// <summary>
/// A call that was never attempted, or that a guard stopped part-way through.
/// <para>
/// From Phase 2 this is what a tripped breaker and an exhausted retry budget throw. In Phase 1
/// it is reachable only through <see cref="CallResult{T}.ValueOrThrow"/> on a failure that
/// carried no exception of its own.
/// </para>
/// </summary>
public sealed class CallRejectedException : Exception
{
    /// <summary>Creates a rejection.</summary>
    /// <param name="reason">Why the call was refused.</param>
    /// <param name="attempts">Whatever had already happened.</param>
    /// <param name="retryAfter">A hint for callers that schedule their own polling.</param>
    public CallRejectedException(StopReason reason, AttemptLog attempts, TimeSpan? retryAfter = null)
        : base($"The call was rejected: {reason}.")
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
    public AttemptLog Attempts { get; internal set; } = AttemptLog.Empty;
}

/// <summary>
/// One attempt exceeded its own ceiling. Classified <see cref="VerdictKind.Transient"/> by the
/// executor itself, never by a user predicate — disambiguating the library's own timeout from
/// caller cancellation is the classic bug in timeout implementations, and it is not something a
/// classifier should be able to get wrong.
/// </summary>
public sealed class AttemptTimeoutException : TimeoutException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="timeout">The ceiling the attempt exceeded.</param>
    /// <param name="innerException">The cancellation the timeout produced.</param>
    public AttemptTimeoutException(TimeSpan timeout, Exception? innerException)
        : base($"The attempt exceeded its {timeout.TotalSeconds:0.###}s timeout.", innerException)
        => Timeout = timeout;

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

    /// <summary>Everything that happened, set when this is the exception a call ends on.</summary>
    public AttemptLog Attempts { get; internal set; } = AttemptLog.Empty;
}

/// <summary>
/// A policy that cannot be executed. Lists <b>every</b> problem at once, because fixing
/// configuration one error per run is a tax with no purpose.
/// </summary>
public sealed class ResilienceConfigurationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="problems">Every problem found.</param>
    public ResilienceConfigurationException(IReadOnlyList<string> problems)
        : base(Describe(problems))
        => Problems = problems;

    /// <summary>Creates the exception.</summary>
    public ResilienceConfigurationException()
        : this([])
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public ResilienceConfigurationException(string message)
        : base(message)
        => Problems = [message];

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ResilienceConfigurationException(string message, Exception innerException)
        : base(message, innerException)
        => Problems = [message];

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
