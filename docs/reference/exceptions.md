---
title: Exceptions
description: Reference for the exceptions thrown by NResilience and how to access the attempt log.
order: 8
---

# Exceptions

When a resilience operation fails, NResilience rethrows the original exception unchanged with `ExceptionDispatchInfo`. The original stack trace survives, so standard catch blocks - `catch (HttpRequestException)`, `catch (SqlException)` - keep working.

The library only introduces new exception types for failures it generates, such as deadlines it enforces or calls it refuses to make.

### `IResilienceFailure`

The three exceptions that mean "this operation is over" - `CallRejectedException`, `DeadlineExceededException`, and `AttemptTimeoutException` - implement `IResilienceFailure`, so one catch reaches the attempt log and the reason without a type switch:

```csharp
catch (Exception e) when (e is IResilienceFailure failure)
{
    logger.LogWarning("{Reason} after {Count} attempt(s)", failure.Reason, failure.Attempts.Count);
    throw;
}
```

| Member | Description |
| :--- | :--- |
| `Attempts` | Everything that happened before the operation stopped. |
| `Reason` | The `StopReason` the executor decided on. |

It is an interface rather than a base class because two of the three derive from `TimeoutException` on purpose, and a caller that catches `TimeoutException` should keep catching them.

`RateLimitedException` is deliberately not one: it is thrown by *your* code, inside an attempt, and is classified and retried like any other failure. `ResilienceConfigurationException` is not one either - it reports a policy that cannot run, which is a startup failure with no call behind it.

### Accessing the attempt log
For any exception thrown by the library - including the one your callback threw, which the executor rethrows unchanged - the attempt log is stored in `Exception.Data` under the `AttemptLog.DataKey`. Retrieve it with `AttemptLog.Of(exception)`. That is the general mechanism; `IResilienceFailure` is the typed one for the three exceptions the library invents to end a call.

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

A `DeadlineExceededException` is thrown when the whole call's wall-clock budget expires. It derives from `TimeoutException`.

| Member | Description |
| :--- | :--- |
| `Deadline` | The budget that was exceeded. |
| `Attempts` | All attempts that occurred before the deadline was reached. |
| `Reason` | Always `DeadlineExceeded`; this exception has one meaning. |

## `AttemptTimeoutException`

An `AttemptTimeoutException` is thrown when a single attempt exceeds its ceiling. It derives from `TimeoutException`.

| Member | Description |
| :--- | :--- |
| `Timeout` | The ceiling that the attempt exceeded. |
| `Attempts` | The complete attempt log, if this was the final exception of the call. |
| `Reason` | Why the call stopped, when this was the exception it stopped on. Normally `AttemptsExhausted`: the last attempt the policy allowed ran out of time. A timeout that spent the whole deadline is a `DeadlineExceededException` instead, so `DeadlineExceeded` never appears here. |

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

This exception comes from `Resilience.Validate()`, `BreakerSettings.Validate()`, `HttpResilienceOptions.Validate()`, `GrpcResilienceOptions.Validate()`, and the `RetryBudget` factories. Each has a `Validated()` companion that runs the check and returns the receiver, so a `static readonly` field fails where it is written. It can also be thrown during DI registration or lazily on a policy instance's first execution.

## Caller cancellation

If the `CancellationToken` you provided is cancelled, an `OperationCanceledException` is thrown. It is never retried, counted as an attempt, converted into a timeout, or suppressed - even with `TryRunAsync`.

## Mapping to HTTP responses

In an ASP.NET Core app, one registration maps the four exceptions the library invents to the HTTP responses they mean - 504 for the timeouts, 503 with `Retry-After` for the refusals - with no try/catch per endpoint. See [Error responses](../http/error-responses.md) for the mapping table, the problem-document body, and the status code options.
