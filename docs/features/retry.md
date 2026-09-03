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

## Measure the backoff base instead of guessing it

`transientBase` is a guess about a dependency the library is already measuring. Against a dependency whose normal call takes three seconds, 100 ms is not backoff - the retry lands while the first attempt's work is very likely still queued. Against one that answers in two milliseconds, it spends 100 ms of the deadline doing nothing.

`Backoff.Measured(...)` sets the transient base to a multiple of what a call to this dependency recently took, and changes nothing else - the throttled base, the factor, the jitter, the cap, and server pushback all behave exactly as they do on `Backoff.Exponential`.

<!-- snippet: retry-backoff-adaptive -->
```csharp
var api = Resilience.Http with
{
    // "Wait about one normal call before retrying", instead of a millisecond count that is
    // only right for one dependency. The measured base is clamped to a factor of 10 either
    // side of the 100 ms written here, so the constant stays the anchor.
    Backoff = Backoff.Measured(multiple: 1, transientBase: TimeSpan.FromMilliseconds(value: 100)),
};
```
<!-- endsnippet -->

Unlike the [measured attempt ceiling](deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it), this estimate is not tighten-only: a longer backoff during a brownout is arguably correct, and it also lengthens every call's wall-clock time during the incident. So the measured base is clamped to `Spread` either side of the base you configured, and the constant you wrote stays the anchor.

`Backoff.Measured(1)` is a complete configuration. The properties you can change, through `Backoff.MeasuredBase`:

| Property | Default | Description |
| :--- | :--- | :--- |
| `Multiple` | none - you supply it | How many normal calls the first retry waits. Must be greater than zero. |
| `Quantile` | `0.5` | The quantile of recent successful latency that counts as normal. Capped at `0.5`. |
| `Window` | `5 min` | How much history the baseline covers. |
| `MinimumSamples` | `20` | How many recent successful calls the baseline needs before it moves anything. |
| `Spread` | `10` | How far the measured base may move from `transientBase`, as a factor in either direction. Must be greater than 1. |

Four behaviors are worth knowing:

- **A cold process does not guess.** Below `MinimumSamples` there is no measured base and the retry waits `transientBase` unchanged.
- **Throttling keeps its constant.** A rate limiter that answers in two milliseconds is telling you about its token bucket, not about how long to wait. Where the server does know, it says so, and `Retry-After` already wins over every curve.
- **Only successful attempts are sampled.** A dependency failing fast has a very short latency distribution, and a base measured from it would turn the retry curve into a tight loop at the moment the dependency could least afford one.
- **The estimate is per policy instance.** The [HTTP handler](../http/index.md) derives one policy per host, so each host's base is measured from that host's own latency. A policy rebuilt per call never warms its estimate - [`NRES008`](../reference/analyzers.md#nres008) reports that shape.

Read the current value from `MeasuredBackoffBase`, or watch the `nresilience.backoff.base` histogram, which is recorded when the number moves. Both report the base after the clamp, so the gap between it and your `transientBase` is how wrong the constant was.

> [!NOTE]
> This is opt-in. It is the one measured term the library does not turn on for you, because it is the one that can lengthen a delay rather than only shorten one. `Adaptive = false` on the policy refuses it alongside every other measured term.

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
