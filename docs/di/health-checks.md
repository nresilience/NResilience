---
title: Health checks
description: Put every circuit breaker and retry budget in the process on your health endpoint.
order: 4
---

# Health checks

A circuit breaker holds the single most operationally interesting fact in the process: whether a dependency is currently considered unusable. `AddResilience()` on an `IHealthChecksBuilder` puts that fact, and every retry budget's utilization, on your health endpoint.

## Register the check

<!-- snippet: di-health-checks -->
```csharp
services.AddResilience(name: "api", policy: Resilience.Http with { Breaker = Api });
services.AddHttpClient(name: "orders").AddResilience();

// One line. Every breaker behind a registered policy, every per-host breaker held by a
// client registered with AddResilience(), and every retry budget's utilization.
services.AddHealthChecks().AddResilience();
```
<!-- endsnippet -->

The check reports on three sources, and needs no configuration to find the first two:

| Source | How it is found |
| :--- | :--- |
| Policies registered with `services.AddResilience(name, …)` | By name, from `IResiliencePolicies`. |
| Clients registered with `.AddResilience()` | Their per-host breakers and budgets, from the handler currently serving each client. |
| Anything passed to `Watch` | Explicitly, for a policy held in a `static readonly` field. |

## Read the result

Each guard is one entry in the check's `Data` dictionary:

| Key | Value |
| :--- | :--- |
| `breaker:<name>` | `Closed`, `HalfOpen`, or `Open since <timestamp>` / `Isolated since <timestamp>`. |
| `budget:<name>` | Utilization, from 0 to 1. |

For a registered policy, `<name>` is the registration name. For an HTTP client it is `<client>:<host:port>`, so a client talking to three hosts reports three breakers and you can tell which one is in trouble.

The `Description` summarizes: either `"4 breaker(s) closed, 2 retry budget(s) funding retries."` or, when something is wrong, `"1 of 4 breaker(s) open or isolated."`

A process with nothing to report says so rather than claiming health - if you see `"No breakers or retry budgets are registered"`, either nothing is configured with one, or the policies that have them are not registered in this container.

## An open breaker is Degraded, not Unhealthy

This is the default, and it is a deliberate opinion.

A breaker opens because a **dependency** is down. At that moment this process is doing exactly what it was configured to do: shedding load, protecting the dependency, and failing fast instead of piling up. Reporting yourself `Unhealthy` for that invites the platform to act on it - restart a pod that is working correctly, pull it out of a load balancer that should keep sending it traffic, or fail a deployment over someone else's outage. None of those help, and the restart actively hurts, because it discards the breaker state that was protecting the dependency.

`Degraded` says the true thing: still serving, with a known impairment.

A process that genuinely cannot do anything useful while its one dependency is down is a real shape, and it is one line:

<!-- snippet: di-health-checks-configured -->
```csharp
// An open breaker reports Degraded by default: the dependency is down and this process is
// shedding load correctly, so reporting Unhealthy invites an orchestrator to restart a pod
// that is working. Override it when the process genuinely cannot serve without that
// dependency.
services.AddHealthChecks().AddResilience(configure: o =>
{
    o.BreakerOpenStatus = HealthStatus.Unhealthy;
    o.BudgetThreshold = 0.75;
    o.Watch(name: "payments", breaker: Payments);
});
```
<!-- endsnippet -->

| Option | Default | What it does |
| :--- | :--- | :--- |
| `BreakerOpenStatus` | `Degraded` | What an open or isolated breaker reports. |
| `BudgetExhaustedStatus` | `Degraded` | What a retry budget at or above the threshold reports. |
| `BudgetThreshold` | `0.9` | The utilization at which a budget counts as exhausted. |
| `IncludeHttpClients` | `true` | Whether per-host guards from HTTP clients are included. |
| `Watch(name, breaker)` | - | Also report a breaker DI does not own. |
| `Watch(name, budget)` | - | Also report a retry budget DI does not own. |

The budget threshold is not `1.0` on purpose. A budget sitting at 0.9 is already refusing retries in bursts, and by the time it reads exactly 1.0 the thing worth alerting on has been happening for a while.

## What the check does not do

**It contacts nothing.** The check reads state that is already in memory, so it cannot itself time out, hang, or add load to the dependency it is reporting on. That makes it safe on a liveness endpoint as well as a readiness one, and it is why the check is a read rather than the more obvious design of having the health endpoint make a real call.

**It reports the current handler generation.** `IHttpClientFactory` rebuilds each client's handler chain when the handler lifetime expires - two minutes by default - and an HTTP client's per-host breakers belong to the handler. A breaker that opened is therefore reported until that rotation and not after it. What the check shows is the state guarding the *next* request, which is the state worth probing.

**It does not aggregate across the fleet.** Breakers and retry budgets are per-process by design, which is what lets them work with no coordination protocol. One pod's endpoint tells you about one pod. The fleet-level view comes from aggregating the [`nresilience.*` metrics](telemetry.md).

## Go deeper

- [Circuit breaker](../features/circuit-breaker.md) - what opens one, and what closes it again.
- [Retry budget](../features/retry-budget.md) - what utilization means and why it is a fraction of traffic.
- [Telemetry](telemetry.md) - the metrics that answer the same questions across a fleet.
