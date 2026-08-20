---
title: CallResult
description: Reference for CallResult, StopReason, and AttemptLog.
order: 2
---

# `CallResult<T>`

`CallResult<T>` is a `readonly struct` returned by the `TryRunAsync` overloads. It encapsulates the outcome of a resilience operation and provides access to the attempt history.

| Member | Description |
| :--- | :--- |
| `IsSuccess` | `true` if an attempt returned a value that the classifier identified as `Ok`. |
| `Value` | The value returned by the final attempt, or `default` if every attempt threw an exception. This is populated even on failure - for example, a final `503 Service Unavailable` response is returned so the caller can dispose of it. |
| `HasValue` | `true` if `Value` contains a result actually returned by an attempt. |
| `Exception` | The exception thrown by the last attempt, or a library-specific exception (such as a deadline timeout). |
| `StopReason` | The reason the execution loop stopped. |
| `Attempts` | The log of all attempts made during the call. |
| `TryGetValue(out T value)` | `true` if the call succeeded. This is the recommended method for most call sites to check for success. |
| `ValueOrThrow()` | Returns the value if the call succeeded, otherwise rethrows the failure exception with its original stack trace intact. |

`CallResult` (the non-generic version) provides the same members without `Value`, `HasValue`, or `TryGetValue`, and adds the `ThrowIfFailed()` method.

**Note**: `TryRunAsync` still throws an exception if the caller's `CancellationToken` is cancelled.

### Example: Implement a fallback
You can use the result to provide a fallback value when a resilience operation fails:

<!-- snippet: fallback-is-an-if -->
```csharp
private async Task<User> ReadUserAsync(UserCache cache, CancellationToken cancellationToken)
{
    CallResult<User> result = await Resilience.Http.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);

    if (result.TryGetValue(out User? user))
    {
        return user;
    }

    _logger.LogWarning("Serving the cached user: {Reason} after {Attempts}", result.StopReason, result.Attempts);
    return cache.LastKnownGood;
}
```
<!-- endsnippet -->

## `StopReason`

The `StopReason` enum indicates why the resilience loop stopped executing.

| Value | Meaning |
| :--- | :--- |
| `Succeeded` | An attempt returned a result that the classifier identified as `Ok`. |
| `Permanent` | The outcome was classified as `Permanent`, so the handler did not retry. |
| `AttemptsExhausted` | The maximum number of attempts allowed by the policy was reached. |
| `DeadlineExceeded` | The overall wall-clock budget for the call expired. |
| `BudgetExhausted` | The retry budget refused to fund another attempt. |
| `DependencyUnavailable` | A circuit breaker refused to execute the call. |

## `AttemptLog`

`AttemptLog` is a `sealed class` that implements `IReadOnlyList<Attempt>`.

| Member | Description |
| :--- | :--- |
| `Count` | The number of attempts executed. |
| `Elapsed` | The wall-clock time from the start of the call until the final attempt returned. |
| `this[int index]` | The attempt at the specified 0-based index. |
| `AttemptLog.Empty` | A static instance of an empty log. |
| `AttemptLog.Of(Exception)` | Extracts the log attached to an exception that the library rethrew. |
| `AttemptLog.DataKey` | The `Exception.Data` key used to store the log: `"NResilience.Attempts"`. |
| `ToString()` | Returns a human-readable summary of the attempts and delays. |

While `TryRunAsync` always materializes the log, `RunAsync` only materializes it when a call is about to fail.

## `Attempt`

`Attempt` is a `readonly struct` representing a single completed attempt.

| Member | Description |
| :--- | :--- |
| `Number` | The 1-based index of the attempt. |
| `Duration` | The time taken for the callback to execute. |
| `DelayBefore` | The backoff delay served immediately before this attempt (zero for the first attempt). |
| `Verdict` | The classification of the outcome. The kind is recorded; `RetryAfter` is not, because it is observable as the next attempt's `DelayBefore`. |
| `Exception` | The exception thrown by this attempt, or `null` if it returned a value. |
| `Remaining` | The time remaining on the deadline when the attempt started. |
