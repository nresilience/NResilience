---
title: Quick start
description: Install the package, retry an HTTP call, and read the outcome.
order: 1
---

# Quick start

```bash
dotnet add package NResilience
```

<!-- snippet: quick-start-first-call -->
```csharp
var api = Resilience.Http;

User? user = await api.RunAsync(ct => client.GetFromJsonAsync<User>(url, ct), cancellationToken);
```
<!-- endsnippet -->

That is a working retried call. `Resilience.Http` gives you three attempts, exponential backoff with
full jitter, a 30-second deadline, a 10-second ceiling on any one attempt, and a classifier that
knows a 503 is worth retrying and a 404 is not.

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
CallResult<User> result = await api.TryRunAsync(ct => FetchAsync(ct), cancellationToken);

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

