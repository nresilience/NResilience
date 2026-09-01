---
title: Retry
description: Configure attempt counts, backoff curves, jitter, and the hook that runs before every attempt.
order: 1
---

# Retry

Retry is on by default: `Resilience.Default` and `Resilience.Http` make up to three attempts using exponential backoff with full jitter. Exponential backoff grows the delay after each attempt, and jitter randomizes it so many clients don't retry simultaneously. The [classifier](classification.md) decides which outcomes get retried.

## Configure attempts

The `Attempts` property is the total number of attempts, including the first call: `Attempts = 1` means no retries. Attempt numbers in the attempt log, events, and `NextAttempt` are 1-based.

<!-- snippet: retry-attempts -->
```csharp
// Three attempts: try, retry, retry. Not "one call plus three retries".
var api = Resilience.Default with { Attempts = 3 };

var value = await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
```
<!-- endsnippet -->

## Configure backoff

There are two base delays because throttling and transient failures want different pacing. A delay tuned for connection resets is likely too aggressive for a rate limiter.

| Setting | Default | Description |
| :--- | :--- | :--- |
| `transientBase` | 100 ms | The first delay after a `Transient` verdict |
| `throttledBase` | 1 s | The first delay after a `Throttled` verdict |
| `factor` | 2.0 | The growth rate per attempt |
| `max` | 30 s | The maximum limit for any single delay |

<!-- snippet: retry-backoff-tuning -->
```csharp
var api = Resilience.Http with
{
    Backoff = Backoff.Exponential(
        transientBase: TimeSpan.FromMilliseconds(value: 200), // the first delay after a transient failure
        throttledBase: TimeSpan.FromSeconds(value: 2), // the first delay after being throttled
        factor: 2, // doubling
        max: TimeSpan.FromSeconds(value: 10)), // the cap on any single delay
};
```
<!-- endsnippet -->

Server pushback overrides the backoff curve. If a verdict carries a `RetryAfter` value - which `Classifier.Http` extracts from the `Retry-After` header on 429 or 503 responses - NResilience uses it exactly. The only limits are the `max` setting and the remaining deadline; no jitter is applied.

`Backoff.Constant(delay)` and `Backoff.None` are also available. Use `Backoff.None` only if you know the dependency is not shared.

## Configure jitter

When many clients retry at the same time - right after a brief outage, say - they create a second traffic spike. Jitter prevents that by adding a random component to each delay.

<!-- snippet: retry-jitter -->
```csharp
// Full jitter is the default. `None` is for tests, and rarely right even there.
var deterministic = Resilience.Default with
{
    Backoff = Backoff.Default with { Jitter = Jitter.None },
};
```
<!-- endsnippet -->

`Jitter.Full` is the default: it picks a random value between 0 and the computed delay, which removes correlation between clients. `Jitter.Equal` keeps a minimum delay, and `Jitter.None` produces synchronized retries.

## Compute custom delays

`Backoff.Custom` defines your own delay logic.

<!-- snippet: retry-custom-backoff -->
```csharp
var api = Resilience.Default with
{
    Backoff = Backoff.Custom(next => next.PreviousVerdict.Kind == VerdictKind.Throttled
        ? TimeSpan.FromSeconds(value: 5)
        : TimeSpan.FromMilliseconds(value: 50 * next.Number)),
};
```
<!-- endsnippet -->

The `Backoff.Custom` function receives the next attempt's details: its number, the previous verdict and exception, the remaining deadline time, and the cancellation token. Custom curves ignore the `max` setting and jitter.

## Rebuild work between attempts

A retry re-invokes your callback from the beginning, so rebuild any single-use objects inside the callback. The `BeforeAttempt` hook is the place for setup work.

<!-- snippet: retry-before-attempt -->
```csharp
var api = Resilience.Http with
{
    // Runs before every attempt, including the first. The place to refresh a token or
    // rebuild a request, because a retry re-invokes the callback from the top.
    BeforeAttempt = next => tokens.RefreshAsync(cancellationToken: next.CancellationToken),
};
```
<!-- endsnippet -->

The `BeforeAttempt` hook runs before every attempt, including the first, and its execution time counts toward the deadline. The HTTP policy uses the same principle internally to clone requests, because an `HttpRequestMessage` can only be sent once.

## Analyze retry results

Every retry raises a `Retrying` event that includes the delay. The [attempt log](../reference/call-result.md#attemptlog) records every attempt.

For the architecture behind this, see [Why one flat executor](../deep-dives/one-executor.md).
