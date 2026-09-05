---
title: Per-service scope
description: Why a gRPC circuit breaker is scoped to the service rather than to the channel, and how to change it.
order: 4
---

# Per-service scope

The HTTP handler scopes its circuit breaker per host, so one dead dependency does not trip calls to healthy ones. A gRPC channel serves exactly one host, so there is no per-host scoping to do - but the blast-radius argument is the same, and the gRPC unit it applies to is the **service**.

One expensive RPC failing should not open the circuit on every other method the client exposes. By default, each gRPC service gets its own [circuit breaker](../features/circuit-breaker.md), its own [retry budget](../features/retry-budget.md), and its own hedging latency estimate:

<!-- snippet: grpc-breakers -->
```csharp
// One breaker and one budget per gRPC service by default, keyed by the service's full name -
// so an operator can be told which dependency opened, not merely that something did.
foreach (var (service, breaker) in interceptor.Breakers())
    Console.WriteLine($"{service}: {breaker.State}");
```
<!-- endsnippet -->

## Change the key

`ScopeBy` takes an `IMethod` and returns the key:

| `ScopeBy` | Scope | Use when |
| :--- | :--- | :--- |
| `static m => m.ServiceName` | Per service. The default. | The usual case. |
| `static m => m.FullName` | Per method. | One method has failure modes the others do not share - an expensive report next to a cheap lookup. |
| `null` | One scope for the whole client. | The client fronts one coherent service and you want its breaker to see every call. |

The registry is bounded by `MaximumScopes`, which defaults to 1024 - far above the method count of any real service. Keys past that bound drop the least-recently-seen entries. There is no unbounded mode: unbounded keying is a memory leak with a breaker and a budget on every entry.

## Where the breaker comes from

`BreakerPerScope` is on by default, so each scope gets a breaker built from `BreakerSettings` even though the shipped preset carries none. This mirrors what `AddResilience()` does per host, and it is why moving a client from HTTP to gRPC does not silently lose its breaker.

A policy that already carries a `Breaker` keeps it: an explicit breaker is a deliberate scope decision, and this switch does not overrule it. That breaker then acts as a *prototype* - each key gets one of its own with those settings, because sharing a single breaker's state across every key would defeat the point of keying.

## Where the budget comes from

`BudgetPerScope` is on by default, and for the same reason `BreakerPerScope` is: a storm against one service must not throttle retries to another. It is the gRPC counterpart of the HTTP handler's [`BudgetPerHost`](../http/per-host-scope.md).

`RetryBudget.Automatic` - the shipped default - means "no scope decision was made", so each key gets its own budget. A [shared budget](../features/retry-budget.md) is a deliberate decision and is left as is, `RetryBudget.None` included.

Turning `BudgetPerScope` off gives every scope one budget between them, which is the right reading for a client whose methods all front one dependency:

| Desired scope | Configuration |
| :--- | :--- |
| One budget per scope (default) | No change required. |
| One budget for the whole client | `BudgetPerScope = false`, or set `Budget` to a `RetryBudget.Of(...)` or `RetryBudget.Shared(...)` instance. |
| No retry budget | Set `Budget` to `RetryBudget.None`. |

## Where the state lives

The guards live on the interceptor, and the interceptor is registered at **channel scope**. One channel gets one set of guards for the life of the client.

That matters more than it sounds. An interceptor built per call hands every call a fresh breaker that has never seen a failure and a fresh budget that has never seen a deposit - resilience that reads as configured but provides none. `NRES005` catches it. `AddGrpcResilience()` passes the scope explicitly rather than relying on a default, so the registration cannot ship the failure it exists to prevent.

Hold an interceptor you build by hand in a `static readonly` field or a container singleton, for the same reason.

## Read the scopes from a health check

The registration adds every scope's breaker and budget to [`ResilienceHealthOptions`](../di/health-checks.md) under the client's name, so `AddHealthChecks().AddResilienceHealthCheck()` reports them with no wiring of yours. An operator gets told *which* dependency opened, rather than that something did.
