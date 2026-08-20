---
title: NResilience
description: A dependency that goes slow or starts failing can hang your requests, tie up your threads, and take your own application down - retry, timeouts, and circuit breaking for .NET calls, on by default.
order: 0
---

# NResilience

A dependency that goes slow or starts failing can hang your requests, tie up your threads, and take
your own application down with it - and retrying blindly only makes that worse, because every caller
hits the failing service again at the same time. NResilience adds retry, timeouts, and circuit
breaking to a .NET call so a struggling dependency degrades your app instead of crashing it - with
sensible defaults already on, so a working retried HTTP call is one line, and every call you tune
after that is one `with` expression, not a builder chain.

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

## What it gives you

- Retries when a call fails transiently, with backoff (a short wait before each retry) and jitter
  (random spacing so clients don't all retry at once)
- Timeouts so a slow dependency can't hang your application
- A circuit breaker - a switch that stops calling a dependency when it's failing, so you don't pile
  on load it can't handle
- A retry budget - a cap on retries as a fraction of traffic, so a fleet of clients can't overwhelm
  a struggling dependency
- HTTP-aware out of the box (knows a 503 is retryable, a 404 is not)
- Works with zero configuration - sensible defaults are already on

Overhead is one allocation per call, gated in CI. The measured values per framework and the gate
source are in [where the allocations are](deep-dives/allocations.md).
