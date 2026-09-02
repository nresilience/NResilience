---
title: Deadlines and attempt timeouts
description: Manage total call duration and individual attempt limits.
order: 2
---

# Deadlines and attempt timeouts

A retried call needs two different time bounds - mixing them up causes common timeout bugs. A 30-second per-attempt timeout with three retries could run for 90 seconds in total.

The **deadline** is the ceiling for the entire operation, including every attempt and backoff delay. The **attempt timeout** is the ceiling for a single attempt.

Both are on by default:
- **Deadline**: 30 seconds for the whole call.
- **Attempt timeout**: 10 seconds for any single attempt, and usually far less: the ceiling is measured from the dependency's own latency, and 10 seconds is where the lowering stops.

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

`Deadline` is wall-clock time from the moment you call `RunAsync`. It covers every attempt, every backoff delay, and every `BeforeAttempt` hook.

`AttemptTimeout` covers one attempt. If no time remains on the deadline, a retry never starts; the call fails immediately with a deadline exception rather than sleeping through a backoff delay.

This also applies when too little time remains. If the circuit breaker measures how long a healthy call to the dependency takes (which it does by default; see [`Breaker.NormalLatency`](circuit-breaker.md#trip-on-brownouts-without-guessing-a-number)), a retry with less time remaining than that measurement is not started. You get the same `DeadlineExceededException` a few milliseconds sooner, with one fewer attempt in `result.Attempts`, and the dependency gets one fewer request that you would not have waited for. The first attempt of a call always runs regardless of the measurement, and a policy with no breaker or a cold baseline behaves as usual.

## Measure the attempt ceiling instead of guessing it

**On by default.** `AttemptTimeout` alone is a number you pick per dependency before it runs, and update whenever it changes. `Timeouts` measures the ceiling from the dependency's own latency instead, by default at `AttemptTimeouts.Above(3)` - three times the recent p95. `Timeouts = null` leaves `AttemptTimeout` as the only per-attempt ceiling.

<!-- snippet: deadline-measured-ceiling -->
```csharp
var api = Resilience.Http with
{
    AttemptTimeout = TimeSpan.FromSeconds(value: 5), // the ceiling. Never exceeded.
    Timeouts = AttemptTimeouts.Above(multiple: 3), // and usually far below it: 3x the recent p95.
};

// The measured term can only lower the ceiling, so AttemptTimeout stops being a guess about how
// long this dependency takes and becomes what it reads as - the point beyond which you stop
// caring. A dependency whose p95 is 40 ms gets a 120 ms ceiling; one whose p95 is 2 s gets the
// configured 5 s, because 3x its p95 is above that and the clamp is what wins.
```
<!-- endsnippet -->

The effective ceiling is the minimum of `AttemptTimeout`, the time remaining on the deadline, and the measured quantile multiplied by `Multiple`. Because the measured term only lowers the ceiling, the feature is safe to leave on: `AttemptTimeout` remains the ultimate ceiling, and a dependency slow enough that the measurement exceeds it simply gets the default behavior.

`AttemptTimeouts.Above(3)` is a complete configuration. The properties you can change:

| Property | Default | Description |
| :--- | :--- | :--- |
| `Multiple` | none - you supply it | How many times the measured quantile an attempt may take. Must be greater than 1. |
| `Quantile` | `0.95` | The quantile of recent successful latency the ceiling is measured from. Between 0.5 and 0.99. |
| `Window` | `5 min` | How much history the estimate covers. |
| `MinimumSamples` | `20` | How many recent successful calls the estimate needs before it bounds anything. |
| `Floor` | `50 ms` | A floor under the measured ceiling, so a dependency whose p95 is microseconds does not cancel itself on one scheduling hiccup. |

Four behaviors are worth knowing:

- **A cold process does not guess.** Below `MinimumSamples` there is no measured term and the attempt gets `AttemptTimeout` unchanged.
- **It only tightens a ceiling you set.** A policy whose `AttemptTimeout` is `Timeout.InfiniteTimeSpan` gets no default measured ceiling: you said the deadline was the only per-attempt bound, and there is nothing there to tighten. Writing `Timeouts` yourself there is a different instruction - "bound me by the dependency's latency and nothing else" - and it is honored.
- **Only successful attempts are sampled.** A ceiling tight enough to cancel calls that would have succeeded starves its own estimator, so the policy reverts to `AttemptTimeout` rather than tightening further.
- **The estimate is per policy instance.** The HTTP handler derives one policy per host, so each host's ceiling is measured from that host's own latency.

Read the current value from `MeasuredAttemptTimeout`, or watch the `nresilience.attempt.timeout` histogram, which is recorded when the number moves. Both report the measured ceiling before `AttemptTimeout` clamps it, so a value above your `AttemptTimeout` is the reading that says the clamp is now what bounds the attempt.

> [!NOTE]
> When [hedging](hedging.md) is configured too, the ceiling is measured from at least the hedge's own quantile. A ceiling below the hedge threshold would cancel the first leg at the moment the second was due to start, and you would have bought a feature that never fires.

### If you have an exact SLA

Two different requirements hide behind "we need an exact timeout", and they have different answers.

**A hard upper bound** - "this call must never take longer than 3 seconds" - is `Deadline`, and a measured ceiling never touches it. The effective ceiling is `min(AttemptTimeout, time left, measured)`, so the time left always clamps and the measured term only ever operates *inside* the deadline. Your bound is exact to the tick whether or not `Timeouts` is set, and the measured term can only ever cancel an attempt **earlier** than you configured, never later.

Counter-intuitively, a tight SLA is the strongest case *for* measuring the ceiling. Take a 3-second deadline, three attempts, and a dependency whose p95 is 40 ms:

| | With `AttemptTimeout = 10 s` alone | With `Timeouts = AttemptTimeouts.Above(3)` |
| :--- | :--- | :--- |
| First attempt hangs | Capped at `min(10 s, 3 s left)` = 3 s | Cancelled at ~120 ms |
| Attempts you actually get | **One.** The deadline is gone. | **Three**, all inside ~660 ms |

An attempt timeout far above the dependency's real latency is not a safety margin under a tight deadline; it is a guarantee that one hung attempt spends the whole budget. [`NRES004`](../reference/analyzers.md#nres004) warns about the extreme form of this - an `AttemptTimeout` longer than the `Deadline` - and a measured ceiling handles the cases an analyzer cannot see, because they depend on what the dependency actually does.

**A guaranteed allowance** - "every attempt must be allowed a full 2 seconds before we give up on it" - is the requirement a measured ceiling would genuinely fight, and `Floor` is the answer:

<!-- snippet: deadline-sla-floor -->
```csharp
// An exact SLA: this call has 10 seconds, full stop. Deadline is that bound, and nothing here
// lowers or raises it.
var api = Resilience.Http with
{
    Deadline = TimeSpan.FromSeconds(value: 10),
    AttemptTimeout = TimeSpan.FromSeconds(value: 5),

    // And this endpoint legitimately takes up to 2 s sometimes, so no attempt may be
    // cancelled before then. Adaptation is confined to [2 s, 5 s]: it can trim the dead time
    // above 2 s and can never cut into the allowance below it.
    Timeouts = AttemptTimeouts.Above(multiple: 3) with { Floor = TimeSpan.FromSeconds(value: 2) },
};
```
<!-- endsnippet -->

Note that a `Floor` at or above `AttemptTimeout` is refused at validation. That combination pins the ceiling to exactly `AttemptTimeout`, which makes `Timeouts` do nothing at all, and the library refuses configurations that silently have no effect - so the honest way to say "an exact attempt timeout, always" is `Timeouts = null`. An `AttemptTimeout` at or below the default 50 ms `Floor` works the same way: the default steps aside rather than turning your policy into an error.

### Bounding one request, not one policy

`Timeouts` measures across calls, so the estimate lives on the policy instance. If you need a bound that differs per request, publish it rather than deriving a policy per request:

- `ResilienceDeadline.Begin(remaining)` with `UseAmbientDeadline` gives that request an exact deadline, resolved once as `min(Deadline, remaining)`. See [propagating the deadline](#propagate-the-deadline-across-a-hop).
- Deriving `policy with { Deadline = ... }` per request also works, but the latency estimate is keyed by the policy instance - so a policy built per request is permanently cold and `Timeouts` silently does nothing. It fails safe, back to `AttemptTimeout`, but it fails quietly. [`NRES008`](../reference/analyzers.md#nres008) reports the cases the compiler can see.

## Propagate the deadline across a hop

A deadline stops at the process edge unless something carries it across. A service with 200 ms left that sends a request the peer works on for 10 seconds has already produced garbage, and neither side can tell. Two halves fix that, and each is useful without the other.

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

The value is the attempt's own ceiling - `min(AttemptTimeout, time left on the deadline)` - in whole milliseconds, recomputed for every attempt and every hedged leg. `DeadlineHeader` changes the header name, which defaults to `X-Deadline-Ms`.

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

Nothing else in the model changes. `AttemptTimeout` is already `min(configured, time left)`, so a shorter deadline shortens the attempts with it, and a call whose inherited deadline has already expired fails immediately with `DeadlineExceededException` without contacting the dependency.

In an ASP.NET Core app, install `NResilience.AspNetCore` and read the header with one line:

```csharp
app.UseResilienceDeadline();
```

Register it before anything that makes an outbound call. `UseResilienceDeadline` also takes a callback: `Header` changes the header it reads, `Maximum` caps what it believes from a caller, and `Reserve` keeps part of the deadline back for this service's own work.

`UseAmbientDeadline` is off by default and stays off in every preset, because reading the ambient value costs an `AsyncLocal<T>` read on calls that mostly have no inbound deadline to read. For what that costs and why the read happens once per call rather than once per attempt, see [the cancellation contract](../deep-dives/cancellation.md).

## Handle timeout exceptions

Both `DeadlineExceededException` and `AttemptTimeoutException` derive from `TimeoutException`, so you can catch them together or separately. Both include the attempt log.

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

The executor classifies attempt timeouts as `Transient` internally rather than through your classifier, so it can tell its own timeout apart from caller cancellation.

## Caller cancellation

Cancelling the token you passed to the call aborts it immediately. Caller cancellation is not a failure:
- It is never retried.
- It is not counted against a breaker or a budget.
- It is never converted into a timeout.
- Classifiers cannot override it.

The call returns an `OperationCanceledException`, even when using `TryRunAsync`.

If a token is cancelled while an attempt is already succeeding, NResilience does not throw away the completed work. The post-attempt check only prevents the loop from starting *another* attempt.

## Work that ignores the token

Timeouts work through the cancellation token, so callbacks must observe it to be terminated. The required `CancellationToken` parameter on every execution overload, the analyzers, and the `OrphanedWork` event are the safeguards against callbacks that ignore cancellation.

For the full picture, see [The cancellation contract](../deep-dives/cancellation.md).
