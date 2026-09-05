---
title: Classifier and verdicts
description: Reference for the Classifier class, the Verdict structure, and how different outcomes are categorized.
order: 3
---

# `Classifier`

The `Classifier` is a `sealed class` that categorizes the outcome of an attempt. It is immutable: every modification returns a new instance.

| Member | Description |
| :--- | :--- |
| `Classifier.Default` | Classifies `TimeoutException`, `IOException`, and `SocketException` as `Transient`. All other exceptions are `Permanent`. |
| `Classifier.Http` | Extends `Default` by adding `HttpRequestException` as `Transient` and including a status-code rule for `HttpResponseMessage`. |
| `Classifier.Data` | Extends `Default` by classifying a `DbException` as `Transient` when `DbException.IsTransient` is `true`, and `Permanent` when it is not. Adds no package dependency and no error-number table. |
| `Classifier.RetryEverything` | Contains no specific rules; every exception is classified as `Transient`. |
| `On<TException>(Verdict)` | Assigns a fixed verdict to a specific exception type and its subclasses. |
| `On<TException>(Func<TException, Verdict>)` | Assigns a verdict based on a predicate that can inspect the exception. |
| `OnResult<T>(Func<T, Verdict>)` | Assigns a verdict to a returned value. `T` must match exactly. |
| `ClassifyException(Exception)` | Returns the verdict for a given exception. |
| `ClassifyResult<T>(T)` | Returns the verdict for a given result. Returns `Verdict.Ok` if no rule is registered for type `T`. |
| `ToString()` | Returns a list of all rules in evaluation order, including the default verdict for unrecognized exceptions. |

Rules are evaluated in reverse order of addition; the most recently added rule takes precedence.

### HTTP status-code rules
`Classifier.Http` uses the following rules for `HttpResponseMessage` outcomes:

| Status | Verdict |
| :--- | :--- |
| 429 | `Throttled` (includes `Retry-After` if present) |
| 503 with `Retry-After` | `Throttled` (includes `Retry-After`) |
| Other 5xx and 408 | `Transient` |
| All other statuses | `Ok` |

The `Retry-After` value is supported as both a delta-seconds value and an HTTP date. Both are converted to a `TimeSpan` and floored at zero.

### Database rules
`Classifier.Data` reads `DbException.IsTransient`, which the provider implements. Providers that do not override it report `false`, making `Classifier.Data` equivalent to `Classifier.Default`. Resource limits are reported as transient; see [Classify database failures](../features/classification.md#classify-database-failures) for the rule that classifies them as throttling.

## `Verdict`

`Verdict` is a `readonly struct` that describes the outcome of an attempt.

| Member | Description |
| :--- | :--- |
| `Kind` | The `VerdictKind` of the outcome. |
| `RetryAfter` | A server-provided or limiter-provided delay that is honored over the standard backoff curve. This is `null` if there was no pushback. |
| `SelfImposed` | `true` when the verdict came from local admission control rather than from the dependency. The [retry budget](../features/retry-budget.md) is not charged for a self-imposed verdict. `false` for every other verdict, including `default`. |
| `Verdict.Ok` | The call succeeded. |
| `Verdict.Transient` | A failure that may not recur. |
| `Verdict.Permanent` | A failure that will recur. |
| `Verdict.Throttled(TimeSpan?)` | The dependency is defending itself, potentially with a suggested retry delay. |
| `Verdict.Refused(TimeSpan?)` | Local admission control - a [limiter](../features/rate-limiting.md), a distributed lock, a load shedder - refused the attempt. `Kind` is `Throttled` and `SelfImposed` is `true`. |

`Verdict` implements value equality, and `SelfImposed` is part of it: `Verdict.Throttled()` and `Verdict.Refused()` are not equal. `ToString()` prints a summary such as `Throttled (retry after 2s)` or `Throttled (self-imposed, retry after 2s)`.

## `VerdictKind`

`VerdictKind` determines how the [executor](index.md) handles the outcome.

| Value | Retried? | Counted against the breaker? |
| :--- | :--- | :--- |
| `Ok` | No (returned) | Yes (sampled as success) |
| `Transient` | Yes (short backoff curve) | Yes |
| `Throttled` | Yes (long backoff curve or `Retry-After`) | No (dependency is working as intended) |
| `Permanent` | No | No (treated as a client-side error) |

### Special cases
Three specific verdicts are produced by the [executor](index.md) itself and cannot be overridden by a classifier:
1. **Attempt Timeout**: Classified as `Transient`.
2. **Caller Cancellation**: Not classified as a failure.
3. **`RateLimitedException`**: Classified as `Verdict.Refused`, so no classifier can turn a refusal this process imposed on itself into evidence against the dependency.

**Note**: If a classifier identifies an *exception* as `Ok`, the executor treats it as `Permanent`, because an exception cannot be converted into a return value.
