---
title: CallResult
description: What TryRunAsync hands back, why a call stopped, and the attempt log.
order: 2
---

# `CallResult<T>`

`readonly struct CallResult<T>`. What the `TryRunAsync` overloads return.

| Member | Meaning |
| --- | --- |
| `IsSuccess` | True when an attempt returned a value the classifier called `Ok`. |
| `Value` | The last value an attempt returned, or `default` when every attempt threw. Populated even on failure - a final 503 response is still an answer the caller needs, not least so it can be disposed. |
| `HasValue` | Whether `Value` holds a value an attempt actually returned. |
| `Exception` | What the last attempt threw, or the exception the library invented. |
| `StopReason` | Why the call stopped. |
| `Attempts` | The attempt log. Always populated on this type. |
| `TryGetValue(out T value)` | True when the call succeeded. The success test most call sites want. |
| `ValueOrThrow()` | The value, or the failure rethrown with its original stack intact. |

`CallResult` is the void form: the same members without `Value`, `HasValue` or `TryGetValue`, plus
`ThrowIfFailed()`.

Caller cancellation is the one thing `TryRunAsync` still throws.

This type is what replaces a fallback strategy:

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

| Value | Meaning |
| --- | --- |
| `Succeeded` | An attempt returned a result the classifier called `Ok`. |
| `Permanent` | The outcome was classified `Permanent`, so it was not retried. |
| `AttemptsExhausted` | Every attempt the policy allows was used. |
| `DeadlineExceeded` | The wall-clock budget ran out. |
| `BudgetExhausted` | The retry budget refused to fund another attempt. |
| `DependencyUnavailable` | A circuit breaker refused the call. |

## `AttemptLog`

`sealed class AttemptLog : IReadOnlyList<Attempt>`.

| Member | Meaning |
| --- | --- |
| `Count` | How many attempts ran. |
| `Elapsed` | Wall-clock time from the start of the call to its last attempt returning. |
| `this[int index]` | One attempt. 0-based; `Attempt.Number` is 1-based. |
| `AttemptLog.Empty` | A log with nothing in it. |
| `AttemptLog.Of(Exception)` | The log attached to an exception the library rethrew unchanged, or null. |
| `AttemptLog.DataKey` | `"NResilience.Attempts"` - the `Exception.Data` key `Of` reads. |
| `ToString()` | `3 attempts over 1.2s: Transient IOException (0.2ms), +150ms, Transient IOException (0.3ms), …` |

`RunAsync` materializes the log only when the call is about to fail. `TryRunAsync` always does.

## `Attempt`

`readonly struct`. One completed attempt.

| Member | Meaning |
| --- | --- |
| `Number` | 1-based attempt number. |
| `Duration` | How long the callback ran for. |
| `DelayBefore` | The backoff delay served immediately before this attempt. Zero on the first. |
| `Verdict` | How the outcome was classified. The kind is recorded; `RetryAfter` is not, because it is observable as the next attempt's `DelayBefore`. |
| `Exception` | What this attempt threw, or null when it returned. |
| `Remaining` | Time left on the deadline when this attempt started. |

