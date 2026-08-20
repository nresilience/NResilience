---
title: Quick start
description: Install the package, retry an HTTP call, and read the outcome.
order: 1
---

# Quick start

Install the NResilience package:

```bash
dotnet add package NResilience
```

<!-- snippet: quick-start-http-client -->
```csharp
// One client for the application's lifetime, with the policy already inside it.
private static readonly HttpClient Client = ResilienceHttp.CreateClient();

private static async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken) =>
    await Client.GetFromJsonAsync<User>(new Uri($"https://api.example.com/users/{id}"), cancellationToken);
```
<!-- endsnippet -->

This creates a working retried client. Provide only your own cancellation token.

`CreateClient()` uses the [`Resilience.Http`](../reference/resilience.md) preset, which provides:
- Three attempts
- Exponential backoff with full jitter
- A 30-second deadline
- A 10-second attempt timeout
- An HTTP classifier that retries `503` responses but not `404` responses

The client's [handler](../http/index.md) handles HTTP-specific logic: it rebuilds the request for every attempt, excludes POST requests from the retry path, and scopes the circuit breaker to the host.

## Run any call, not just HTTP

Wrap any asynchronous work - such as a queue read, a database call, or a third-party SDK - using `RunAsync`.

<!-- snippet: quick-start-run-any-call -->
```csharp
var api = Resilience.Default;

string name = await api.RunAsync(attempt => db.ReadNameAsync(id, attempt), cancellationToken);
```
<!-- endsnippet -->

The callback provides an `attempt` token, which differs from the `cancellationToken` you pass in:
- `attempt` is cancelled when the specific attempt hits its [`AttemptTimeout`](../features/deadlines.md).
- `cancellationToken` cancels the entire operation.

Pass the `attempt` token into your work to ensure that timed-out attempts actually stop.

> [!TIP]
> Every execution overload requires a callback that takes a `CancellationToken`. An [analyzer in the package](../reference/analyzers.md) notifies you at build time if you pass the wrong token to your work.

## Handle outcomes without exceptions

`RunAsync` throws the exception encountered during the call. To branch on the outcome instead of catching exceptions, use `TryRunAsync`.

<!-- snippet: quick-start-outcome -->
```csharp
CallResult<User> result = await api.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);

if (!result.TryGetValue(out User? user))
{
    // Why it stopped, and everything that happened on the way.
    Console.WriteLine(result.StopReason);   // AttemptsExhausted
    Console.WriteLine(result.Attempts);     // 2 attempts over 1.2ms: Transient IOException (0.6ms), ...
}
```
<!-- endsnippet -->

Implement your fallback strategy in the `if` block, such as by serving a cached value or a default.

## Next steps

- [Key concepts](key-concepts.md) - learn the core terminology and how to organize policies.
- [Retry an HTTP call](../guides/retry-an-http-call.md) - follow an end-to-end example with the handler.
- [`CallResult<T>`](../reference/call-result.md) - explore the properties of the call outcome.
