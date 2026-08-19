---
title: Retry budget
description: Retries bounded as a fraction of traffic - on by default, and the reason a failing dependency cannot turn your client into a load generator.
order: 5
---

# Retry budget

A retry budget bounds retries as a **fraction of traffic** rather than per call. It is **on by
default**: a policy with `Budget = null` and more than one attempt gets an automatic budget private
to that policy instance, so you get storm protection without learning the word.

## Why a fraction

A per-call attempt limit cannot prevent a retry storm, because every caller independently believes it
is being reasonable. Retries compose multiplicatively: if a frontend, a backend and a database each
permit three retries, one user action can become 4³ = 64 attempts at the bottom. With every client
independently holding to 10%, total amplification is 1.1 times.

## Tuning, sharing and turning it off

<!-- snippet: budget-off -->
```csharp
// Null - the default - is an automatic budget private to this policy instance, so storm
// protection needs no configuration. None is the deliberate opt-out, and the only correct
// use is a dependency you know is not shared.
var unbudgeted = Resilience.Default with { Budget = RetryBudget.None };

// Or tune it, privately to whoever holds the instance.
var generous = Resilience.Default with { Budget = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10) };
```
<!-- endsnippet -->

| Parameter | Default | What it does |
| --- | --- | --- |
| `fraction` | 0.1 | Retries funded per successful attempt - one retry per ten successes in steady state |
| `minimumPerSecond` | 3 | An absolute floor, so a low-traffic client can still retry at all |

<!-- snippet: budget-shared -->
```csharp
// Retries compose multiplicatively: three layers each retrying three times is 27 attempts
// at the bottom. A budget bounds retries as a fraction of traffic - 10% here - so the
// aggregate is bounded whether or not anybody coordinates.
var budget = RetryBudget.Shared("payments", fraction: 0.1, minimumPerSecond: 3);

var charge = Resilience.Http with { Budget = budget };
var refund = Resilience.Http with { Budget = budget };
```
<!-- endsnippet -->

Sharing is deliberately opt-in. A single process-wide budget would let a storm against payments
throttle retries to search, which is the blast-radius inversion a resilience library exists to
prevent.

Budget state is per-process and there is no coordination between pods. That is not a defect - it is
why the mechanism works at all: every client independently capping retries bounds fleet-wide
amplification with no protocol. It does follow that a budget allocated per `HttpClient` instance, or
resolved from a scoped DI container, is worthless, because it is thrown away before it can observe
enough traffic to mean anything. Share one instance, or use `RetryBudget.Shared`.

## What a refused retry looks like

The first attempt always runs - a budget throttles retries, not calls. When it will not fund one, the
call stops with `StopReason.BudgetExhausted` after a short pause, and
`CallRejectedException.RetryAfter` carries how long until the floor rate has accrued a whole token.

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

## Watching it

<!-- snippet: budget-utilisation -->
```csharp
// For a dashboard: a budget sitting near 1 is a client whose retries are being refused,
// which is a symptom to alert on rather than a steady state.
double spent = budget.Utilisation;   // 0 to 1
```
<!-- endsnippet -->

`nresilience.attempts ÷ nresilience.calls` is the number to alert on - see
[telemetry](telemetry.md).

Go deeper: [Retry budget internals](../deep-dives/retry-budget-internals.md).

