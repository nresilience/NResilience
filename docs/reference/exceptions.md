---
title: Exceptions
description: What the library throws, what it rethrows unchanged, and where the attempt log is.
order: 8
---

# Exceptions

When the operation genuinely failed, **the original exception is rethrown unchanged**, through
`ExceptionDispatchInfo`, with its stack intact. `catch (HttpRequestException)` and
`catch (SqlException)` keep working. The library only invents an exception for failures it invented: a
deadline it enforced, a timeout it fired, a call it refused to make.

The attempt log rides along on `Exception.Data`, under `AttemptLog.DataKey`. Read it with
`AttemptLog.Of(exception)`.

## `CallRejectedException`

A call a guard refused. `Exception`.

| Member | Meaning |
| --- | --- |
| `Reason` | `DependencyUnavailable` for an open breaker, `BudgetExhausted` for a depleted budget. |
| `Attempts` | Whatever had already happened. |
| `RetryAfter` | When to come back, when the refusal carried a hint. |

It arrives no sooner than the rejection pause. A rejection reports itself rather than the last
attempt's exception, because the call it describes was never made; that earlier exception is the inner
one.

## `DeadlineExceededException`

The wall-clock budget for the whole call ran out. Derives from `TimeoutException`.

| Member | Meaning |
| --- | --- |
| `Deadline` | The budget that was exceeded. |
| `Attempts` | Everything that happened before it ran out. |

## `AttemptTimeoutException`

One attempt exceeded its own ceiling. Derives from `TimeoutException`.

| Member | Meaning |
| --- | --- |
| `Timeout` | The ceiling the attempt exceeded. |
| `Attempts` | Everything that happened, when this is the exception the call ended on. |

Classified `Transient` by the executor itself, never by a classifier.

## `ResilienceConfigurationException`

A policy, breaker settings or budget parameters that cannot be used.

| Member | Meaning |
| --- | --- |
| `Problems` | **Every** problem found, not just the first. |

Thrown by `Resilience.Validate()`, `BreakerSettings.Validate()`, the `RetryBudget` factories, at DI
registration, and lazily on the first execution of a policy instance.

## Caller cancellation

`OperationCanceledException` from the token you passed in is never retried, never counted, never
converted into a timeout, and never suppressed - including by `TryRunAsync`.

