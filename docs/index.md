---
title: NResilience
description: Prevent cascading failures in your .NET applications with built-in retries, timeouts, and circuit breakers.
order: 0
---

# NResilience

Prevent cascading failures in your .NET applications.

A struggling dependency can hang your requests, tie up your threads, and crash your application. Blind retries make it worse by piling onto the failing service. NResilience wraps your calls in retries, timeouts, and circuit breakers so your application degrades gracefully instead of crashing.

## Why NResilience?

NResilience replaces fluent builders, strategy ordering, and mandatory `Build()` calls with values and C# `with` expressions.

- **No fluent builders.** Configure policies with `with` expressions: change one setting, keep the rest.
- **Sensible defaults.** A working, retried HTTP call in one line of code.
- **One execution method.** `RunAsync` works for HTTP calls, database queries, or queue reads.
- **Retry budget.** Caps retries as a fraction of traffic, on by default, so a fleet of clients cannot overwhelm a struggling dependency.
- **Production-ready.** Built-in analyzers catch common mistakes, such as passing the wrong cancellation token.
- **Native AOT compatible.** Zero external dependencies and no reflection.

## Get started

Add NResilience to your project:

```bash
dotnet add package NResilience
```

For most HTTP scenarios, use the pre-configured client:

```csharp
// Create one client for the application's lifetime
private static readonly HttpClient Client = HttpResilience.CreateClient();

private static async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken) =>
    await Client.GetFromJsonAsync<User>(new Uri($"https://api.example.com/users/{id}"), cancellationToken);
```

Every call this client makes uses three attempts with exponential backoff, a 30-second deadline, and HTTP-aware retry logic (for example, it retries a `503` but not a `404`).

## One method for any callback

<!-- snippet: whole-api -->
```csharp
// 1. Start from a preset. `Resilience.Http` retries and times out an HTTP call out of the box.
var api = Resilience.Http;

// 2. Change one setting, keep the rest: `with` copies everything you did not mention.
var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(value: 20) };

// 3. Run any callback through one method. The token handed to your work is the attempt's own.
var user = await api.RunAsync(attempt => client.GetFromJsonAsync<User>(requestUri: url, cancellationToken: attempt),
    cancellationToken: cancellationToken);

var response = await api.RunAsync(attempt => client.GetAsync(requestUri: url, cancellationToken: attempt), cancellationToken: cancellationToken);
await slow.RunAsync(attempt => queue.FlushAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

// 4. Want the outcome without an exception? `TryRunAsync` hands it back to branch on.
var result = await api.TryRunAsync(attempt => FetchAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
var best = result.TryGetValue(value: out var fetched) ? fetched : cache.LastKnownGood;
```
<!-- endsnippet -->

The `attempt` token is cancelled when the specific attempt hits its timeout, while the `cancellationToken` cancels the entire operation.

## Handle failures without exceptions

Use `TryRunAsync` to branch on the outcome instead of catching exceptions:

```csharp
CallResult<User> result = await api.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);
User best = result.TryGetValue(out User? fetched) ? fetched : cache.LastKnownGood;
```

## Performance and correctness

Built for high-performance .NET applications:

- **Low overhead.** One flat execution path, so cost does not grow as you add policy settings.
- **Built-in analyzers.** Seven diagnostics ship with the package to prevent silent failures.
- **Native AOT.** Works with `net8.0` and `net10.0` trimming and AOT publishing.

## Start here

| If you want | Go to |
| :--- | :--- |
| A retried HTTP call in two minutes | [Quick start](getting-started/quick-start.md) |
| The core terminology | [Key concepts](getting-started/key-concepts.md) |
| Worked scenarios for common patterns | [Guides](guides/index.md) |
| Detailed configuration options | [Features](features/index.md) |
| `AddResilience()` on a client | [Dependency injection](di/index.md) |
| A retried gRPC client | [gRPC](grpc/index.md) |
| Every member, in order | [Reference](reference/index.md) |
| Architecture and design decisions | [Deep dives](deep-dives/index.md) |
| To move off Polly | [Migrating from Polly](migrating-from-polly.md) |

Overhead is one allocation per call, gated in CI. For details, see [Where the allocations are](deep-dives/allocations.md).
