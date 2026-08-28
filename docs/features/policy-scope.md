---
title: Keyed policy scope
description: Give every tenant, shard, or channel its own breaker, retry budget, and hedging estimate, with a bound on how many keys are kept.
order: 8
---

# Keyed policy scope

A **policy scope** keys a policy by a value - such as a tenant, shard, queue, or gRPC channel - and gives every key its own [circuit breaker](circuit-breaker.md), [retry budget](retry-budget.md), and [hedging](hedging.md) latency estimate. This provides the same mechanism as [per-host scoping](../http/per-host-scope.md) for non-HTTP calls.

Policy scopes are **opt-in**. Only the HTTP handler creates one automatically.

## Turn it on

Hold the scope where the state it keeps can outlive the call - a `static readonly` field, or a container singleton:

<!-- snippet: policy-scope-field -->
```csharp
// One scope for the process, like the breaker it holds. A scope built per call would hand every
// call a fresh breaker and a fresh budget, which is what NRES005 says.
private static readonly PolicyScope<string> Tenants = new(Resilience.Default with { Breaker = new Breaker() });
```
<!-- endsnippet -->

Then ask it for the policy for one key:

<!-- snippet: policy-scope-use -->
```csharp
// The policy for this tenant, with the tenant's own breaker and retry budget attached.
var value = await Tenants.For(tenantId).RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
```
<!-- endsnippet -->

Each key is derived on first use and cached. `For` is a dictionary lookup after that.

> [!IMPORTANT]
> A scope built per call is the same error as a breaker built per call: every call receives a fresh dictionary of guards. [NRES005](../reference/analyzers.md#nres005) reports it.

## What a key gets

| On the template | What each key gets |
| :--- | :--- |
| `Breaker = new Breaker()` | Its own breaker with those settings. The template's instance is a prototype and is never used for execution. |
| No breaker | No breaker. A scope with no breaker still gives each key its own budget and latency estimate. |
| `Budget = null` or `RetryBudget.Automatic` | Its own retry budget. |
| `Budget = RetryBudget.Shared("name")` | The shared budget. Use `Shared` to maintain a single retry ceiling across all keys. |
| `Hedge = Hedge.At(0.95)` | Its own latency estimate. A slow tenant does not lower the hedge threshold for a fast one. |
| `Name = "orders"` | `Name = "orders:<key>"`, so a log line says which key it came from. |

A policy scope does not share a single breaker's state across keys. To share a breaker, use a policy instead of a scope.

## Shape a key, and bound the set

<!-- snippet: policy-scope-shape -->
```csharp
private static readonly PolicyScope<string> Shards = new(
    Resilience.Default with { Breaker = new Breaker() },

    // Run once per key, on first sight. The per-key breaker and budget are derived from whatever
    // it returns, so shaping a key does not cost it guards of its own.
    shape: key => Resilience.Default with
    {
        Breaker = new Breaker(),
        Attempts = key == "reporting" ? 1 : 3,
    },

    // How many keys to keep. The least-recently-seen are dropped past this.
    maxKeys: 64);
```
<!-- endsnippet -->

Unbounded keying is a memory leak because every entry includes a breaker and a budget. Therefore, there is no unbounded mode: the default is 1024, and the minimum is 1.

Eviction is the same approximation the host registry uses: a key seen since the last sweep survives the next one, the scope can briefly hold more than `maxKeys` while a sweep catches up, and no lookup ever waits on a sweep.

> [!IMPORTANT]
> Eviction discards state. A dropped key that comes back gets a fresh breaker, which does not remember that it was open. Size `maxKeys` above the number of keys you expect to be active at once.

## Read what it produces

<!-- snippet: policy-scope-inspect -->
```csharp
// For a health endpoint: a breaker whose scope is a key with a name is one an operator can
// be told about.
foreach (var (tenant, breaker) in Tenants.Breakers())
    Console.WriteLine(value: $"{tenant}: {breaker.State}");
```
<!-- endsnippet -->

`Breakers()` and `Budgets()` are snapshots using the scope's keys. `Count` is the number of current keys; `Template` and `MaxKeys` are the initial configuration.

## Go deeper

- [Per-host scope](../http/per-host-scope.md) - the same mechanism, keyed by host, on by default for HTTP.
- [Circuit breaker](circuit-breaker.md) - what a per-key breaker does once it has one.
- [Resource isolation](../guides/resource-isolation.md) - bounding a dependency's concurrency, which composes with a scope rather than replacing it.
