using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace NResilience;

/// <summary>
///     The outcome of a call that was asked not to throw. This is what replaces a fallback strategy:
///     a fallback is an <c>if</c>.
/// </summary>
/// <typeparam name="T">What the callback returns.</typeparam>
public readonly struct CallResult<T>
{
    internal CallResult(bool isSuccess, T? value, bool returnedValue, Exception? exception, StopReason reason, AttemptLog attempts)
    {
        IsSuccess = isSuccess;
        Value = value;
        ReturnedValue = returnedValue;
        Exception = exception;
        Reason = reason;
        Attempts = attempts;
    }

    /// <summary>
    ///     True when the call succeeded: an attempt returned a value the classifier called
    ///     <see cref="VerdictKind.Ok" />. This is the test a fallback branches on.
    ///     <para>
    ///         Not the same question as <see cref="ReturnedValue" />, and the difference is the whole
    ///         reason <c>TryRunAsync</c> exists. The two disagree in exactly one case: the last attempt
    ///         returned a value the classifier refused - a final <c>503</c>, say. There
    ///         <see cref="ReturnedValue" /> is true and this is false.
    ///     </para>
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    ///     The last value an attempt returned, or <c>default</c> when every attempt threw.
    ///     <para>
    ///         This is populated even when <see cref="IsSuccess" /> is false, because an answer the policy
    ///         judged a failure is still an answer - a final <c>503 HttpResponseMessage</c> is a value the
    ///         caller needs, not least so it can be disposed. <see cref="ReturnedValue" /> says whether it
    ///         is real.
    ///     </para>
    /// </summary>
    public T? Value { get; }

    /// <summary>
    ///     True when an attempt got as far as returning something, so <see cref="Value" /> holds what it
    ///     returned rather than <c>default</c>.
    ///     <para>
    ///         Not <c>Nullable&lt;T&gt;.HasValue</c>, and not <see cref="IsSuccess" />. It says an answer
    ///         arrived, not that the answer was good: when the last attempt returned a value the
    ///         classifier refused, this is true and <see cref="IsSuccess" /> is false. Branch on
    ///         <see cref="IsSuccess" /> to decide whether to serve the value; branch on this one to
    ///         decide whether there is something to dispose.
    ///     </para>
    /// </summary>
    public bool ReturnedValue { get; }

    /// <summary>What the last attempt threw, or the deadline exception the library invented.</summary>
    public Exception? Exception { get; }

    /// <summary>Why the call stopped.</summary>
    public StopReason Reason { get; }

    /// <summary>Everything that happened. Always populated on this type.</summary>
    public AttemptLog Attempts { get; }

    /// <summary>The success test most call sites want.</summary>
    /// <param name="value">The value, when the call succeeded.</param>
    /// <returns>True when the call succeeded.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = Value!;
        return IsSuccess;
    }

    /// <summary>
    ///     Rethrows the failure, with its original stack intact, if there was one. The member this
    ///     shares with the void <see cref="CallResult" />, for the case where you want the exception
    ///     but not the value.
    /// </summary>
    public void ThrowIfFailed()
    {
        if (!IsSuccess)
            CallFailure.Rethrow(Exception, Reason, Attempts);
    }

    /// <summary>The value, or the failure - rethrown with its original stack intact.</summary>
    /// <returns>The value.</returns>
    public T ValueOrThrow()
    {
        if (IsSuccess)
            return Value!;

        CallFailure.Rethrow(Exception, Reason, Attempts);

        // Unreachable: Rethrow is [DoesNotReturn], which the compiler's definite-return analysis
        // does not consult.
        return default!;
    }
}

/// <summary>
///     The void form of <see cref="CallResult{T}" />. The same members, minus the four about a value:
///     <see cref="CallResult{T}.Value" />, <see cref="CallResult{T}.ReturnedValue" />,
///     <see cref="CallResult{T}.TryGetValue" /> and <see cref="CallResult{T}.ValueOrThrow" />.
/// </summary>
public readonly struct CallResult
{
    internal CallResult(bool isSuccess, Exception? exception, StopReason reason, AttemptLog attempts)
    {
        IsSuccess = isSuccess;
        Exception = exception;
        Reason = reason;
        Attempts = attempts;
    }

    /// <summary>True when an attempt completed without a failure verdict.</summary>
    public bool IsSuccess { get; }

    /// <summary>What the last attempt threw, or the deadline exception the library invented.</summary>
    public Exception? Exception { get; }

    /// <summary>Why the call stopped.</summary>
    public StopReason Reason { get; }

    /// <summary>Everything that happened. Always populated on this type.</summary>
    public AttemptLog Attempts { get; }

    /// <summary>Rethrows the failure, with its original stack intact, if there was one.</summary>
    public void ThrowIfFailed()
    {
        if (!IsSuccess)
            CallFailure.Rethrow(Exception, Reason, Attempts);
    }
}

/// <summary>
///     Reports the failure a <see cref="CallResult{T}" /> or <see cref="CallResult" /> is carrying.
///     Non-generic because it uses no type parameter, and the void form would otherwise have to reach
///     into an arbitrary instantiation to find it.
/// </summary>
internal static class CallFailure
{
    [DoesNotReturn]
    internal static void Rethrow(Exception? exception, StopReason reason, AttemptLog attempts)
    {
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();

        throw new CallRejectedException(reason, attempts);
    }
}
