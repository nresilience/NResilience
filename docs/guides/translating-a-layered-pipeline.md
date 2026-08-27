---
title: Translate a layered pipeline to the flat model
description: Map a traditional middleware chain - auth refresh, cache, retry, fallback - onto the flat executor's hooks and a call-site branch.
order: 6
---

# Translate a layered pipeline to the flat model

A traditional resilience stack wraps the call in middleware: an outer layer refreshes an auth token, the next checks a cache, a retry layer re-invokes the call on failure, and an outer fallback layer serves a default if everything fails. NResilience has no chain, so none of these are layers. Each concern moves to a targeted insertion point, and the fallback becomes a branch at the call site.

## Scenario

You have a call that needs four things to happen around it:

1. Refresh the auth token before the call.
2. Serve from a local cache if it is hot.
3. Retry the call on transient failure, bounded by a deadline.
4. Fall back to a known-good value if the dependency is down.

In a layered library, that is four middleware wrappers stacked around the call. In NResilience, it is one policy with a `BeforeAttempt` hook, a cache check at the top of the callback, and an `if` at the call site.

## Complete example

The following example maps the four concerns onto the flat executor. The auth refresh runs in `BeforeAttempt`, the cache check is the first thing the callback does, retry and the deadline come from the preset, and the fallback is a branch on the `CallResult<T>`.

<!-- snippet: guide-translating-a-layered-pipeline -->
```csharp
// A traditional pipeline stacks four middleware layers around the call:
//   auth-refresh -> cache-check -> retry/timeout -> fallback.
// The flat executor has no chain, so each concern moves to a targeted
// insertion point, and the fallback becomes a branch at the call site.
private static Resilience TranslatedPolicy(UserCache cache, TokenSource tokens) =>
    Resilience.Http with
    {
        // The outermost layer - "refresh the token before the call" - maps to
        // BeforeAttempt. It runs before every attempt, outside the classified
        // region. If the auth server is down, the exception escapes the loop
        // instead of being retried, which is the behavior an outer middleware
        // layer would have given.
        BeforeAttempt = next => tokens.RefreshAsync(cancellationToken: next.CancellationToken),
    };

// The callback is the seam for everything that returns a value or needs to
// run inside the classified region. A cache check belongs here, not in Admit:
// Admit returns a verdict (admit or refuse), and a cache hit is a value, not
// a verdict. Checking the cache at the top of the callback serves the hit
// without calling the dependency, and a miss falls through to the real call.
private static async Task<User?> FetchAsync(HttpClient client, UserCache cache, CancellationToken cancellationToken)
{
    if (cache.TryGet(out var cached))
        return cached;

    return await client.GetFromJsonAsync<User>(requestUri: new Uri(uriString: "https://api.example.com/users/1"), cancellationToken: cancellationToken);
}

// The outermost layer in a pipeline is usually a fallback. The flat executor
// has no outermost layer, so the fallback is an `if` at the call site:
// TryRunAsync hands back the outcome, and the caller branches on it.
private static async Task<User> ReadUserAsync(Resilience policy, UserCache cache, CancellationToken cancellationToken)
{
    var result = await policy.TryRunAsync(attempt => FetchAsync(client: Client, cache: cache, cancellationToken: attempt), cancellationToken: cancellationToken);

    return result.TryGetValue(value: out var user) && user is not null ? user : cache.LastKnownGood;
}
```
<!-- endsnippet -->

## What's happening

Each concern in the original pipeline maps to one place in the flat model. None of them is a layer.

| Pipeline layer | Flat model | Why it lives there |
| :--- | :--- | :--- |
| Auth refresh | `BeforeAttempt` | Runs before every attempt, outside the classified region. An exception escapes the loop instead of being retried, which is what an outer middleware layer would do. |
| Cache check | Top of the callback | The callback is the seam for anything that returns a value. `Admit` returns a verdict, not a value, so a cache hit does not belong there. |
| Retry, deadline, attempt timeout | The preset | `Resilience.Http` already fuses these into one execution loop. Nothing wraps the callback; the loop runs around it. |
| Fallback | A branch on `CallResult<T>` | The flat executor has no outermost layer, so the fallback is an `if` at the call site. |

### Why the cache check is not in `Admit`

`Admit` is `Func<NextAttempt, Task<Verdict>>?`. It returns a verdict - `Ok` to admit the attempt, or `Refused`/`Limited` to refuse it. A cache hit is a value, not a verdict, so it does not fit `Admit`'s contract. Checking the cache at the top of the callback serves the hit without calling the dependency, and a miss falls through to the real call. For the full reasoning, see [the callback is the seam](../deep-dives/admission-control.md#the-callback-is-the-seam).

### Why the auth refresh is not in the callback

`BeforeAttempt` runs before every attempt, including the first, but outside the classified region. If the auth server is down, the exception it throws escapes the executor entirely instead of being classified and retried. That is the behavior an outer middleware layer gives you, without the state machine that layer would allocate. For more information, see the `BeforeAttempt` reference in [retry](../features/retry.md#before-each-attempt).

## Handle the outcome

`TryRunAsync` returns a `CallResult<T>` that carries the outcome, the stop reason, and the attempt log. The fallback is a single branch on that result:

```csharp
return result.TryGetValue(out var user) ? user : cache.LastKnownGood;
```

If the call succeeded, `TryGetValue` returns the value. If it failed, `result.StopReason` tells you why - `Permanent`, `AttemptsExhausted`, `DeadlineExceeded`, `BudgetExhausted`, or `DependencyUnavailable` - and you serve the fallback. For the full list, see [`CallResult<T>`](../reference/call-result.md).

## When to go deeper

- [One flat executor](../deep-dives/one-executor.md): Why there is no chain, and the trade-offs of fusing every strategy into one method.
- [Admission control](../deep-dives/admission-control.md): The full `Admit` contract, when to use it instead of a classified exception, and why it lives in a second execution path.
- [Classification](../features/classification.md): How a classifier turns an outcome into a verdict, and how to add your own rules.
- [Retry an HTTP call](retry-an-http-call.md): A simpler guide that starts from the preset and adds nothing custom.