---
title: Rate limiting
description: Bound the absolute rate or concurrency of outbound calls, before anything has gone wrong.
order: 6
---

# Rate limiting

A **limiter** bounds what leaves this process: the absolute rate of outbound calls, or how many run at once. It is opt-in, and it is the only guard that acts before anything has gone wrong.

It is a different guard from the two the library turns on for you. The [circuit breaker](circuit-breaker.md) reacts to evidence that a dependency is unhealthy. The [retry budget](retry-budget.md) bounds retries as a *fraction* of traffic. Neither bounds an absolute number, so neither stops you exceeding a published quota or opening 500 concurrent connections to one host.

## Turn it on for an HTTP client

<!-- snippet: limit-http -->
```csharp
services.AddHttpClient("api")
        .AddResilience()                              // outer: makes the attempts
        .AddRateLimit(o =>
        {
            o.PermitsPerSecond = 100;                 // one of three shapes; set exactly one
            o.PerHost = true;                         // the default, scoped like the breakers
        });
```
<!-- endsnippet -->

> [!IMPORTANT]
> `AddRateLimit` goes **after** `AddResilience`. Handlers run in registration order, outermost first, so this is what puts the limiter inside the retry loop, where it takes one permit per attempt.

The other order would take one permit for an operation that then makes three calls, so it is refused rather than accepted.

<!-- snippet: limit-order -->
```csharp
// Handlers run in registration order, outermost first, so this puts the limiter *outside*
// the retries - one permit for an operation that goes on to make three calls. Refused at
// registration rather than accepted and silently wrong.
ResilienceConfigurationException error = Assert.Throws<ResilienceConfigurationException>(
    () => services.AddHttpClient("api")
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
using RateLimiter limiter = Limit.PerSecond(100);

var api = Resilience.Http;

int value = await api.RunAsync(async ct =>
{
    // Inside the callback, not around the call. Retry re-invokes the callback, so a permit
    // taken here is taken once per attempt - and `using` is what releases a concurrency
    // permit when the attempt ends, however it ends.
    using RateLimitLease lease = await limiter.AcquireOrThrowAsync(ct);
    return await FetchAsync(ct);
});
```
<!-- endsnippet -->

`ct` is the attempt's token, which is already `min(AttemptTimeout, remaining deadline)` linked with the caller's. A limiter that waits is therefore bounded by the policy's own time budget with nothing further to configure.

## Choose a shape

<!-- snippet: limit-shapes -->
```csharp
// A published per-second quota.
using RateLimiter perSecond = Limit.PerSecond(100);

// A longer quota. The window slides in eight segments, so you cannot spend it all at the
// end of one window and all of the next at the start of the following one.
using RateLimiter perMinute = Limit.PerWindow(1_000, TimeSpan.FromMinutes(1));

// The bulkhead: at most 20 calls in flight at once, whatever their rate.
using RateLimiter inFlight = Limit.Concurrency(20);
```
<!-- endsnippet -->

Set exactly one of them in `RateLimitOptions`. Asking for two is a configuration error; the library does not resolve this for you.

<!-- snippet: limit-validate -->
```csharp
// Three different guards, and a section that asks for two of them is a section whose
// author expected one to win. Every problem is listed at once.
ResilienceConfigurationException error = Assert.Throws<ResilienceConfigurationException>(
    () => new RateLimitOptions { PermitsPerSecond = 100, Concurrency = 20 }.Validate());
```
<!-- endsnippet -->

## Read what a refusal does

A refusal is throttling that knows where it came from.

<!-- snippet: limit-verdict -->
```csharp
CallResult<int> result = await api.TryRunAsync(_ =>
    Task.FromException<int>(new RateLimitedException("payments", TimeSpan.FromSeconds(2))));

Attempt refused = result.Attempts[0];

// Throttling, so it takes the long backoff curve and honors the limiter's own hint.
Assert.Equal(VerdictKind.Throttled, refused.Verdict.Kind);

// And it says where it came from. This is the bit the retry budget reads: a refusal that
// never left the process is not charged, because retrying it costs the dependency nothing.
Assert.True(refused.Verdict.SelfImposed);
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

That is deliberate, because the library is already good at waiting. A refusal becomes a retry on the throttled curve, capped by `Backoff.Max` and by the time left on the deadline, and visible in telemetry as a retry. Queue time is instead charged against `AttemptTimeout`, where it is indistinguishable from a slow dependency.

> [!CAUTION]
> If you set `QueueLimit` above zero, raise `AttemptTimeout` to cover the wait. A queued call that exceeds the attempt timeout reports as a timeout, and a `SlowCallThreshold` breaker will count it against a dependency that is perfectly healthy.

## What it reports

Two instruments, on the same meter as everything else. See [Telemetry](telemetry.md).

| Instrument | What it is |
| :--- | :--- |
| `nresilience.limiter.leases` | Permits asked for, tagged `nresilience.outcome` = `acquired` or `denied`. |
| `nresilience.limiter.wait.duration` | How long a caller waited. Zero unless queueing is enabled. |

The limiter records these itself rather than deriving them from a `CallEvent`, because it is the only thing that knows how long a caller waited, and because a refusal the policy then retries successfully raises no distinguishable event.

## Learn by example

For a step-by-step guide to implementing bulkhead isolation (using `Limit.Concurrency` to prevent resource exhaustion), see [Resource isolation with bulkheads](../guides/resource-isolation.md).

## Go deeper

[Admission control](../deep-dives/admission-control.md) covers why a refusal is not a fifth verdict kind, why the retry budget is exempt, and why the limiter lives in the callback rather than on the policy.
