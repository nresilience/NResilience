---
title: Retry
description: Configure attempt counts, backoff curves, jitter, and the hook that runs before every attempt.
order: 1
---

# Retry

Retry is enabled by default. For example, `Resilience.Default` and `Resilience.Http` make up to three attempts using exponential backoff and full jitter. Exponential backoff increases the delay between each attempt, and full jitter randomizes the delay to prevent multiple clients from retrying simultaneously. The [classifier](classification.md) determines whether an outcome is retried.

## Configure attempts

The `Attempts` property specifies the total number of attempts, including the first call. For example, `Attempts = 1` means no retries occur. Attempt numbers in the attempt log, events, and `NextAttempt` are 1-based.

<!-- snippet: retry-attempts -->
```csharp
// Three attempts: try, retry, retry. Not "one call plus three retries".
var api = Resilience.Default with { Attempts = 3 };

var value = await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
```
<!-- endsnippet -->

## Configure backoff

NResilience provides two base delays to handle throttling and transient failures differently. A delay tuned for connection resets might be too aggressive for a rate limiter.

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

Server pushback overrides the backoff curve. If a verdict contains a `RetryAfter` value - which `Classifier.Http` extracts from the `Retry-After` header on 429 or 503 responses - NResilience honors that value exactly. The only limits are the `max` setting and the remaining time on the deadline; no jitter is applied.

You can also use `Backoff.Constant(delay)` or `Backoff.None`. Use `Backoff.None` only if you know the dependency is not shared.

## Configure jitter

When many clients retry simultaneously - for example, after a brief outage - they can create a second spike of traffic. Jitter prevents this by adding a random component to each delay.

<!-- snippet: retry-jitter -->
```csharp
// Full jitter is the default. `None` is for tests, and rarely right even there.
var deterministic = Resilience.Default with
{
    Backoff = Backoff.Default with { Jitter = Jitter.None },
};
```
<!-- endsnippet -->

`Jitter.Full` is the default. It calculates a random value between 0 and the computed delay, which effectively removes correlation between clients. `Jitter.Equal` maintains a minimum delay, and `Jitter.None` results in synchronized retries.

## Compute custom delays

Use `Backoff.Custom` to define your own delay logic.

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

The `Backoff.Custom` function receives the details of the next attempt, including its number, the previous verdict and exception, the remaining deadline time, and the cancellation token. Custom curves ignore the `max` setting and jitter.

## Rebuild work between attempts

A retry re-invokes your callback from the beginning, so you must rebuild any single-use objects inside the callback. Use the `BeforeAttempt` hook to perform setup work.

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

The `BeforeAttempt` hook runs before every attempt, including the first, and its execution time counts toward the deadline. This is the same principle the HTTP handler uses internally to clone requests, as an `HttpRequestMessage` can only be sent once.

## Analyze retry results

Every retry triggers a `Retrying` event that includes the delay. You can also find every attempt in the [attempt log](../reference/call-result.md#attemptlog).

For a more detailed explanation of the architecture, see [Why one flat executor](../deep-dives/one-executor.md).
