using System.Runtime.ExceptionServices;

namespace NResilience.Internal;

/// <summary>
/// Turns the loop's final state into whatever the entry point promised to return.
/// <para>
/// The design document says the throwing and non-throwing entry points must be two fused methods
/// rather than one plus a wrapper, because a wrapper would add a second state-machine box on the
/// suspending path — doubling exactly the overhead this design exists to remove. That is right,
/// and it does not follow that the loop itself must be written twice: with the <i>output</i> type
/// as a second type parameter, one <c>async</c> method serves both, and the shaping happens in a
/// non-<c>async</c> struct that devirtualizes away. Two closed instantiations either way, one
/// loop to keep correct.
/// </para>
/// </summary>
/// <typeparam name="T">What the callback returns.</typeparam>
/// <typeparam name="TOut">What the entry point returns.</typeparam>
internal interface IOutcomeShaper<in T, out TOut>
{
    /// <summary>
    /// Whether the attempt log has to be materialised on the <i>success</i> path. False for the
    /// throwing entry points, so their happy path allocates nothing for a history nobody can read.
    /// The failure path always materialises it.
    /// </summary>
    bool WantsLogOnSuccess { get; }

    /// <summary>Shapes a success.</summary>
    /// <param name="value">What the last attempt returned.</param>
    /// <param name="attempts">The log, or <see cref="AttemptLog.Empty"/> when not wanted.</param>
    /// <returns>The entry point's return value.</returns>
    TOut Success(T value, AttemptLog attempts);

    /// <summary>Shapes a failure, which for a throwing entry point means throwing it.</summary>
    /// <param name="lastValue">The last value an attempt returned, if any.</param>
    /// <param name="hasValue">Whether <paramref name="lastValue"/> is real.</param>
    /// <param name="error">The last exception, if any.</param>
    /// <param name="reason">Why the call stopped.</param>
    /// <param name="deadline">The policy's deadline, for the exception message.</param>
    /// <param name="attempts">The log.</param>
    /// <returns>The entry point's return value.</returns>
    TOut Failure(T lastValue, bool hasValue, Exception? error, StopReason reason, TimeSpan deadline, AttemptLog attempts);
}

/// <summary>
/// The throwing entry points. A failure that produced a value still returns that value: an answer
/// the policy judged a failure is still an answer, and turning a final <c>503</c> into an
/// exception would both surprise the caller and leak the response.
/// </summary>
internal readonly struct ThrowingShaper<T> : IOutcomeShaper<T, T>
{
    public bool WantsLogOnSuccess => false;

    public T Success(T value, AttemptLog attempts) => value;

    public T Failure(T lastValue, bool hasValue, Exception? error, StopReason reason, TimeSpan deadline, AttemptLog attempts)
    {
        if (hasValue)
        {
            return lastValue;
        }

        ExceptionDispatchInfo.Capture(Failures.Build(reason, error, deadline, attempts)).Throw();
        return default!;
    }
}

/// <summary>The non-throwing typed entry points.</summary>
internal readonly struct ResultShaper<T> : IOutcomeShaper<T, CallResult<T>>
{
    public bool WantsLogOnSuccess => true;

    public CallResult<T> Success(T value, AttemptLog attempts) =>
        new(true, value, true, null, StopReason.Succeeded, attempts);

    public CallResult<T> Failure(T lastValue, bool hasValue, Exception? error, StopReason reason, TimeSpan deadline, AttemptLog attempts) =>
        new(false, lastValue, hasValue, hasValue ? null : Failures.Build(reason, error, deadline, attempts), reason, attempts);
}

/// <summary>The non-throwing void entry points.</summary>
internal readonly struct VoidResultShaper : IOutcomeShaper<VoidResult, CallResult>
{
    public bool WantsLogOnSuccess => true;

    public CallResult Success(VoidResult value, AttemptLog attempts) =>
        new(true, null, StopReason.Succeeded, attempts);

    public CallResult Failure(VoidResult lastValue, bool hasValue, Exception? error, StopReason reason, TimeSpan deadline, AttemptLog attempts) =>
        new(false, Failures.Build(reason, error, deadline, attempts), reason, attempts);
}

/// <summary>
/// Decides which exception a failure reports.
/// <para>
/// The library only invents an exception for failures it invented — a deadline it enforced, a
/// timeout it fired, a call it refused to make. When the operation genuinely failed, the original
/// exception is reported unchanged, with the attempt history attached to
/// <see cref="Exception.Data"/> rather than wrapped around it, so <c>catch (HttpRequestException)</c>
/// keeps working.
/// </para>
/// </summary>
internal static class Failures
{
    public static Exception Build(StopReason reason, Exception? error, TimeSpan deadline, AttemptLog attempts)
    {
        if (reason == StopReason.DeadlineExceeded)
        {
            var deadlineExceeded = new DeadlineExceededException(deadline, attempts, error);
            attempts.AttachTo(deadlineExceeded);
            return deadlineExceeded;
        }

        if (error is not null)
        {
            if (error is AttemptTimeoutException timedOut)
            {
                timedOut.Attempts = attempts;
            }

            attempts.AttachTo(error);
            return error;
        }

        return new CallRejectedException(reason, attempts);
    }
}
