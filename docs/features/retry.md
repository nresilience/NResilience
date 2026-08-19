---
title: Retry
description: Attempt counts, the backoff curves, jitter, and the hook that runs before every attempt.
order: 1
---

# Retry

Retry is **on by default**: `Resilience.Default` and `Resilience.Http` make up to three attempts with
exponential backoff and full jitter. Whether an outcome is retried at all is the
[classifier's](classification.md) decision, not the retry loop's.

## Attempts

<!-- snippet: retry-attempts -->
```csharp
// Three attempts: try, retry, retry. Not "one call plus three retries".
var api = Resilience.Default with { Attempts = 3 };

int value = await api.RunAsync(ct => calls.NextAsync(ct), cancellationToken);
```
<!-- endsnippet -->

`Attempts` is the **total**, including the first. `Attempts = 1` means no retry. Attempt numbers in
the attempt log, in events and in `NextAttempt` are 1-based.

## Backoff

Two base delays, because throttling and transient failure need curves an order of magnitude apart:
a delay tuned for connection resets is a hostile retry rate against a rate limiter.

| Setting | Default | What it does |
| --- | --- | --- |
| `transientBase` | 100 ms | First delay after a `Transient` verdict |
| `throttledBase` | 1 s | First delay after a `Throttled` verdict |
| `factor` | 2.0 | Growth per attempt |
| `max` | 30 s | Hard cap on any single delay |

<!-- snippet: retry-backoff-tuning -->
```csharp
var api = Resilience.Http with
{
    Backoff = Backoff.Exponential(
        transientBase: TimeSpan.FromMilliseconds(200),   // the first delay after a transient failure
        throttledBase: TimeSpan.FromSeconds(2),          // the first delay after being throttled
        factor: 2,                                       // doubling
        max: TimeSpan.FromSeconds(10)),                  // the cap on any single delay
};
```
<!-- endsnippet -->

Server pushback wins over every curve. When a verdict carries a `RetryAfter` - which
`Classifier.Http` reads off a `Retry-After` header on a 429, or a 503 that supplied one - that value
is honored verbatim, capped only by `max` and by the time left on the deadline, and no jitter is
applied to it. A server telling you when to come back is better information than a client-side guess.

`Backoff.Constant(delay)` and `Backoff.None` are the other shipped shapes. `Backoff.None` retries
immediately, and is only correct when you know the dependency is not shared.

## Jitter

<!-- snippet: retry-jitter -->
```csharp
// Full jitter is the default. `None` is for tests, and rarely right even there.
var deterministic = Resilience.Default with
{
    Backoff = Backoff.Default with { Jitter = Jitter.None },
};
```
<!-- endsnippet -->

`Jitter.Full` - `random(0, computed)` - is the default and the only shape that actually destroys the
correlation between clients. `Jitter.Equal` keeps a floor under the delay. `Jitter.None` leaves a
synchronized fleet retrying in step.

## Computing the delay yourself

<!-- snippet: retry-custom-backoff -->
```csharp
var api = Resilience.Default with
{
    Backoff = Backoff.Custom(next => next.PreviousVerdict.Kind == VerdictKind.Throttled
        ? TimeSpan.FromSeconds(5)
        : TimeSpan.FromMilliseconds(50 * next.Number)),
};
```
<!-- endsnippet -->

`Backoff.Custom` receives the attempt that is about to happen: its number, the verdict and exception
that ended the previous one, the time left on the deadline, and the caller's token. A custom curve
ignores `max` and jitter - it is the whole calculation.

## Rebuilding work between attempts

A retry re-invokes your callback from the top, so anything single-use has to be rebuilt inside it.
`BeforeAttempt` is the hook for the work that surrounds it.

<!-- snippet: retry-before-attempt -->
```csharp
var api = Resilience.Http with
{
    // Runs before every attempt, including the first. The place to refresh a token or
    // rebuild a request, because a retry re-invokes the callback from the top.
    BeforeAttempt = next => tokens.RefreshAsync(next.CancellationToken),
};
```
<!-- endsnippet -->

It runs before every attempt, including the first, and its time counts against the deadline. The
HTTP handler uses the same principle internally: it clones the request for each attempt, because an
`HttpRequestMessage` may be sent once.

## Reading what happened

Every retry raises a `Retrying` event carrying the delay it is about to serve, and every attempt
lands in the [attempt log](../reference/call-result.md#attemptlog).

Go deeper: [Why one flat executor](../deep-dives/one-executor.md).

