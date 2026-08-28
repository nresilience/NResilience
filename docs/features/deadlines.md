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
    Deadline = TimeSpan.FromSeconds(value: 10), // the whole call
    AttemptTimeout = TimeSpan.FromSeconds(value: 3), // one attempt
};

// Attempt 1 gets 3 s. An attempt starting with 2 s left on the deadline gets 2 s, not 3 -
// the effective ceiling is min(AttemptTimeout, time left), so there is no
// "is that per attempt or total?" question to get wrong.
```
<!-- endsnippet -->

`Deadline` is measured as wall-clock time from the moment you call `RunAsync`. It covers every attempt, every backoff delay, and every `BeforeAttempt` hook.

`AttemptTimeout` covers a single attempt. If no time remains on the deadline, a retry is never started; the call fails immediately with a deadline exception rather than sleeping through a backoff delay.

## Propagate the deadline across a hop

A deadline stops at the process edge unless something carries it. A service with 200 ms left that sends a request the peer works on for 10 seconds has already produced garbage, and neither side can tell. Two halves fix that, and each is useful without the other.

### Send the deadline

Set `PropagateDeadline` on the HTTP options, and every attempt carries how long this side is going to wait for it:

<!-- snippet: deadline-propagate -->
```csharp
// The outbound half. Every attempt carries the time this side is prepared to wait:
// min(AttemptTimeout, time left on the deadline). This allows peers to stop
// work that is no longer needed. Off by default.
var api = Resilience.Http with
{
    Deadline = TimeSpan.FromSeconds(value: 10),
    AttemptTimeout = TimeSpan.FromSeconds(value: 3),
};

var options = new HttpResilienceOptions { PropagateDeadline = true };

using var client = new HttpClient(handler: new ResilienceHandler(innerHandler: transport, policy: api, options: options));
using var response = await client.GetAsync(requestUri: uri, cancellationToken: cancellationToken);

// X-Deadline-Ms: 3000 on the first attempt, and less on every attempt after it.
```
<!-- endsnippet -->

The value is the attempt's own ceiling - `min(AttemptTimeout, time left on the deadline)` - in whole milliseconds, and it is recomputed for every attempt and every hedged leg. `DeadlineHeader` changes the header name, which defaults to `X-Deadline-Ms`.

> [!NOTE]
> `grpc-timeout` is not a drop-in name for it. gRPC's value carries a unit suffix rather than a bare count of milliseconds, and the gRPC client stack already propagates its own deadlines from `CallOptions.Deadline`.

### Inherit the deadline

Set `UseAmbientDeadline` on the policy, and the effective deadline becomes `min(Deadline, the time the caller is still waiting)`:

<!-- snippet: deadline-inherit -->
```csharp
// The inbound half. The policy is bounded by the inherited deadline, so its
// effective deadline is min(Deadline, time the caller is still waiting), resolved once
// at the start of the call.
var api = Resilience.Http with { UseAmbientDeadline = true };

// In an ASP.NET Core app, UseResilienceDeadline() publishes what the caller sent. Anywhere else -
// a queue consumer reading a deadline off a message, or a test - publish it yourself.
using var inbound = ResilienceDeadline.Begin(remaining: TimeSpan.FromMilliseconds(value: 200));
```
<!-- endsnippet -->

Nothing else in the model changes. `AttemptTimeout` is already `min(configured, time left)`, so a shorter deadline shortens the attempts with it, and a call whose inherited deadline has already expired fails immediately with `DeadlineExceededException` without contacting the dependency at all.

In an ASP.NET Core app, install `NResilience.AspNetCore` and read the header with one line:

```csharp
app.UseResilienceDeadline();
```

Register it before anything that makes an outbound call. `UseResilienceDeadline` also takes a callback: `Header` changes the header it reads, `Maximum` caps what it will believe from a caller, and `Reserve` keeps part of the deadline back for this service's own work.

`UseAmbientDeadline` is off by default and stays off in every preset, because reading the ambient value costs an `AsyncLocal<T>` read on calls that mostly have no inbound deadline to read. For what that costs and why the read happens once per call rather than once per attempt, see [the cancellation contract](../deep-dives/cancellation.md).

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
    Console.WriteLine(value: $"gave up after {deadline.Deadline.TotalSeconds}s and {deadline.Attempts.Count} attempt(s)");
}
catch (TimeoutException attempt)
{
    Console.WriteLine(value: $"one attempt overran: {attempt.Message}");
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
