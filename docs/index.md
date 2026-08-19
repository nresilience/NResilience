---
title: NResilience
description: A .NET resilience library built around one flat execution engine, a declarative policy value, and defaults that are correct without configuration.
order: 0
---

# NResilience

A policy is a value. You derive variants with `with`, run any callback through one method, and get
defaults that are already right.

<!-- snippet: whole-api -->
```csharp
// 1. A policy is a value. Presets are the entry point.
var api = Resilience.Http;

// 2. Derive with `with`. No builder, no Build(), no ordering to get right.
var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };

// 3. Run anything. One method, any return type, nothing to declare.
User? user = await api.RunAsync(ct => client.GetFromJsonAsync<User>(url, ct), cancellationToken);
HttpResponseMessage response = await api.RunAsync(ct => client.GetAsync(url, ct), cancellationToken);
await slow.RunAsync(ct => queue.FlushAsync(ct), cancellationToken);

// 4. Fallback is not a strategy. It is an `if`.
CallResult<User> result = await api.TryRunAsync(ct => FetchAsync(ct), cancellationToken);
User best = result.TryGetValue(out User? fetched) ? fetched : cache.LastKnownGood;
```
<!-- endsnippet -->

There is no pipeline, no builder, no strategy, no context, no property bag and no ordering.

## Start here

| If you want | Go to |
| --- | --- |
| A retried HTTP call in the next two minutes | [Quick start](getting-started/quick-start.md) |
| The five words the rest of the docs use | [Key concepts](getting-started/key-concepts.md) |
| A worked scenario | [Guides](guides/index.md) |
| One knob explained | [Features](features/index.md) |
| `AddResilience()` on a client | [Dependency injection](di/index.md) |
| Every member, in order | [Reference](reference/index.md) |
| Why it is built this way | [Deep dives](deep-dives/index.md) |
| To move off Polly | [Migrating from Polly](migrating-from-polly.md) |

## What is already on

`Resilience.Default` and `Resilience.Http` retry three times with exponential backoff and full
jitter, bound the whole call to 30 seconds and any one attempt to 10, and keep a retry budget so a
failing dependency cannot turn your client into a load generator. Nothing has to be configured for
that to be true.

Two things are deliberately off until you ask: the [circuit breaker](features/circuit-breaker.md),
because its scope is a decision only you can make, and
[telemetry](features/telemetry.md), because a listener you did not attach should cost nothing.

## What it costs

Bytes above an identical un-wrapped callback, measured in one process on .NET 8 and .NET 10.

| Scenario | Overhead |
| --- | ---: |
| `Resilience.None`, any callback | **0** |
| Sync-completing, full policy | 64 B - one linked cancellation source |
| Suspending, full policy | **384 B** - one state-machine box plus that source |
| Suspending, Polly retry + timeout, same harness | 1,291 B |

Every figure is a test that fails the build. "Zero allocation" is never claimed unqualified: every
`async` method that actually awaits allocates a state machine, and no library-side trick removes it.

Go deeper: [where the allocations are](deep-dives/allocations.md).

