using System.Collections;
using System.Text;

namespace NResilience;

/// <summary>Why a call stopped.</summary>
public enum StopReason
{
    /// <summary>An attempt returned a result the classifier called <see cref="VerdictKind.Ok"/>.</summary>
    Succeeded,

    /// <summary>The outcome was classified <see cref="VerdictKind.Permanent"/>, so it was not retried.</summary>
    Permanent,

    /// <summary>Every attempt the policy allows was used.</summary>
    AttemptsExhausted,

    /// <summary>The wall-clock budget for the whole operation ran out.</summary>
    DeadlineExceeded,

    /// <summary>The retry budget refused to fund another attempt.</summary>
    BudgetExhausted,

    /// <summary>A circuit breaker refused the call.</summary>
    DependencyUnavailable,
}

/// <summary>
/// An attempt that has completed. This is what an <see cref="AttemptLog"/> contains.
/// <para>
/// A completed attempt has a duration and a verdict; an attempt that has not run yet has neither.
/// The two are separate types (<see cref="NextAttempt"/> is the other) so that neither carries
/// fields that are meaningless half the time.
/// </para>
/// </summary>
public readonly struct Attempt
{
    internal Attempt(int number, TimeSpan duration, TimeSpan delayBefore, Verdict verdict, Exception? exception, TimeSpan remaining)
    {
        Number = number;
        Duration = duration;
        DelayBefore = delayBefore;
        Verdict = verdict;
        Exception = exception;
        Remaining = remaining;
    }

    /// <summary>1-based attempt number.</summary>
    public int Number { get; }

    /// <summary>How long the callback ran for.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The backoff delay served immediately before this attempt. Zero on the first.</summary>
    public TimeSpan DelayBefore { get; }

    /// <summary>
    /// How the outcome was classified.
    /// <para>
    /// The kind is recorded; <see cref="NResilience.Verdict.RetryAfter"/> is not. The inline log
    /// stores 16 bytes per attempt and server pushback is already observable as the
    /// <see cref="DelayBefore"/> of the attempt that followed it.
    /// </para>
    /// </summary>
    public Verdict Verdict { get; }

    /// <summary>The exception this attempt threw, or null when it returned.</summary>
    public Exception? Exception { get; }

    /// <summary>Time left on the deadline when this attempt started, or <see cref="Timeout.InfiniteTimeSpan"/>.</summary>
    public TimeSpan Remaining { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        Exception is null
            ? $"#{Number} {Verdict.Kind} ({Duration.TotalMilliseconds:0.#}ms)"
            : $"#{Number} {Verdict.Kind} {Exception.GetType().Name} ({Duration.TotalMilliseconds:0.#}ms)";
}

/// <summary>
/// An attempt that is about to happen. What <see cref="Resilience.BeforeAttempt"/> and
/// <see cref="Backoff.Custom"/> receive.
/// </summary>
public readonly struct NextAttempt
{
    internal NextAttempt(int number, Verdict previousVerdict, Exception? previousException, TimeSpan remaining, CancellationToken cancellationToken)
    {
        Number = number;
        PreviousVerdict = previousVerdict;
        PreviousException = previousException;
        Remaining = remaining;
        CancellationToken = cancellationToken;
    }

    /// <summary>1-based; 1 on the first attempt, before anything has failed.</summary>
    public int Number { get; }

    /// <summary>How the previous attempt was classified. <see cref="Verdict.Ok"/> on the first.</summary>
    public Verdict PreviousVerdict { get; }

    /// <summary>What the previous attempt threw, if anything.</summary>
    public Exception? PreviousException { get; }

    /// <summary>Time left on the deadline, or <see cref="Timeout.InfiniteTimeSpan"/> when unbounded.</summary>
    public TimeSpan Remaining { get; }

    /// <summary>The caller's token. Cancelling it aborts the operation.</summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// Everything that happened during one call.
/// <para>
/// <see cref="Resilience.RunAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>
/// materializes this only when the call is about to fail; the various
/// <c>TryRunAsync</c> overloads always materialize it, because their caller has explicitly asked
/// for a result object and a log that vanished on success would make "assert this succeeded on
/// the third attempt" impossible to write.
/// </para>
/// </summary>
/// <remarks>
/// A class implementing <see cref="IReadOnlyList{T}"/> rather than a struct over a
/// <see cref="ReadOnlySpan{T}"/>: a struct holding a span is a <c>ref struct</c>, and a
/// <c>ref struct</c> cannot be a generic type argument, cannot appear in
/// <c>ValueTask&lt;CallResult&lt;T&gt;&gt;</c>, and cannot live across an <c>await</c>.
/// </remarks>
public sealed class AttemptLog : IReadOnlyList<Attempt>
{
    /// <summary>
    /// The key under which a log is attached to a rethrown original exception's
    /// <see cref="Exception.Data"/>. See <see cref="Of(Exception)"/>.
    /// </summary>
    public const string DataKey = "NResilience.Attempts";

    private readonly Attempt[] _attempts;

    internal AttemptLog(Attempt[] attempts, TimeSpan elapsed)
    {
        _attempts = attempts;
        Elapsed = elapsed;
    }

    /// <summary>A log with nothing in it.</summary>
    public static AttemptLog Empty { get; } = new([], TimeSpan.Zero);

    /// <summary>How many attempts ran.</summary>
    public int Count => _attempts.Length;

    /// <summary>Wall-clock time from the start of the call to its last attempt returning.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>One attempt.</summary>
    /// <param name="index">0-based index. <see cref="Attempt.Number"/> is 1-based.</param>
    /// <returns>The attempt.</returns>
    public Attempt this[int index] => _attempts[index];

    /// <summary>
    /// The log attached to an exception the library rethrew unchanged.
    /// <para>
    /// When the operation genuinely failed, the original exception is rethrown as it was, so
    /// <c>catch (HttpRequestException)</c> keeps working. The history is attached to
    /// <see cref="Exception.Data"/> rather than wrapped around it, and this reads it back.
    /// </para>
    /// </summary>
    /// <param name="exception">The exception that came out of a call.</param>
    /// <returns>The log, or null when the exception did not come from this library.</returns>
    public static AttemptLog? Of(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // `as` covers both the absent key and a key somebody else put there under this name.
        return exception.Data[DataKey] as AttemptLog;
    }

    /// <inheritdoc/>
    public IEnumerator<Attempt> GetEnumerator() => ((IEnumerable<Attempt>)_attempts).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _attempts.GetEnumerator();

    /// <inheritdoc/>
    public override string ToString()
    {
        if (_attempts.Length == 0)
        {
            return "no attempts";
        }

        var text = new StringBuilder();
        text.Append(_attempts.Length).Append(_attempts.Length == 1 ? " attempt over " : " attempts over ");
        text.Append(Format(Elapsed)).Append(':').Append(' ');

        for (var i = 0; i < _attempts.Length; i++)
        {
            var attempt = _attempts[i];
            if (i > 0)
            {
                text.Append(", +").Append(Format(attempt.DelayBefore)).Append(", ");
            }

            text.Append(attempt.Verdict.Kind);
            if (attempt.Exception is not null)
            {
                text.Append(' ').Append(attempt.Exception.GetType().Name);
            }

            text.Append(" (").Append(Format(attempt.Duration)).Append(')');
        }

        return text.ToString();
    }

    internal void AttachTo(Exception exception) => exception.Data[DataKey] = this;

    private static string Format(TimeSpan value) =>
        value.TotalSeconds >= 1
            ? $"{value.TotalSeconds:0.##}s"
            : $"{value.TotalMilliseconds:0.#}ms";
}
