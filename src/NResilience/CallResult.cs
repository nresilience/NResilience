using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace NResilience;

/// <summary>
/// The outcome of a call that was asked not to throw. This is what replaces a fallback strategy:
/// a fallback is an <c>if</c>.
/// </summary>
/// <typeparam name="T">What the callback returns.</typeparam>
public readonly struct CallResult<T>
{
    internal CallResult(bool isSuccess, T? value, bool hasValue, Exception? exception, StopReason stopReason, AttemptLog attempts)
    {
        IsSuccess = isSuccess;
        Value = value;
        HasValue = hasValue;
        Exception = exception;
        StopReason = stopReason;
        Attempts = attempts;
    }

    /// <summary>True when an attempt returned a value the classifier called <see cref="VerdictKind.Ok"/>.</summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// The last value an attempt returned, or <c>default</c> when every attempt threw.
    /// <para>
    /// This is populated even when <see cref="IsSuccess"/> is false, because an answer the policy
    /// judged a failure is still an answer - a final <c>503 HttpResponseMessage</c> is a value the
    /// caller needs, not least so it can be disposed. <see cref="HasValue"/> says whether it is
    /// real.
    /// </para>
    /// </summary>
    public T? Value { get; }

    /// <summary>True when <see cref="Value"/> holds a value an attempt actually returned.</summary>
    public bool HasValue { get; }

    /// <summary>What the last attempt threw, or the deadline exception the library invented.</summary>
    public Exception? Exception { get; }

    /// <summary>Why the call stopped.</summary>
    public StopReason StopReason { get; }

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

    /// <summary>The value, or the failure - rethrown with its original stack intact.</summary>
    /// <returns>The value.</returns>
    public T ValueOrThrow()
    {
        if (IsSuccess)
        {
            return Value!;
        }

        ThrowFailure(Exception, StopReason, Attempts);

        // Unreachable: ThrowFailure is [DoesNotReturn], which the compiler's definite-return
        // analysis does not consult.
        return default!;
    }

    [DoesNotReturn]
    internal static void ThrowFailure(Exception? exception, StopReason stopReason, AttemptLog attempts)
    {
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        throw new CallRejectedException(stopReason, attempts);
    }
}

/// <summary>The void form of <see cref="CallResult{T}"/>. Same members, no value.</summary>
public readonly struct CallResult
{
    internal CallResult(bool isSuccess, Exception? exception, StopReason stopReason, AttemptLog attempts)
    {
        IsSuccess = isSuccess;
        Exception = exception;
        StopReason = stopReason;
        Attempts = attempts;
    }

    /// <summary>True when an attempt completed without a failure verdict.</summary>
    public bool IsSuccess { get; }

    /// <summary>What the last attempt threw, or the deadline exception the library invented.</summary>
    public Exception? Exception { get; }

    /// <summary>Why the call stopped.</summary>
    public StopReason StopReason { get; }

    /// <summary>Everything that happened. Always populated on this type.</summary>
    public AttemptLog Attempts { get; }

    /// <summary>Rethrows the failure, with its original stack intact, if there was one.</summary>
    public void ThrowIfFailed()
    {
        if (!IsSuccess)
        {
            CallResult<bool>.ThrowFailure(Exception, StopReason, Attempts);
        }
    }
}
