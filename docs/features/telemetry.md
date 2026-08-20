---
title: Telemetry
description: One event type, one delegate, and the metrics the extensions package builds on them.
order: 6
---

# Telemetry

The whole surface is one struct and one delegate: `CallEvent` and `Resilience.OnEvent`. It is
**opt-in** for a policy you build by hand - `OnEvent = null` means the executor raises nothing and
pays nothing - and **on by default** for a policy registered in a container, because registering a
policy is a statement that this application has an operations story.

## Attaching a listener

<!-- snippet: telemetry-listener -->
```csharp
var api = Resilience.Http with
{
    Name = "payments",
    Backoff = Backoff.None,
    OnEvent = e => _logger.LogInformation(
        "{Policy} {Kind} attempt {Attempt}: {Verdict} in {Ms}ms",
        e.PolicyName, e.Kind, e.AttemptNumber, e.Verdict.Kind, e.Duration.TotalMilliseconds),
};
```
<!-- endsnippet -->

The listener is synchronous and runs on the thread the executor is running on, so a listener that
blocks blocks the call. Log, count, enqueue; do not do I/O. An exception thrown by a listener is
swallowed - telemetry that can fail the operation it is observing is worse than no telemetry.

Two listeners is `OnEvent = first + second`.

## The events

| Kind | When | Terminal? |
| --- | --- | --- |
| `Attempt` | An attempt finished, whatever the verdict | No |
| `Retrying` | A retry is decided and its backoff is about to be served | No |
| `Succeeded` | The call succeeded | Yes |
| `NotRetried` | The outcome was `Permanent` | Yes |
| `Exhausted` | The last attempt failed and there were none left | Yes |
| `Rejected` | A breaker or the budget refused the call | Yes |
| `DeadlineExceeded` | The wall-clock budget ran out | Yes |
| `OrphanedWork` | A callback ran well past the timeout that should have stopped it | No |
| `BreakerOpened` / `BreakerClosed` / `BreakerHalfOpened` | A breaker changed state | No |
| `NestedRetry` | This request is already inside a retrying client | No |

**Every call ends with exactly one terminal event.** That invariant is what makes a count of logical
operations trustworthy, and it is tested.

<!-- snippet: telemetry-recorder -->
```csharp
var api = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

await api.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken);

// Attempt, Retrying, Attempt, Succeeded
Console.WriteLine(string.Join(", ", events.Kinds));
```
<!-- endsnippet -->

`Duration` is the attempt's own on an `Attempt` event and the call's elapsed time on every other
kind. `Delay` is the pause about to be served on `Retrying` and `Rejected`, and null elsewhere.
`Reason` distinguishes the two refusals a `Rejected` event covers.

<!-- snippet: telemetry-tostring -->
```csharp
// [PolicyName] Kind #N VerdictKind ExceptionType (duration) +delay
Console.WriteLine(events[0]);   // [api] Attempt #1 Ok (0.1ms)
```
<!-- endsnippet -->

Go deeper: [`CallEvent` reference](../reference/events.md).

## Metrics and traces

`NResilience.Extensions` ships a meter, an activity source and a listener that feeds both.

<!-- snippet: telemetry-with-telemetry -->
```csharp
// A policy registered in a container is instrumented for you. A policy in a static field
// is not, because nothing about it says there is an operations story - this says it.
var api = (Resilience.Http with { Name = "payments" }).WithTelemetry();
```
<!-- endsnippet -->

| Instrument | Unit | What it is |
| --- | --- | --- |
| `nresilience.calls` | `{call}` | Logical operations - one per call, whatever happened inside it |
| `nresilience.attempts` | `{attempt}` | Wire-level attempts |
| `nresilience.rejections` | `{rejection}` | Calls a guard refused, tagged `dependency_unavailable` or `budget_exhausted` |
| `nresilience.call.duration` | s | End-to-end duration of a logical operation |
| `nresilience.attempt.duration` | s | Duration of one attempt |

`nresilience.attempts ÷ nresilience.calls` is the **retry fraction**: the characteristic metric of a
retry feedback loop, and the one that tells you whether you are approaching a storm rather than
merely serving errors. The counters are split so that it is computable.

Go deeper: [Telemetry in DI](../di/telemetry.md).

