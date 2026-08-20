---
title: Circuit breaker
description: Prevent cascading failures by stopping calls to a failing dependency.
order: 4
---

# Circuit breaker

When a dependency fails, continuing to call it on every request can overwhelm the service and exacerbate the outage. A **circuit breaker** prevents this by stopping calls to the dependency, allowing it time to recover, and periodically letting a small number of trial calls through to test for recovery.

Circuit breakers are opt-in and are implemented as objects rather than settings. This allows you to define the scope of the "stop calling" decision based on your application's architecture.

## Enable the circuit breaker

Create a `Breaker` object and assign it to your policy.

<!-- snippet: breaker-construct -->
```csharp
// Breaker scope is a variable with a name and a lifetime. `with` copies the reference,
// so every policy derived from `payments` shares this breaker.
var breaker = new Breaker { Name = "payments" };

var payments = Resilience.Http with { Breaker = breaker };
var paymentsWrites = payments with { Attempts = 1 };
```
<!-- endsnippet -->

The scope of the breaker is determined by where you hold the reference. Because the `with` keyword copies the reference and not the state, two policies derived from a common ancestor share the same breaker.

For HTTP calls, the handler can scope a breaker per host automatically. For more information, see [per-host scope](../http/per-host-scope.md).

## Breaker states

A circuit breaker is always in one of these four states:

| State | Description |
| :--- | :--- |
| `Closed` | Normal operation. Calls are allowed, and failures are tracked. |
| `Open` | The dependency is failing. Calls are refused for the duration of the break. |
| `HalfOpen` | The break duration has elapsed. A small number of trial calls (probes) are allowed to test for recovery. |
| `Isolated` | Forced open by an operator via `Isolate()`. The breaker stays open until `Reset()` is called. |

## Trip conditions

A circuit breaker can trip based on consecutive failures or based on rates of failure and slowness. For example, a dependency might return successful responses but with such high latency that it exhausts your thread and connection pools.

| Setting | Default | Description |
| :--- | :--- | :--- |
| `ConsecutiveFailures` | 5 | The number of consecutive failures before the breaker opens. |
| `FailureRatio` | null | An optional rate-based trip condition, evaluated alongside the consecutive failure counter. |
| `MinimumCalls` | 20 | The minimum number of calls required before a ratio-based trip is evaluated. |
| `Window` | 30 s | The sliding window over which rates are measured. |
| `SlowCallThreshold` | null | The latency threshold; any attempt slower than this counts as a slow call. |
| `SlowCallRatio` | 0.5 | The proportion of slow calls within the window that trips the breaker. |
| `BreakDuration` | 15 s | The duration of the first break. |
| `MaxBreakDuration` | 2 min | The maximum break duration. The break duration doubles on each consecutive open. |
| `HalfOpenProbes` | 1 | The number of concurrent trial calls allowed while in the `HalfOpen` state. |
| `ProbeSuccesses` | 2 | The number of successful probes required to close the breaker. |

<!-- snippet: breaker-slow-calls -->
```csharp
// The most common real degradation is not errors, it is a dependency answering 200s at
// 30x normal latency. A breaker that only counts errors stays closed through the whole
// incident, because the responses are not failing - they are just slow.
var breaker = new Breaker(new BreakerSettings
{
    ConsecutiveFailures = 5,                             // the default trip condition
    SlowCallThreshold = TimeSpan.FromSeconds(2),         // anything slower counts against
    SlowCallRatio = 0.5,                                 // half the window being slow trips it
    MinimumCalls = 20,                                   // below this, a ratio means nothing
    Window = TimeSpan.FromSeconds(30),
    BreakDuration = TimeSpan.FromSeconds(15),            // doubles per consecutive open
    MaxBreakDuration = TimeSpan.FromMinutes(2),
    ProbeSuccesses = 2,                                  // two good probes to close, not one
})
{
    Name = "search",
};
```
<!-- endsnippet -->

The breaker samples individual **attempts**. Only `Transient` outcomes count as evidence of failure. `Throttled` responses indicate the dependency is functioning and defending itself, and `Permanent` outcomes are typically client-side issues.

## Handle refused calls

When a circuit breaker refuses a call, it serves a short pause before returning. This prevents callers in tight polling loops from busy-spinning and wasting CPU.

<!-- snippet: breaker-rejection -->
```csharp
// A refused call reports itself rather than the dependency's last exception, and it says
// which guard refused it. RetryAfter is there so a caller that schedules its own polling
// does not have to guess.
if (result.Exception is CallRejectedException rejection)
{
    Console.WriteLine(rejection.Reason);      // DependencyUnavailable, or BudgetExhausted
    Console.WriteLine(rejection.RetryAfter);  // when to come back, when there is an answer
}
```
<!-- endsnippet -->

The breaker uses `StopReason.DependencyUnavailable` for refusals. The [retry budget](retry-budget.md) uses `BudgetExhausted`.

For more information, see [Guarded rejection](../deep-dives/guarded-rejection.md).

## Manage the breaker

You can monitor the state of the breaker or manually control its behavior.

<!-- snippet: breaker-admin -->
```csharp
BreakerState state = breaker.State;         // Closed, Open, HalfOpen or Isolated
DateTimeOffset? since = breaker.OpenedAt;   // null while it is closed

breaker.Isolate();                          // force it open and keep it there
breaker.Reset();                            // close it and forget the history
```
<!-- endsnippet -->

`State` reports `HalfOpen` for an open breaker whose break duration has elapsed. Reading the state does not consume a probe slot. `Isolate` and `Reset` do not raise events because they are not triggered by a call.

Transitions trigger `BreakerOpened`, `BreakerClosed`, and `BreakerHalfOpened` [events](telemetry.md) on the call that caused the transition.

For a deeper dive, see [Breaker internals](../deep-dives/breaker-internals.md).
