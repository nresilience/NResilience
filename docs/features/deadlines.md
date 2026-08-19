---
title: Deadlines and attempt timeouts
description: The two bounds, how they interact, and what happens to work that ignores its cancellation token.
order: 2
---

# Deadlines and attempt timeouts

Both are **on by default**: a 30-second `Deadline` for the whole call and a 10-second
`AttemptTimeout` for any one attempt. `Timeout.InfiniteTimeSpan` turns either off.

## The two bounds

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

`Deadline` covers everything: every attempt, every backoff delay, every `BeforeAttempt` hook. It is
wall-clock time from the moment you call `RunAsync`.

`AttemptTimeout` covers one attempt. The effective ceiling is `min(AttemptTimeout, time left on the
deadline)`, and a retry the deadline has no time left for is never started - the call fails with the
deadline rather than sleeping through it.

## Handling them

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

`DeadlineExceededException` and `AttemptTimeoutException` both derive from `TimeoutException`, so one
`catch` covers "it did not answer in time" while the two stay distinguishable. Both carry the attempt
log.

An attempt timeout is classified `Transient` by the executor itself, never by your classifier: the
executor knows which cancellation source fired, and telling its own timeout apart from caller
cancellation is the classic bug in timeout implementations.

## Caller cancellation is not a failure

Cancelling the token you passed in aborts the call immediately. It is never retried, never counted
against a breaker or a budget, never converted into a timeout, and no classifier can override it.
`OperationCanceledException` comes straight back out - including from `TryRunAsync`, which is the one
thing that method still throws.

The one deliberate asymmetry: a token cancelled while an attempt was already succeeding does not
throw away the completed work. The post-attempt check exists to stop the loop starting *another*
attempt.

## Work that ignores its token

> [!CAUTION]
> A timeout cannot kill a callback that ignores its cancellation token. The attempt timeout fires,
> the token is cancelled, and a callback that never observes it keeps running - and the policy waits
> for it, because the executor is awaiting that very task.

This is the most-hit footgun in the .NET resilience ecosystem, and the defenses here are structural.
Every execution overload requires a callback that takes a `CancellationToken`, so there is no
zero-argument form to forget. And when an attempt overruns its ceiling by more than a second, an
`OrphanedWork` event fires naming the policy - raised retrospectively, when the work finally does
return, which is the only moment the overrun is observable at all.

Go deeper: [The cancellation contract](../deep-dives/cancellation.md).

