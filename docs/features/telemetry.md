---
title: Telemetry
description: Observe policy behavior through a single event stream and integrated metrics.
order: 7
---

# Telemetry

When a call is slow or fails in production, you need visibility into the policy's behavior - such as which attempts are being retried, how long they take, whether a circuit breaker opened, or if the retry budget is exhausted. Telemetry provides this visibility through a single event stream.

Telemetry is enabled by default for policies registered in a container. For policies built manually, it is opt-in. If `OnEvent` is `null`, the [executor](../reference/index.md) raises no events and incurs no performance overhead.

The telemetry system uses a single struct, `CallEvent`, and a single delegate, `Resilience.OnEvent`.

## Attach a listener

You can attach a listener to a policy to log or record events.

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

The listener is synchronous and runs on the same thread as the executor. To avoid blocking the call, only perform fast operations such as logging, counting, or enqueuing; do not perform synchronous I/O. Any exception thrown by a listener is swallowed to prevent telemetry from failing the operation it is observing.

To use multiple listeners, combine them using the `+` operator: `OnEvent = first + second`.

## Event types

| Kind | Description | Terminal? |
| :--- | :--- | :--- |
| `Attempt` | An attempt finished, regardless of the verdict | No |
| `Retrying` | A retry was decided and the backoff delay is about to start | No |
| `Succeeded` | The call succeeded | Yes |
| `NotRetried` | The outcome was `Permanent` | Yes |
| `Exhausted` | The final attempt failed and no retries remain | Yes |
| `Rejected` | A circuit breaker or the retry budget refused the call | Yes |
| `DeadlineExceeded` | The total wall-clock budget expired | Yes |
| `OrphanedWork` | A callback ran past the timeout that should have stopped it | No |
| `BreakerOpened` / `BreakerClosed` / `BreakerHalfOpened` | A circuit breaker changed state | No |
| `NestedRetry` | The request is already inside another retrying client | No |

**Every call ends with exactly one terminal event.** This invariant ensures that counts of logical operations are accurate.

<!-- snippet: telemetry-recorder -->
```csharp
var api = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

await api.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken);

// Attempt, Retrying, Attempt, Succeeded
Console.WriteLine(string.Join(", ", events.Kinds));
```
<!-- endsnippet -->

- `Duration` represents the individual attempt's duration for `Attempt` events, and the total elapsed time for all other event types.
- `Delay` represents the pause about to be served for `Retrying` and `Rejected` events; it is `null` for other events.
- `Reason` distinguishes between the two types of refusals covered by a `Rejected` event.

<!-- snippet: telemetry-tostring -->
```csharp
// [PolicyName] Kind #N VerdictKind ExceptionType (duration) +delay
Console.WriteLine(events[0]);   // [api] Attempt #1 Ok (0.1ms)
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
| `nresilience.limiter.leases` | `{lease}` | Permits a [limiter](rate-limiting.md) was asked for, tagged `acquired` or `denied` |
| `nresilience.limiter.wait.duration` | s | How long a caller waited on a limiter. Zero unless queueing is enabled |

The **retry fraction** is calculated as `nresilience.attempts ÷ nresilience.calls`. This is the primary metric for monitoring retry feedback loops and identifying potential retry storms.

For more information, see [Telemetry in DI](../di/telemetry.md).
