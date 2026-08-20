# NResilience

Prevent cascading failures in your .NET applications.

A struggling dependency can hang your requests, tie up your threads, and crash your application. Blind retries often make the problem worse by overwhelming the failing service. NResilience wraps your calls in retries, timeouts, and circuit breakers so your app degrades gracefully instead of crashing.

## Why NResilience?

NResilience replaces complex fluent builders, confusing strategy ordering, and mandatory `Build()` calls with simple values and C# `with` expressions.

- **No fluent builders.** Configure policies using `with` expressions to change one setting while keeping the rest.
- **Sensible defaults.** Get a working, retried HTTP call with one line of code.
- **One method for everything.** Use `RunAsync` for HTTP calls, database queries, or queue reads.
- **Production-ready.** Built-in analyzers catch common mistakes, such as passing the wrong cancellation token.
- **AOT and trimming safe.** Zero external dependencies and no reflection.

## Get started

To add NResilience to your project, run this command:

```bash
dotnet add package NResilience
```

For most HTTP scenarios, use the pre-configured client:

```csharp
// Create one client for the application's lifetime
private static readonly HttpClient Client = ResilienceHttp.CreateClient();

private static async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken) =>
    await Client.GetFromJsonAsync<User>(new Uri($"https://api.example.com/users/{id}"), cancellationToken);
```

Every call this client makes now uses three attempts with exponential backoff, a 30-second deadline, and HTTP-aware retry logic (for example, it retries a `503` but not a `404`).

## Simple configuration

Policies in NResilience are values. You can derive a new policy from an existing one using a `with` expression:

```csharp
var slowPolicy = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };
```

## Beyond HTTP

Use `RunAsync` to wrap any asynchronous work, such as a database call or a third-party SDK:

```csharp
var api = Resilience.Default;

string name = await api.RunAsync(attempt => db.ReadNameAsync(id, attempt), cancellationToken);
```

The `attempt` token is cancelled when the specific attempt hits its timeout, while the `cancellationToken` cancels the entire operation.

## Handle failures without exceptions

Use `TryRunAsync` to branch on the outcome instead of catching exceptions:

```csharp
CallResult<User> result = await api.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);
User best = result.TryGetValue(out User? fetched) ? fetched : cache.LastKnownGood;
```

## Performance and correctness

NResilience is built for high-performance .NET applications:

- **Low overhead.** A flat execution path ensures that cost doesn't grow as you add more policy settings.
- **Built-in analyzers.** Seven diagnostics ship with the package to prevent silent failures.
- **Native AOT.** Fully compatible with `net8.0` and `net10.0` trimming and AOT publishing.

## Documentation

For more information, see these resources:

- [Quick start](docs/getting-started/quick-start.md) - get a retried HTTP call working in two minutes.
- [Key concepts](docs/getting-started/key-concepts.md) - learn the core terminology.
- [Guides](docs/guides/index.md) - see worked scenarios for common patterns.
- [Features](docs/features/index.md) - detailed explanations of every configuration option.
- [Migrating from Polly](docs/migrating-from-polly.md) - a translation guide and behavioral differences.

## Packages

| Package | Use case |
|---|---|
| `NResilience` | The core library, HTTP handler, and analyzers. |
| `NResilience.Extensions` | Dependency injection, configuration binding, and metrics for hosted apps. |
| `NResilience.Testing` | Helpers for testing your policies. |
