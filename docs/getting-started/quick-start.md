---
title: Quick start
description: Install the package, retry an HTTP call, and read the outcome.
order: 1
---

# Quick start

Install the package:

```bash
dotnet add package NResilience
```

<!-- snippet: quick-start-http-client -->
```csharp
// One client for the application's lifetime, with the policy already inside it.
private static readonly HttpClient Client = HttpResilience.CreateClient();

private static async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken) =>
    await Client.GetFromJsonAsync<User>(requestUri: new Uri(uriString: $"https://api.example.com/users/{id}"), cancellationToken: cancellationToken);
```
<!-- endsnippet -->

That is a working retried client. Your only job is to pass your own cancellation token.

`CreateClient()` uses the [`Resilience.Http`](../reference/resilience.md) preset, which provides:
- Three attempts
- Exponential backoff with full jitter
- A 30-second deadline
- A 10-second attempt timeout, tightened to three times what a call to that host recently took
- An HTTP classifier that retries `503` responses but not `404` responses

The client's [policy](../http/index.md) handles HTTP-specific logic: it rebuilds the request for every attempt, excludes POST requests from the retry path, and scopes the circuit breaker to the host.

## Run any call, not just HTTP

Wrap any asynchronous work - such as a queue read, a database call, or a third-party SDK - using `RunAsync`.

<!-- snippet: quick-start-run-any-call -->
```csharp
var api = Resilience.Default;

var name = await api.RunAsync(attempt => db.ReadNameAsync(id: id, cancellationToken: attempt), cancellationToken: cancellationToken);
```
<!-- endsnippet -->

The callback provides an `attempt` token, which differs from the `cancellationToken` you pass in:
- `attempt` is cancelled when the specific attempt hits its [`AttemptTimeout`](../features/deadlines.md).
- `cancellationToken` cancels the entire call.

Pass the `attempt` token into your work so timed-out attempts actually stop.

> [!TIP]
> Every call overload requires a callback that takes a `CancellationToken`. An [analyzer in the package](../reference/analyzers.md) notifies you at build time if you pass the wrong token to your work.

## Handle outcomes without exceptions

`RunAsync` throws the exception encountered during the call. To branch on the outcome instead of catching exceptions, use `TryRunAsync`.

<!-- snippet: quick-start-outcome -->
```csharp
var result = await api.TryRunAsync(attempt => FetchAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

if (!result.TryGetValue(value: out var user))
{
    // Why it stopped, and everything that happened on the way.
    Console.WriteLine(value: result.StopReason); // AttemptsExhausted
    Console.WriteLine(value: result.Attempts); // 2 attempts over 1.2ms: Transient IOException (0.6ms), ...
}
```
<!-- endsnippet -->

Implement your fallback policy in the `if` block, such as by serving a cached value or a default.

## Next steps

- [Key concepts](key-concepts.md) - learn the core terminology and how to organize policies.
- [Retry an HTTP call](../guides/retry-an-http-call.md) - follow an end-to-end example with the handler.
- [`CallResult<T>`](../reference/call-result.md) - explore the properties of the call outcome.
