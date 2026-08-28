---
title: Hedging
description: Start a second copy of a slow attempt against a live latency quantile, so the caller sees the shorter of two draws.
order: 7
---

# Hedging

**Hedging** starts a second copy of an attempt that is taking longer than almost every other call to the same dependency, and returns whichever answer arrives first. A call that has already outrun the p95 is unlikely to finish quickly, and a fresh attempt often beats it - so the caller sees the p99 of two draws rather than the p99 of one.

Hedging is **opt-in**. It is off in `Resilience.Default`, off in `Resilience.Http`, and off in `AddResilience()`. Set `Hedge` to turn it on.

## Turn it on

<!-- snippet: hedging-configure -->
```csharp
// The threshold is always a live quantile of recent latency, never a constant. A brownout moves
// the quantile with it, so the fraction of calls that hedge stays at about 1 - Quantile whatever
// the dependency is doing - which is why there is deliberately no Hedge.After(TimeSpan).
var api = Resilience.Http with
{
    Attempts = 3, // at most 3 calls reach the dependency, whatever shape they run in
    Hedge = Hedge.At(quantile: 0.95), // the 2nd may start before the 1st comes back
};
```
<!-- endsnippet -->

`Hedge.At` is the only way to configure it. There is deliberately no fixed-delay form: the threshold is always a **live quantile of recent latency**, which is what makes the feature safe to leave on. See [Hedging internals](../deep-dives/hedging-internals.md) for the argument.

`Attempts` stays what it says: the total number of calls that reach the dependency, whether they run one after another or at the same time. `Attempts = 3` with `MaxConcurrent = 2` means at most three wire calls, at most two of them in flight at once. There is one number to reason about, not two multiplied together.

## Tune it

<!-- snippet: hedging-tuning -->
```csharp
// Hedge.At fills in the rest, so change only what you mean to. The quantile is the load: 0.99
// hedges 1% of calls and shortens a smaller part of the tail than 0.95 does.
var api = Resilience.Http with
{
    Hedge = Hedge.At(quantile: 0.99, maxConcurrent: 3) with
    {
        MinimumSamples = 50, // wait for 50 recent calls before hedging anything
        MinimumDelay = TimeSpan.FromMilliseconds(value: 25), // never hedge sooner than this
        Window = TimeSpan.FromMinutes(value: 1), // how much history the estimate covers
    },
};
```
<!-- endsnippet -->

| Property | Default | What it means |
| :--- | :--- | :--- |
| `Quantile` | - | The quantile a hedge fires at, from 0.5 to 1 exclusive. This is also the extra load: 0.95 costs about 5%. |
| `MaxConcurrent` | `2` | How many attempts may be in flight at once, counting the first. |
| `MinimumSamples` | `20` | How many recent calls the estimate needs before any hedge fires. |
| `MinimumDelay` | `10 ms` | A floor under the delay, so a dependency with a sub-millisecond p95 does not hedge everything. |
| `Window` | `30 s` | How much history the estimate covers. |

Pick `Quantile` by the load you are willing to add. Everything else has a working default, and `Hedge.At(0.95)` is a complete configuration.

## Hold the policy

The latency estimate is private to the policy **instance**, exactly as the [automatic retry budget](retry-budget.md) is. A policy rebuilt on every call never accumulates samples, never reaches `MinimumSamples`, and never hedges anything.

<!-- snippet: hedging-static-policy -->
```csharp
public static class Policies
{
    // One instance, for the lifetime of the process. The latency estimate is private to this
    // instance, exactly as the automatic retry budget is, so a `with` expression inside a method
    // would hand every call a policy that has never seen a single latency sample.
    public static readonly Resilience Search = (Resilience.Http with
    {
        Attempts = 3,
        Hedge = Hedge.At(quantile: 0.95),
    }).Validated();
}
```
<!-- endsnippet -->

<!-- snippet: hedging-static -->
```csharp
var value = await Policies.Search.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
```
<!-- endsnippet -->

## Hedge HTTP requests

The [HTTP handler](../http/idempotency.md) needs no hedging configuration of its own.

