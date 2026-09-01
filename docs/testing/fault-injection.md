---
title: Fault injection
description: Inject faults and latency at a chosen rate to find out what your policy does before the dependency decides to show you.
order: 1
---

# Fault injection

A policy is a set of claims about what happens when a dependency misbehaves. Fault injection checks those claims. `Chaos` fails or slows a chosen fraction of calls, so a test - or a game day against a real environment - exercises the retry curve, the breaker, and the budget on the path you actually ship.

Fault injection is opt-in and off by default. `Chaos.Enabled` starts `false`, so a profile bound from a configuration section that does not mention it is inert.

```bash
dotnet add package NResilience.Testing
```

## Inject into a callback

<!-- snippet: chaos-callback -->
```csharp
// One call in ten fails and one in five is slow. Chaos wraps the callback rather than the
// policy, so an injected fault is classified, retried, counted against the breaker and
// written to the attempt log exactly like a real one.
var chaos = new Chaos
{
    Enabled = true,
    FaultRate = 0.1,
    LatencyRate = 0.2,
    Latency = TimeSpan.FromSeconds(value: 2),
};

var result = await policy.TryRunAsync(
    work: chaos.Inject(work: attempt => orders.FetchAsync(cancellationToken: attempt)),
    cancellationToken: cancellationToken);
```
<!-- endsnippet -->

`Chaos` wraps the **callback**, not the policy, and that placement is the whole design. An injected fault travels through the classifier, the retry loop, the circuit breaker, the retry budget, and the attempt log exactly as a real one would, so what a game day exercises is the machinery you deploy rather than a parallel path that only exists under test.

The two rates are rolled independently, so a call can be both slowed and failed - the shape most real degradations take.

| Member | What it does |
| :--- | :--- |
| `Enabled` | The master switch. While `false`, `Inject` returns your callback unwrapped. |
| `FaultRate` | The fraction of calls that fail, from 0 to 1. |
| `Fault` | What a failing call throws. Defaults to an `IOException`. |
| `LatencyRate` | The fraction of calls that are slowed, from 0 to 1. |
| `Latency` | How much slower a slowed call is. |
| `Gate` | Asked before every roll. Return `false` to leave the call alone. |
| `Seed` | Fixes the random stream, so an injected count is repeatable. |
| `Time` | The clock the injected latency is served against. |

`Chaos` is a record, so `with` derives a variant the same way it does for a policy, and `Validate()` refuses a rate outside 0 to 1 or a `LatencyRate` with no `Latency` set.

## Why the default fault is an `IOException`

Because `Classifier.Default` treats an unrecognized exception type as `Permanent`. A chaos profile injecting an exception of its own would produce a run in which nothing is ever retried - the feature silently testing none of the machinery it exists to test. `IOException` is `Transient` under both `Classifier.Default` and `Classifier.Http`, so injected faults are retried out of the box.

Set `Fault` when you want a different one, and check that your classifier has an opinion about it.

## Inject a result instead of an exception

Some failures are not exceptions. A 503, an empty page, a stale record: these are results, and the rules that judge them are the ones you want to exercise. Pass an `outcome` to `Inject` and a failing call returns it rather than throwing.

```csharp
var work = chaos.Inject(
    work: attempt => client.GetAsync(url, attempt),
    outcome: () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
```

The `outcome` function is called once per injected failure, so it must produce a fresh value each time when that value owns something disposable, as an `HttpResponseMessage` does.

## Make a run repeatable

<!-- snippet: chaos-deterministic -->
```csharp
// Seed fixes the random stream, so a test that asserts how many calls were injected is
// repeatable. Gate narrows the blast radius past anything a rate can express - one tenant,
// one region, one shard - and is asked before the dice are rolled.
var chaos = new Chaos
{
    Enabled = true,
    FaultRate = 1.0,
    Seed = 1234,
    Gate = () => tenant == "acme",
};

var failed = await policy.TryRunAsync(
    work: chaos.Inject(work: attempt => orders.FetchAsync(cancellationToken: attempt)),
    cancellationToken: cancellationToken);

Assert.False(condition: failed.IsSuccess);
Assert.IsType<IOException>(@object: failed.Exception);
```
<!-- endsnippet -->

`Seed` fixes the stream. Each `Inject` call and each `ChaosHandler` draws its own stream from that seed, so two of them derived from one profile do not interleave. Within one stream the sequence is fixed; which concurrent caller receives which draw is a property of the scheduler, so a test asserting an exact count should drive the callback sequentially.

`Gate` narrows the blast radius past anything a rate can express - one tenant, one region, one shard, a window of wall-clock time. It is asked before the dice are rolled, so a gated-out call does not consume the stream and a seeded test stays repeatable when the gate changes.

## Inject into an HttpClient

<!-- snippet: chaos-http -->
```csharp
// Add this after AddResilience() to make it inner to the resilience handler. Adding it
// before would inject faults outside the policy, so the policy would not retry them.
services.AddHttpClient(name: "orders")
    .AddResilience()
    .AddHttpMessageHandler(() => new ChaosHandler(
        chaos: new Chaos { Enabled = true, FaultRate = 0.05 },
        response: () => new HttpResponseMessage(statusCode: HttpStatusCode.ServiceUnavailable)));
```
<!-- endsnippet -->

> [!IMPORTANT]
> Add `ChaosHandler` **after** `AddResilience()`. `IHttpClientFactory` runs handlers in registration order, outermost first, so this puts the chaos handler inner to the resilience handler and the policy sees the injected faults. Registered the other way round, the faults sit outside the policy and nothing retries them.

`ChaosHandler` counts what it did - the thing to assert on, rather than inferring it from a retry count:

- `Injected` - how many requests were failed.
- `Slowed` - how many requests were slowed.

Chaos applies on the asynchronous path only. That is not a gap in practice: `ResilienceHandler.Send` throws `NotSupportedException`, so a pipeline with a policy in it has no synchronous path to inject into.

## Test an attempt timeout with injected latency

Injected latency is served on the **attempt's** cancellation token, so a delay longer than `AttemptTimeout` is cut short by it. That is what makes this the way to test a timeout: the delay and the bound meet on the real path, and no dependency has to be persuaded to be slow.

Drive it with `FakeTimeProvider` on both the profile and the policy, and advancing the clock past the ceiling ends the attempt:

```csharp
var time = new FakeTimeProvider();

var chaos = new Chaos { Enabled = true, LatencyRate = 1, Latency = TimeSpan.FromMinutes(5), Time = time };
var policy = (Resilience.Default with { AttemptTimeout = TimeSpan.FromSeconds(1) }).UseClock(time);
```

## Run a game day in production

This is a legitimate thing to want, and it means taking a package named `Testing` as a runtime dependency - a deliberate act that shows up in a project file diff. Two properties make that safe enough to consider:

- `Enabled` is `false` by default, so nothing is injected until something explicitly says otherwise.
- `Gate` is asked on every call, so the exposure can be scoped to a tenant, a region, or a time window rather than to the whole fleet.

A disabled profile hands your callback straight back, unwrapped, so leaving `Inject` at the call site costs one branch at composition time and nothing per call. The switch does not need to be a code change.

## Go deeper

- [Classification](../features/classification.md) - what an injected fault is judged by.
- [Deadlines](../features/deadlines.md) - the bound that injected latency runs into.
- [Testing](index.md) - scripted callbacks, the recording listener, and fake time.
