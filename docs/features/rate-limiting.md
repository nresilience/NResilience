---
title: Rate limiting
description: Bound the absolute rate or concurrency of outbound calls, before anything has gone wrong.
order: 6
---

# Rate limiting

A **limiter** bounds what leaves this process: the absolute rate of outbound calls, or how many run at once. It is opt-in, and it is the only guard that acts before anything has gone wrong.

It is a different guard from the two the library turns on for you. The [circuit breaker](circuit-breaker.md) reacts to evidence that a dependency is unhealthy. The [retry budget](retry-budget.md) bounds retries as a *fraction* of traffic. Neither bounds an absolute number, so neither stops you from exceeding a published quota or opening 500 concurrent connections to one host.

## Turn it on for an HTTP client

<!-- snippet: limit-http -->
```csharp
services.AddHttpClient(name: "api")
    .AddResilience() // outer: makes the attempts
    .AddRateLimit(o =>
    {
        o.PermitsPerSecond = 100; // one of four shapes; set exactly one
        o.PerHost = true; // the default, scoped like the breakers
    });
```
<!-- endsnippet -->

> [!IMPORTANT]
> `AddRateLimit` goes **after** `AddResilience`. Handlers run in registration order, outermost first, so this is what puts the limiter inside the retry loop, where it takes one permit per attempt.

The other order takes one permit for an operation that then makes three calls, so it is refused rather than accepted.

<!-- snippet: limit-order -->
```csharp
// Handlers run in registration order, outermost first, so this puts the limiter *outside*
// the retries - one permit for an operation that goes on to make three calls. Refused at
// registration rather than accepted and silently wrong.
var error = Assert.Throws<ResilienceConfigurationException>(() => services.AddHttpClient(name: "api")
    .AddRateLimit(o => o.PermitsPerSecond = 100)
    .AddResilience());
```
<!-- endsnippet -->

## Turn it on for anything else

The limiter goes inside the callback, not around the call.

<!-- snippet: limit-callback -->
```csharp
// 100 calls per second, with one second of burst. The limiter is an object you hold: give
// it the lifetime of whatever it protects, and dispose it with that.
using var limiter = Limit.PerSecond(permits: 100);

var api = Resilience.Http;

var value = await api.RunAsync(async ct =>
{
    // Inside the callback, not around the call. Retry re-invokes the callback, so a permit
    // taken here is taken once per attempt - and `using` is what releases a concurrency
    // permit when the attempt ends, however it ends.
    using var lease = await limiter.AcquireOrThrowAsync(cancellationToken: ct);
    return await FetchAsync(cancellationToken: ct);
});
```
<!-- endsnippet -->

`ct` is the attempt's token, already `min(AttemptTimeout, remaining deadline)` linked with the caller's. A limiter that waits is therefore bounded by the policy's own time budget with nothing further to configure.

## Choose a shape

<!-- snippet: limit-shapes -->
```csharp
// A published per-second quota.
using var perSecond = Limit.PerSecond(permits: 100);

// A longer quota. The window slides in eight segments, so you cannot spend it all at the
// end of one window and all of the next at the start of the following one.
using var perMinute = Limit.PerWindow(permits: 1_000, window: TimeSpan.FromMinutes(value: 1));

// The bulkhead: at most 20 calls in flight at once, whatever their rate.
using var inFlight = Limit.Concurrency(permits: 20);

// The bulkhead you do not have to size. Set the range it may move within; the number
// inside it is measured from how the dependency responds under load.
using var adaptive = Limit.Adaptive(new AdaptiveLimitOptions { Minimum = 4, Maximum = 200 });
```
<!-- endsnippet -->

Set exactly one of them in `RateLimitOptions`. Asking for two is a configuration error; the library does not pick one for you.

<!-- snippet: limit-validate -->
```csharp
// Four different guards, and a section that asks for two of them is a section whose
// author expected one to win. Every problem is listed at once.
var error = Assert.Throws<ResilienceConfigurationException>(() => new RateLimitOptions { PermitsPerSecond = 100, Concurrency = 20 }.Validate());
```
<!-- endsnippet -->

## Let it find its own concurrency

`Limit.Concurrency(50)` is right on one pod and wrong on a hundred. The arithmetic that makes it right - the dependency's ceiling divided by the expected pod count - goes stale on the next scaling change, and nobody revisits it.

`Limit.Adaptive` measures instead. Latency under load reveals queueing, the only observable difference between a dependency that is keeping up and one that is not.

<!-- snippet: limit-adaptive -->
```csharp
// No permit count. Minimum and Maximum are guardrails - what the loop may never leave -
// and the limit between them is read from latency: a round of calls slower than this
// dependency normally is means a queue downstream, and the limit backs off.
using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Minimum = 4, Maximum = 200 }, name: "payments");

var api = Resilience.Http;

var value = await api.RunAsync(async ct =>
{
    // The lease is the measurement: how long the permit is held is the round-trip time
    // the control loop reads. `using` is what frees the slot *and* reports the sample.
    using var lease = await limiter.AcquireOrThrowAsync(name: "payments", cancellationToken: ct);
    return await FetchAsync(cancellationToken: ct);
});

// What it has settled on, for a dashboard. Null until it has seen enough calls to have
// an opinion, at which point it holds at Initial rather than guessing.
int discovered = limiter.CurrentLimit;
TimeSpan? normal = limiter.Baseline;
```
<!-- endsnippet -->

You set the range and the loop finds the number inside it:

