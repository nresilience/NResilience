---
title: Telemetry
description: Observe policy behavior through a single event stream and integrated metrics.
order: 9
---

# Telemetry

When a call is slow or fails in production, you need to know what the policy did: which attempts were retried, how long they took, whether the breaker opened, whether the budget ran out. Telemetry gives you that through a single event stream.

Telemetry is on by default for policies registered in a container and opt-in for policies built manually. If `OnEvent` is `null`, the [executor](../reference/index.md) raises no events and incurs no overhead.

The telemetry system uses a single struct, `CallEvent`, and a single delegate, `Resilience.OnEvent`.

## Attach a listener

Attach a listener to a policy to log or record events.

<!-- snippet: telemetry-listener -->
```csharp
var api = Resilience.Http with
{
    Name = "payments",
    Backoff = Backoff.None,
    OnEvent = e => _logger.LogInformation(
        message: "{Policy} {Kind} attempt {Attempt}: {Verdict} in {Ms}ms",
        e.PolicyName, e.Kind, e.AttemptNumber, e.Verdict.Kind, e.Duration.TotalMilliseconds),
};
```
<!-- endsnippet -->

The listener is synchronous and runs on the executor's thread. Keep it fast - logging, counting, enqueuing - and avoid synchronous I/O. Any exception a listener throws is swallowed so telemetry cannot fail the operation it is observing.

To use multiple listeners, combine them using the `+` operator: `OnEvent = first + second`.

A lambda is still the right answer for anything that is not an `ILogger`. If it is, a ready-made listener already exists and says what each event means: see [Logging](logging.md).

## Event types

| Kind | Description | Terminal? |
| :--- | :--- | :--- |
| `Attempt` | An attempt finished, regardless of the verdict | No |
| `Retrying` | A retry was decided and the backoff delay is about to start | No |
| `Succeeded` | The call succeeded | Yes |
| `NotRetried` | The outcome was `Permanent` | Yes |
| `Exhausted` | The final attempt failed and no retries remain | Yes |
| `RejectedByBreaker` | A circuit breaker refused the call: the dependency is unavailable | Yes |
| `RejectedByBudget` | The retry budget refused to fund another attempt: this client is retrying too hard | Yes |
| `DeadlineExceeded` | The total wall-clock budget expired | Yes |
| `OrphanedWork` | A callback ran past the timeout that should have stopped it | No |
| `BreakerOpened` / `BreakerClosed` / `BreakerHalfOpened` | A circuit breaker changed state | No |
| `NestedRetry` | The request is already inside another retrying client | No |
| `HedgeStarted` | A copy of a slow attempt was started. `Delay` carries the live [latency quantile](hedging.md) that triggered it | No |
| `HedgeWon` | The copy answered, so this call saw the shorter of two draws | No |
| `HedgeDiscarded` | An attempt was cancelled because a sibling answered first | No |
 
**Every call ends with exactly one terminal event.** That invariant is what makes counts of logical operations accurate. Use the `IsTerminal` property to identify these events. `IsRejection` is true for the two refusal kinds; use it when a listener treats both rejections alike.

<!-- snippet: telemetry-recorder -->
```csharp
var api = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

// Attempt, Retrying, Attempt, Succeeded
Console.WriteLine(value: string.Join(separator: ", ", values: events.Kinds));
```
<!-- endsnippet -->

- `Duration` represents the individual attempt's duration for `Attempt` events, and the total elapsed time for all other event types.
- `Delay` represents the pause about to be served for `Retrying` and the two rejection events; it is `null` for other events.
- `Reason` agrees with the kind on a rejection: `DependencyUnavailable` for `RejectedByBreaker` and `BudgetExhausted` for `RejectedByBudget`. A listener switching on `Kind` does not need to read this field.

<!-- snippet: telemetry-tostring -->
```csharp
// [PolicyName] Kind #N VerdictKind ExceptionType (duration) +delay
Console.WriteLine(value: events[index: 0]); // [api] Attempt #1 Ok (0.1ms)
```
<!-- endsnippet -->

For more details, see the [`CallEvent` reference](../reference/events.md).

## Metrics and traces

The `NResilience.Extensions` package provides a meter, an activity source, and a listener that feeds both.

<!-- snippet: telemetry-with-telemetry -->
```csharp
// A policy registered in a container is instrumented for you. A policy in a static field
// is not - this says it.
var api = (Resilience.Http with { Name = "payments" }).WithTelemetry();
```
<!-- endsnippet -->

| Instrument | Unit | Description |
| :--- | :--- | :--- |
| `nresilience.calls` | `{call}` | Total logical operations |
| `nresilience.attempts` | `{attempt}` | Total wire-level attempts |
| `nresilience.rejections` | `{rejection}` | Calls refused by a guard, tagged `dependency_unavailable` or `budget_exhausted` |
| `nresilience.call.duration` | s | End-to-end duration of a logical operation |
| `nresilience.attempt.duration` | s | Duration of a single attempt |
| `nresilience.hedges` | `{hedge}` | [Hedged](hedging.md) attempts, tagged `started`, `won` or `discarded` |
| `nresilience.hedge.threshold` | s | The latency quantile a hedge fired at, recorded when it fired |
| `nresilience.attempt.timeout` | s | The measured per-attempt [ceiling](deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it), recorded when it changes |
| `nresilience.limiter.leases` | `{lease}` | Permits a [limiter](rate-limiting.md) was asked for, tagged `acquired` or `denied` |
| `nresilience.limiter.wait.duration` | s | How long a caller waited on a limiter. Zero unless queueing is enabled |

The **retry fraction** is `nresilience.attempts ÷ nresilience.calls` - the primary metric for spotting retry feedback loops and retry storms.

For more, see [Telemetry in DI](../di/telemetry.md).
