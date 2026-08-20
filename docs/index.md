---
title: NResilience
description: Retry, timeouts, and circuit breaking for .NET calls - defaults on out of the box, one method for any callback, no builder chain.
order: 0
---

# NResilience

Add retry, timeouts, and circuit breaking to your .NET calls. Defaults are on, so a working retried
HTTP call is one line - and every call you tune after that is one `with` expression, not a builder
chain.

<!-- snippet: whole-api -->
```csharp
// 1. Start from a preset. `Resilience.Http` retries and times out an HTTP call out of the box.
var api = Resilience.Http;

// 2. Change one setting, keep the rest: `with` copies everything you did not mention.
var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };

// 3. Run any callback through one method. The token handed to your work is the attempt's own.
User? user = await api.RunAsync(attempt => client.GetFromJsonAsync<User>(url, attempt), cancellationToken);
HttpResponseMessage response = await api.RunAsync(attempt => client.GetAsync(url, attempt), cancellationToken);
await slow.RunAsync(attempt => queue.FlushAsync(attempt), cancellationToken);

// 4. Want the outcome without an exception? `TryRunAsync` hands it back to branch on.
CallResult<User> result = await api.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);
User best = result.TryGetValue(out User? fetched) ? fetched : cache.LastKnownGood;
```
<!-- endsnippet -->

## Start here

| If you want | Go to |
| --- | --- |
| A retried HTTP call in the next two minutes | [Quick start](getting-started/quick-start.md) |
| The vocabulary the rest of the docs use | [Key concepts](getting-started/key-concepts.md) |
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

Overhead is one allocation per call, gated in CI - the ceilings and the comparison against Polly are
in [where the allocations are](deep-dives/allocations.md).