| Option | Default | What it is |
| :--- | :--- | :--- |
| `Minimum` | 4 | The floor. A liveness guarantee: without one, a dependency that is slow for reasons unrelated to your concurrency drives the limit to zero and the recovery is never sampled. |
| `Maximum` | 200 | The ceiling. The one worth setting per dependency, because it is what bounds the damage when the measurement is wrong. |
| `Initial` | 20 | Where it starts, before there is anything to measure. |
| `Threshold` | 2.0 | How many times the baseline latency counts as queueing. Dimensionless, like [`SlowCalls.Above`](circuit-breaker.md). |
| `DecreaseFactor` | 0.9 | What the limit is multiplied by when a round says there is queueing. |

### How it decides

A **round** is one limit's worth of calls - so the loop reacts at the pace the dependency is actually being driven at, not on a timer. At the end of each round:

- The round's **fastest** call is compared against a **baseline**: the 10th percentile of the last five minutes. One slow call among many is a tail; even the fastest call being slow is a queue.
- Fastest above `Threshold` x baseline, and the limit is multiplied by `DecreaseFactor`.
- Otherwise, if the limit was what was actually constraining you during the round, it grows by one.

Multiplicative decrease against additive increase, in that pairing, for the reason TCP uses it: the cost of being too high is paid by the dependency, the cost of being too low by you, so the two directions must not move at the same speed.

The growth condition matters as much as the shrink one. A limiter that grew while idle would ratchet to `Maximum` during a quiet period, and the first burst afterward would meet no limit at all.

> [!NOTE]
> The baseline is measured, so it can be measured wrong. A process that starts *while the dependency is already queueing* learns the queued latency as normal and grows to `Maximum`. That is what the ceiling is for: make it a number the dependency can survive, not one you expect never to reach.

It reads its own state for a dashboard - `CurrentLimit` is the number it settled on, `Baseline` is what it thinks a fast call looks like. It also records `nresilience.limiter.limit` whenever the limit moves.

### From configuration

<!-- snippet: limit-adaptive-http -->
```csharp
services.AddHttpClient(name: "api")
    .AddResilience()
    .AddRateLimit(o =>
    {
        // The presence of the section is what turns it on - every property inside has a
        // working default, so this is a complete configuration. Per host, like the
        // breakers, because each host queues on its own.
        o.Adaptive = new AdaptiveLimitOptions { Minimum = 4, Maximum = 200 };
        o.Name = "api";
    });
```
<!-- endsnippet -->

`Adaptive` is a nested section, and its presence is what turns it on - every property inside has a working default, so `"Adaptive": {}` is a complete configuration.

## Read what a refusal does

A refusal is throttling that knows where it came from.

<!-- snippet: limit-verdict -->
```csharp
var result = await api.TryRunAsync(_ =>
    Task.FromException<int>(exception: new RateLimitedException(limiter: "payments", retryAfter: TimeSpan.FromSeconds(value: 2))));

var refused = result.Attempts[index: 0];

// Throttling, so it takes the long backoff curve and honors the limiter's own hint.
Assert.Equal(expected: VerdictKind.Throttled, actual: refused.Verdict.Kind);

// And it says where it came from. This is the bit the retry budget reads: a refusal that
// never left the process is not charged, because retrying it costs the dependency nothing.
Assert.True(condition: refused.Verdict.SelfImposed);
```
<!-- endsnippet -->

Four consequences follow from that verdict; none require configuration:

| What | Why |
| :--- | :--- |
| Retried on the throttled backoff curve | The dependency is being defended, so the long curve is the right one. |
| The limiter's own hint wins over the curve | The same rule that honors a server's `Retry-After`. |
| The [breaker](circuit-breaker.md) records nothing | Only `Transient` is evidence, and nothing reached the dependency. |
| The [retry budget](retry-budget.md) is not charged | The budget is a fraction of the dependency's traffic, and this call never joined it. |

If every attempt is refused, the call ends with `StopReason.AttemptsExhausted` and surfaces the `RateLimitedException`, whose `Limiter` and `RetryAfter` say which limiter refused and when to come back.

## Queueing

`QueueLimit` is `0` by default: a call that cannot get a permit is refused immediately rather than queued.

That is deliberate, because the library is already good at waiting. A refusal becomes a retry on the throttled curve, capped by `Backoff.Max` and by the time left on the deadline, and visible in telemetry as a retry. Queue time instead counts against `AttemptTimeout`, where it is indistinguishable from a slow dependency.

> [!CAUTION]
> If you set `QueueLimit` above zero, raise `AttemptTimeout` to cover the wait. A queued call that exceeds the attempt timeout reports as a timeout, and a `SlowCallThreshold` breaker will count it against a dependency that is perfectly healthy.

## What it reports

Three instruments, on the same meter as everything else. See [Telemetry](telemetry.md).

| Instrument | What it is |
| :--- | :--- |
| `nresilience.limiter.leases` | Permits asked for, tagged `nresilience.outcome` = `acquired` or `denied`. |
| `nresilience.limiter.wait.duration` | How long a caller waited. Zero unless queueing is enabled. |
| `nresilience.limiter.limit` | The limit an adaptive limiter has settled on, recorded when it changes. Watching this fall is watching the dependency tell you it is queueing. |

The limiter records these itself rather than deriving them from a `CallEvent`, because it is the only thing that knows how long a caller waited, and because a refusal the policy then retries successfully raises no distinguishable event.

## Learn by example

For a step-by-step guide to bulkhead isolation with `Limit.Concurrency`, see [Resource isolation with bulkheads](../guides/resource-isolation.md).

## Go deeper

[Admission control](../deep-dives/admission-control.md) covers why a refusal is not a fifth verdict kind, why the retry budget is exempt, and why the limiter lives in the callback rather than on the policy.