<!-- snippet: hedging-http -->
```csharp
// Nothing HTTP-specific is needed. The handler already scopes a policy per host - so each host
// gets its own latency estimate - and already refuses to repeat a POST, which is the same gate a
// hedge has to pass.
services.AddHttpClient<SearchClient>()
    .AddResilience(Resilience.Http with { Hedge = Hedge.At(quantile: 0.95) });
```
<!-- endsnippet -->

Two things the handler already does are exactly what hedging needs:

- **Per-host scoping.** The handler derives one policy per host, so each host gets its own latency estimate. Hedging one host against another host's tail would hedge everything.
- **The idempotency gate.** A request the handler will not retry is a request it will not hedge, because a hedge is a concurrent retry. `POST` and `PATCH` are not repeatable unless you say so.

<!-- snippet: hedging-repeatable -->
```csharp
// A hedge is a concurrent retry, so the idempotency key that makes a retried POST safe is what
// makes a hedged one safe. Without this the request is sent exactly once, whatever Hedge says.
using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: uri);
request.MarkRepeatable();
```
<!-- endsnippet -->

Each leg builds its own request from a buffered body, and the responses that lose the race are disposed - so a hedged call leaks no sockets.

One consequence of [per-host scoping](../http/per-host-scope.md) is worth knowing: the latency estimate belongs to the handler, and `IHttpClientFactory` rotates handler chains every two minutes by default. A rotated client starts with a cold estimate and hedges nothing until it has seen `MinimumSamples` calls again - the same way its per-host breakers start closed.

## Read what it produces

Three [events](telemetry.md) describe a race, and the attempt log records both legs.

| Event | What it says |
| :--- | :--- |
| `HedgeStarted` | A copy was started. `Delay` carries the threshold that triggered it, which is the live quantile itself. |
| `HedgeWon` | The copy produced the answer, so this call saw the shorter of two draws. |
| `HedgeDiscarded` | An attempt was cancelled because a sibling answered first. `Duration` is how long it had been running. |

<!-- snippet: hedging-events -->
```csharp
var api = Resilience.Http with
{
    Hedge = Hedge.At(quantile: 0.95),
    OnEvent = e =>
    {
        if (e.Kind == CallEventKind.HedgeStarted)
            started++; // e.Delay is the quantile the hedge fired at

        if (e.Kind == CallEventKind.HedgeWon)
            won++; // the copy answered, so this call saw the shorter of two draws
    },
};
```
<!-- endsnippet -->

In `NResilience.Extensions`, the same facts arrive as `nresilience.hedges` tagged `started`, `won` and `discarded`, plus `nresilience.hedge.threshold` - the adaptive threshold, recorded each time a hedge fires. Watching that number move during an incident is how you tell a brownout from a tail.

The [attempt log](../reference/call-result.md) shows both legs, and a discarded one reads as what it is:

```text
2 attempts over 41ms: hedge Ok (1ms), at 40ms, discarded (41ms)
```

`Attempt.IsHedged` says the attempt started alongside one already in flight, `Attempt.IsDiscarded` says it was cancelled because a sibling answered, and `Attempt.StartOffset` is what makes the overlap visible. A discarded attempt was never classified, so its `Verdict` carries no information - read the flag instead.

## When a hedge does not fire

All five conditions must hold. If any fails, the call waits, exactly as it would without hedging configured:

1. `Hedge` is set on the policy.
2. The call is repeatable. For HTTP, the same gate retry uses.
3. The circuit breaker is closed. A dependency that is failing does not need a second copy of every slow request.
4. The estimate has at least `MinimumSamples` samples. A cold process does not guess a threshold.
5. The [retry budget](retry-budget.md) funds it. Hedges and retries draw on one bucket, so a policy already retrying at its limit stops hedging - a retry is evidence that something failed, and a hedge is only a guess that something is slow.

## Go deeper

- [Hedging internals](../deep-dives/hedging-internals.md) - why an adaptive threshold is safe and a constant one is not, and how the quantile is estimated.
- [Retry budget](retry-budget.md) - the bucket hedges and retries share.
- [Idempotency](../http/idempotency.md) - what makes a request repeatable.
