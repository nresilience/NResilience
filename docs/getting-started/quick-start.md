---
title: Quick start
description: Install the package, retry an HTTP call, and read the outcome.
order: 1
---

# Quick start

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

That is a working retried client, and the only cancellation token in it is your own.
`CreateClient()` puts the [`Resilience.Http`](../reference/resilience.md) preset in front of the
transport: three attempts, exponential backoff with full jitter, a 30-second deadline, a 10-second
attempt timeout, and a classifier that knows a 503 is worth retrying and a 404 is not.

The client's [handler](../http/index.md) does the HTTP-specific part: it rebuilds the request for
every attempt, keeps POST out of the retry path, and scopes the breaker to the host.

## Run any call, not just HTTP

HTTP gets a client because it is the common case. Everything else - a queue read, a database call, a
third-party SDK - goes through `RunAsync`, which takes the work as a callback.

<!-- snippet: quick-start-run-any-call -->
```csharp
var api = Resilience.Default;

string name = await api.RunAsync(attempt => db.ReadNameAsync(id, attempt), cancellationToken);
```
<!-- endsnippet -->

The callback takes a token of its own, and it is not the one you passed in. `attempt` is cancelled
when that attempt hits its [`AttemptTimeout`](../features/deadlines.md); `cancellationToken` is
yours, and cancels the whole call. Passing `attempt` into your work is what lets a timed-out attempt
actually stop.

> [!TIP]
> Every execution overload requires a callback that takes a `CancellationToken`. There is no
> zero-argument form to forget, because a timeout cannot stop work that ignores its token.

## Name your policies once

A policy is a value, so the natural home for one is a `static readonly` field.

<!-- snippet: quick-start-house-policy -->
```csharp
public static class Policies
{
    public static readonly Resilience Api = Resilience.Http with
    {
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(3),
    };

    public static readonly Resilience Realtime = Api with
    {
        Attempts = 1,
        AttemptTimeout = TimeSpan.FromMilliseconds(250),
    };
}
```
<!-- endsnippet -->

`with` copies everything you did not mention, so `Realtime` keeps `Api`'s deadline and classifier.
There is no `Build()` and no ordering to get right.

## Read the outcome without an exception

`RunAsync` throws what the call threw. When you would rather branch than catch, use `TryRunAsync`.

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

That is what replaces a fallback strategy: a fallback is an `if`.

## Next

- [Key concepts](key-concepts.md) - the five words the rest of the docs use.
- [Retry an HTTP call](../guides/retry-an-http-call.md) - the same thing, end to end, with the
  handler.
- [`CallResult<T>`](../reference/call-result.md) - every member of what came back.

