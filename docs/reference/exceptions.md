---
title: Exceptions
description: Reference for the exceptions thrown by NResilience and how to access the attempt log.
order: 8
---

# Exceptions

When a resilience operation fails, NResilience rethrows the original exception unchanged using `ExceptionDispatchInfo`. This preserves the original stack trace, ensuring that standard catch blocks - such as `catch (HttpRequestException)` or `catch (SqlException)` - continue to work as expected.

The library only introduces new exception types for failures it generates, such as deadlines it enforces or calls it refuses to make.

### Accessing the attempt log
For any exception thrown by the library, the attempt log is stored in `Exception.Data` under the `AttemptLog.DataKey`. You can retrieve this log using `AttemptLog.Of(exception)`.

## `CallRejectedException`

A `CallRejectedException` covers two shapes, told apart by `Reason`.

**A guard refused the call.** An open circuit breaker or a depleted retry budget stopped it, and it arrives no sooner than the [rejection pause](../deep-dives/guarded-rejection.md). `RetryAfter` carries a hint when the guard supplied one.

**A verdict stopped it.** Nothing threw and no guard intervened - the classifier or an `Admit` hook refused what came back. The dependency was reached; the answer was not acceptable. A streaming call whose first element the classifier refused arrives here rather than yielding that element, because an element carries no status of its own and a truncated stream would be indistinguishable from a short successful one. There is no `RetryAfter` on this shape: a refused result is not a question about timing.

| Member | Description |
| :--- | :--- |
| `Reason` | Why the operation stopped: `DependencyUnavailable` for an open circuit breaker, `BudgetExhausted` for a depleted retry budget, `Permanent` when the result was classified permanent and so was not retried, or `AttemptsExhausted` when the attempts ran out on results the policy kept refusing. |
| `Attempts` | The history of attempts that occurred before the operation stopped. |
| `RetryAfter` | A hint indicating when the caller should retry the operation, when a guard supplied one. Always null for a verdict-driven stop. |

Because a guard's rejected call was never made, the exception reports the rejection itself; the exception from the previous attempt, if there was one, is contained as the inner exception.

## `DeadlineExceededException`

A `DeadlineExceededException` occurs when the overall wall-clock budget for the entire call expires. This exception derives from `TimeoutException`.

| Member | Description |
| :--- | :--- |
| `Deadline` | The budget that was exceeded. |
| `Attempts` | All attempts that occurred before the deadline was reached. |

## `AttemptTimeoutException`

An `AttemptTimeoutException` is thrown when a single attempt exceeds its specific time ceiling. This exception derives from `TimeoutException`.

| Member | Description |
| :--- | :--- |
| `Timeout` | The ceiling that the attempt exceeded. |
| `Attempts` | The complete attempt log, if this was the final exception of the call. |

The [executor](index.md) always classifies `AttemptTimeoutException` as `Transient`, regardless of the configured classifier.

## `RateLimitedException`

A `RateLimitedException` is thrown when local admission control refuses to start an attempt. Nothing reached the dependency.

| Member | Description |
| :--- | :--- |
| `Limiter` | The name of the limiter that refused, or `null` if it was unnamed. |
| `RetryAfter` | When the limiter said a permit would be available, if it said. Honored over the backoff curve. |

The [executor](index.md) always classifies it as `Verdict.Limited`, regardless of the configured classifier - so it is retried on the throttled curve, is never counted against the breaker, and is never charged to the [retry budget](../features/retry-budget.md). Because the executor handles it directly, a `Classifier` never sees it; calling `ClassifyException` with one returns `Permanent`.

Throw it yourself from any limiter you bring, and it composes the same way. See [Rate limiting](../features/rate-limiting.md).

## `ResilienceConfigurationException`

A `ResilienceConfigurationException` is thrown when a policy, breaker setting, or budget parameter is invalid.

| Member | Description |
| :--- | :--- |
| `Problems` | A collection of all configuration problems found. |

This exception is thrown by `Resilience.Validate()`, `BreakerSettings.Validate()`, and the `RetryBudget` factories. It may also be thrown during DI registration or lazily during the first execution of a policy instance.

## Caller cancellation

If the `CancellationToken` you provided is cancelled, an `OperationCanceledException` is thrown. This exception is never retried, counted as an attempt, converted into a timeout, or suppressed - even when using `TryRunAsync`.

## Mapping to HTTP responses

In an ASP.NET Core app, the four exceptions the library invents map to the HTTP responses they mean - 504 for the timeouts, 503 with `Retry-After` for the refusals - via one registration, with no try/catch per endpoint. For the mapping table, the problem-document body, and the status code options, see [Error responses](../http/error-responses.md).
