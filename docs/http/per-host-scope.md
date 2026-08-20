---
title: Per-host scope
description: One breaker and one budget per host, so a dead host does not trip calls to the healthy ones.
order: 2
---

# Per-host scope

If one `HttpClient` talks to several hosts and one of them goes down, a single shared breaker would
trip calls to all the healthy hosts too - a problem in one place taking down everything. The handler
solves this by keeping a separate breaker and retry budget per host, so a dead host only affects
calls to that host.

`BreakerPerHost` and `BudgetPerHost` are **on by default**. The handler keeps one breaker and one
retry budget per authority it has seen, created on first use.

> [!WARNING]
> The registry is unbounded: the set of hosts one `HttpClient` talks to is a property of the
> application rather than of its traffic, and an eviction policy over a dozen entries would be a cache
> with a bug in it. A client that talks to an **unbounded** set of hosts - a proxy, a crawler, a
> webhook dispatcher - should set `BreakerPerHost` and `BudgetPerHost` to false and scope the guards on
> the policy instead.

## Reading it

<!-- snippet: http-per-host -->
```csharp
// A breaker whose scope is a variable with a name is one an operator can be told about.
IReadOnlyDictionary<string, Breaker> breakers = handler.BreakersByHost();
IReadOnlyDictionary<string, RetryBudget> budgets = handler.BudgetsByHost();

foreach ((string host, Breaker breaker) in breakers)
{
    Console.WriteLine($"{host}: {breaker.State} since {breaker.OpenedAt:O}");
}
```
<!-- endsnippet -->

Both return a snapshot of the hosts this handler has actually seen, which is what a health endpoint
needs. The dictionaries are empty until the first request to a host, and empty for good when the
switch is off and the policy carries no breaker of its own.

## When you want a different scope

A policy that already carries a [`Breaker`](../features/circuit-breaker.md) keeps it: an explicit
breaker is a scope decision and the switch does not overrule it. The same is true of an
explicit `Budget`, including `RetryBudget.None`.

So the three scopes available are:

| You want | Do this |
| --- | --- |
| One breaker per host (the default) | Nothing |
| One breaker for the whole client, across hosts | Set `Breaker` on the policy |
| No breaking | Leave `Breaker` null and set `BreakerPerHost = false` |

## Two details worth knowing

**The per-host policy is renamed.** A policy called `orders` reports as `orders:orders.example` in
events and telemetry tags, and the breaker itself is named after the host. That is what lets one
dashboard separate a client's hosts.

**A non-repeatable request runs the same policy with one attempt**, rather than no policy. The breaker
still sees the outcome and the budget still receives its deposit, and nothing is sent twice.

Handler lifetime decides how long that state lives. `IHttpClientFactory` rotates handler chains every
two minutes by default, so the breakers and budgets of a factory-built client are rotated with them;
a client from `ResilienceHttp.CreateClient` that you hold for the life of the process keeps its state
for the life of the process.

