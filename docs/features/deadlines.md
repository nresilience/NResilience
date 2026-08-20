---
title: Deadlines and attempt timeouts
description: Manage total call duration and individual attempt limits.
order: 2
---

# Deadlines and attempt timeouts

A retried call requires two different time bounds to avoid common timeout bugs. For example, a 30-second per-attempt timeout with three retries could result in a total call duration of 90 seconds.

The **deadline** is the ceiling for the entire operation, including every attempt and backoff delay. The **attempt timeout** is the ceiling for a single attempt.

Both are enabled by default:
- **Deadline**: 30 seconds for the whole call.
- **Attempt timeout**: 10 seconds for any single attempt.

Use `Timeout.InfiniteTimeSpan` to disable either bound.

> [!CAUTION]
> A timeout cannot terminate a callback that ignores its cancellation token. If a callback never observes the token, the policy must wait for the task to complete because the [executor](../reference/index.md) is awaiting that task.
> 
> To prevent this, every execution overload requires a callback that accepts a `CancellationToken`. The [analyzers](../reference/analyzers.md) (NRES001 and NRES002) report cases where a callback is handed the wrong token at build time. If an attempt overruns its ceiling by more than one second, an `OrphanedWork` event fires retrospectively when the work finally returns.

## The two bounds

The effective ceiling for any attempt is the minimum of the `AttemptTimeout` and the time remaining on the `Deadline`.

<!-- snippet: deadline-effective -->
```csharp
var api = Resilience.Default with
{
    Deadline = TimeSpan.FromSeconds(10),        // the whole call
    AttemptTimeout = TimeSpan.FromSeconds(3),   // one attempt
};

// Attempt 1 gets 3 s. An attempt starting with 2 s left on the deadline gets 2 s, not 3 -
// the effective ceiling is min(AttemptTimeout, time left), so there is no
// "is that per attempt or total?" question to get wrong.
```
<!-- endsnippet -->

`Deadline` is measured as wall-clock time from the moment you call `RunAsync`. It covers every attempt, every backoff delay, and every `BeforeAttempt` hook.

`AttemptTimeout` covers a single attempt. If no time remains on the deadline, a retry is never started; the call fails immediately with a deadline exception rather than sleeping through a backoff delay.

## Handle timeout exceptions

Both `DeadlineExceededException` and `AttemptTimeoutException` derive from `TimeoutException`. You can catch them together or separately. Both exceptions include the attempt log.

<!-- snippet: deadline-handle-exception -->
```csharp
// DeadlineExceededException and AttemptTimeoutException are both TimeoutException, so one
// catch covers "it did not answer in time" and the two are still distinguishable.
try
{
    result.ValueOrThrow();
}
catch (DeadlineExceededException deadline)
{
    Console.WriteLine($"gave up after {deadline.Deadline.TotalSeconds}s and {deadline.Attempts.Count} attempt(s)");
}
catch (TimeoutException attempt)
{
    Console.WriteLine($"one attempt overran: {attempt.Message}");
}
```
<!-- endsnippet -->

The executor classifies attempt timeouts as `Transient`. This classification happens internally rather than through your classifier, allowing the executor to distinguish its own timeout from caller cancellation.

## Caller cancellation

Cancelling the token you provide to the call aborts the operation immediately. Caller cancellation is not treated as a failure:
- It is never retried.
- It is not counted against a breaker or a budget.
- It is never converted into a timeout.
- Classifiers cannot override it.

The call returns an `OperationCanceledException`, even when using `TryRunAsync`.

If a token is cancelled while an attempt is already succeeding, NResilience does not discard the completed work. The post-attempt check prevents the loop from starting *another* attempt.

## Work that ignores the token

Because timeouts rely on the cancellation token, callbacks must observe it to be terminated. The requirement for a `CancellationToken` in every execution overload, combined with the analyzer and the `OrphanedWork` event, provides safeguards against callbacks that ignore cancellation.

For more information, see [The cancellation contract](../deep-dives/cancellation.md).
