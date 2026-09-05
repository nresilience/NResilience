---
title: Circuit breaker
description: Prevent cascading failures by stopping calls to a failing dependency.
order: 4
---

# Circuit breaker

When a dependency fails, continuing to call it on every request piles onto the outage. A **circuit breaker** prevents that: it stops calls to the dependency, gives it time to recover, and periodically lets a few trial calls through to check.

Breakers are opt-in, and they are objects rather than settings. That lets you decide the scope of the "stop calling" decision - one breaker per dependency, per host, or whatever matches your architecture.

## Enable the circuit breaker

Create a `Breaker` object and assign it to your policy.

<!-- snippet: breaker-construct -->
```csharp
// Breaker scope is a variable with a name and a lifetime. `with` copies the reference,
// so every policy derived from `payments` shares this breaker.
var breaker = Breaker.Of(name: "payments");

var payments = Resilience.Http with { Breaker = breaker };
var paymentsWrites = payments with { Attempts = 1 };
```
<!-- endsnippet -->

The breaker's scope is where you hold the reference. Since `with` copies the reference, not the state, two policies derived from a common ancestor share the same breaker.

For HTTP calls, the handler can scope a breaker per host automatically - see [per-host scope](../http/per-host-scope.md).

## Breaker states

A circuit breaker is always in one of these states:

| State | Description |
| :--- | :--- |
| `Closed` | Normal operation. Calls are allowed, and failures are tracked. |
| `Open` | The dependency is failing. Calls are refused for the duration of the break. |
| `HalfOpen` | The break duration has elapsed. A small number of trial calls (probes) are allowed to test for recovery. |
| `Recovering` | The probes succeeded and a growing fraction of calls is being admitted. Only reachable when `Recovery` is set. |
| `Isolated` | Forced open by an operator via `Isolate()`. The breaker stays open until `Reset()` is called. |

## Trip conditions

A breaker trips on consecutive failures, or on rates of failure and slowness. Slowness matters: a dependency can return successful responses so slowly that it exhausts your thread and connection pools.

`new Breaker()` trips on all three: five consecutive failures, an error rate five times the dependency's own, and half a window three times slower than its own normal. The last two are measured, not configured, and neither is armed until the breaker has a baseline - so a cold breaker behaves exactly as a consecutive-failures one does.

| Setting | Default | Description |
| :--- | :--- | :--- |
| `Adaptive` | `true` | Whether the breaker measures the dependency. `false` turns off both relative trips with a single setting. |
| `ConsecutiveFailures` | 5 | The number of consecutive failures before the breaker opens. |
| `FailureRatio` | null | An optional rate-based trip condition, evaluated alongside the consecutive failure counter. |
| `Failures` | `Failures.Above(5)` | The same trip, expressed as a multiple of the dependency's own measured error rate. On by default. Composes with `FailureRatio`, which stays the ceiling. |
| `MinimumCalls` | 20 | The minimum number of calls required before a ratio-based trip is evaluated. |
| `TripWindow` | 30 s | The sliding window the trip ratios are measured over - not to be confused with the baseline `Window` inside `SlowCalls` and `Failures`. |
| `SlowCallThreshold` | null | A constant latency threshold; any attempt slower than this counts as a slow call. |
| `SlowCalls` | `SlowCalls.Above(3)` | The same trip, expressed as a multiple of measured normal latency. On by default, and composes with `SlowCallThreshold`: a call is slow when it is above either. |
| `SlowCallRatio` | 0.5 | The proportion of slow calls within the window that trips the breaker. |
| `BreakDuration` | 15 s | The duration of the first break. |
| `MaximumBreakDuration` | 2 min | The maximum break duration. The break duration doubles on each consecutive open. |
| `BreakJitter` | `Jitter.Equal` | How much randomness the break duration carries, so a fleet that opened together does not probe together. |
| `HalfOpenProbes` | 1 | The number of concurrent trial calls allowed while in the `HalfOpen` state. |
| `ProbeSuccesses` | 2 | The number of successful probes required to close the breaker. |
| `Recovery` | null | Hand the traffic back over a ramp instead of a cliff. Off by default. |

<!-- snippet: breaker-slow-calls -->
```csharp
// The most common real degradation is not errors, it is a dependency answering 200s at
// 30x normal latency. A breaker that only counts errors stays closed through the whole
// incident, because the responses are not failing - they are just slow.
var breaker = new Breaker(settings: new BreakerSettings
{
    Name = "search",
    ConsecutiveFailures = 5, // the default trip condition
    SlowCallThreshold = TimeSpan.FromSeconds(value: 2), // anything slower counts against
    SlowCallRatio = 0.5, // half the window being slow trips it
    MinimumCalls = 20, // below this, a ratio means nothing
    TripWindow = TimeSpan.FromSeconds(value: 30), // the history the ratios are measured over
    BreakDuration = TimeSpan.FromSeconds(value: 15), // doubles per consecutive open
    MaximumBreakDuration = TimeSpan.FromMinutes(value: 2),
    ProbeSuccesses = 2, // two good probes to close, not one
});
```
<!-- endsnippet -->

The breaker samples individual **attempts**, and only `Transient` outcomes count as evidence of failure. `Throttled` responses mean the dependency is up and defending itself; `Permanent` outcomes are usually client-side issues.

## Trip on brownouts without guessing a number

**On by default.** `SlowCallThreshold` asks for a millisecond figure per dependency - before that dependency has ever run in production, and again every time its latency changes. `SlowCalls` asks for a multiple instead and measures the rest itself, so it is what an unconfigured breaker trips on. `SlowCalls = null` turns it off.

Set both when the dependency has a real, externally fixed budget you never want exceeded. They [compose](../getting-started/key-concepts.md#constants-and-measurements-compose) the way every constant and its measurement do: a call is slow when it is above either, so the constant is a ceiling the measured term can tighten but never loosen.

<!-- snippet: breaker-adaptive-slow-calls -->
```csharp
// "3x slower than usual" ports to any dependency. "800 ms" does not: it is a number you
// have to guess per dependency, before that dependency has ever run in production, and
// re-guess every time its latency changes. The breaker measures normal itself, from the
// successful attempts it already samples.
var breaker = new Breaker(settings: new BreakerSettings
{
    Name = "search",
    SlowCalls = SlowCalls.Above(multiple: 3), // slow = 3x the recent median
    SlowCallRatio = 0.5, // half the window being slow trips it
    MinimumCalls = 20,
});

// What the dependency normally costs, as this breaker measures it. Worth graphing; null
// until 20 successful calls have landed, and the trip is not armed until then either.
var normal = breaker.NormalLatency;
```
<!-- endsnippet -->

The breaker keeps a baseline of how long a successful call takes - by default the median over the last five minutes - and counts an attempt as slow when it exceeds `Multiple` times that. Read the baseline from `Breaker.NormalLatency`.

Two settings make this work, both with defaults you can leave alone:

- `Quantile` (default 0.5, capped there) is the quantile that counts as normal. A brownout only starts moving the median once it accounts for more than half the baseline window.
- `Window` (default 5 minutes) is how far back the baseline reaches - 10 times `TripWindow`, so the trip window fills with slow calls long before the baseline notices them.

`BreakerSettings.Validate` rejects combinations where the baseline would move first - such a breaker never opens on latency at all. That rejection is for a baseline you configured; the *default* baseline widens with a longer `TripWindow` instead, because a value you did not write must not turn your configuration into an error. See [Breaker internals](../deep-dives/breaker-internals.md#the-adaptive-slow-call-threshold) for the arithmetic.

Only successful attempts feed the baseline, and the baseline survives an open, a close, and a `Reset` - it measures the dependency, it does not decide anything about it. That is what makes a slow probe against a still-degraded dependency recognizable as one.

The retry loop uses this baseline: it does not start a retry if the time remaining on the deadline is less than the time a healthy call to the dependency takes. See [deadlines](deadlines.md#the-two-bounds).

## Trip on errors without guessing a rate

**On by default.** `FailureRatio` asks for an absolute error rate, and no single number fits two dependencies. `Failures` asks for a multiple of the dependency's own rate instead and measures the rest itself. `Failures = null` turns it off.

<!-- snippet: breaker-relative-failures -->
```csharp
// "5x its own error rate" ports to any dependency. An absolute ratio does not: 5% is
// catastrophic for a payments API whose steady state is 0.02%, and a quiet day for a
// third-party search backend that has always run at 30%. The breaker measures the rate
// itself, from the outcomes it already samples.
var breaker = new Breaker(settings: new BreakerSettings
{
    Name = "search",
    Failures = Failures.Above(multiple: 5), // too many = 5x the recent error rate
    FailureRatio = 0.5, // and never more than half the window, whatever the baseline
    MinimumCalls = 20,
});

// How often the dependency normally fails, as this breaker measures it. Worth graphing;
// null until 100 outcomes have landed, and the relative trip is not armed until then.
var rate = breaker.NormalFailureRate;
```
<!-- endsnippet -->

The breaker keeps a baseline error rate - by default over the last five minutes - and trips when the window's rate exceeds `Multiple` times it. Read the baseline from `Breaker.NormalFailureRate`.

Three guards make it safe, all defaulted:

- `Floor` (default 0.05) is the rate below which nothing is wrong, whatever the baseline was. Five times a baseline of nearly zero is nearly zero, so without a floor the first error of the day would open the circuit. A relative trip also needs at least two failures in the window, because one failure is not a rate.
- `MinimumSamples` (default 100) is how many outcomes the baseline needs before the relative trip is armed. Until then the breaker behaves exactly as it does without the setting.
- `Window` (default 5 minutes) is how far back the baseline reaches. `BreakerSettings.Validate` rejects a baseline short enough that an outage raises it before the trip window fills - such a breaker never opens on the error rate at all. The default baseline widens instead, the way the slow-call baseline does.

Set `FailureRatio` as well when you have a rate you never want exceeded. The relative trip can only fire sooner than it, never later.

## The break is jittered

Every pod's breaker opens within a second of the others, because they are all watching the same dependency fail. Give them all the same break duration and they all probe in the same second, and a dependency halfway through recovering takes the fleet's probes as one pulse - which it often fails, re-opening every breaker together with a doubled break.

`BreakJitter` breaks that correlation, and it is on by default at `Jitter.Equal`: the break runs for half the computed duration plus up to half again.

<!-- snippet: breaker-jitter -->
```csharp
// Every pod's breaker opens within a second of the others, because they are all watching
// the same dependency fail. Without jitter they all probe in the same second, and a
// dependency halfway through recovering takes the whole fleet's probes at once.
var breaker = new Breaker(settings: new BreakerSettings
{
    Name = "search",
    BreakDuration = TimeSpan.FromSeconds(value: 15), // now half of that, plus up to half again
    BreakJitter = Jitter.Equal, // the default
});
```
<!-- endsnippet -->

`Jitter.Equal` rather than `Jitter.Full` keeps a floor under the break, because the duration has a purpose beyond de-correlation - it is how long the dependency gets left alone. `RetryAfterHint` and `CallRejectedException.RetryAfter` report the break actually being served, so a caller scheduling its own retry is never told the nominal figure. Use `Jitter.None` when a test needs the break to expire at exactly `BreakDuration`.

## Hand the traffic back over a ramp

Two successful probes indicate the dependency can serve some traffic. Closing the breaker immediately restores full load, which may overwhelm a dependency that failed due to capacity limits. This can cause the breaker to trip again with a longer break duration.

`Recovery` introduces a `Recovering` state between `HalfOpen` and `Closed`. In this state, a growing fraction of calls is admitted while the rest are refused. This feature is off by default, as any call refused during the ramp would have been served by a breaker that closed immediately.

<!-- snippet: breaker-recovery -->
```csharp
// Two successful probes prove the dependency can serve two calls. A cliffed close reads
// that as proof it can serve two thousand, and a dependency that failed because it ran out
// of capacity cannot: it fails, the breaker re-opens with a doubled break, and it spends
// more of each period cold. The ramp gives it a trickle it can actually serve.
var breaker = new Breaker(settings: new BreakerSettings
{
    Name = "search",
    Recovery = Recovery.Over(length: 0.25), // ramp back over a quarter of the break served
    BreakDuration = TimeSpan.FromSeconds(value: 15), // so this one ramps over about 4 s
});
```
<!-- endsnippet -->

The ramp's length is derived from the break duration just served, clamped between `MinimumLength` and `MaximumLength`. Its pace depends on the performance of admitted calls: a slow call halves the admitted fraction, and `ProbeSuccesses` consecutive fast calls increase it, with the clock providing an upper bound. A single failure during the ramp re-opens the breaker with an increased break duration.

**The failure mode.** A ramp against a dependency that answers but remains slow will not complete: it stays at `InitialFraction`, and `State` reports `Recovering` indefinitely. This is reported as degraded to the [health check](../di/health-checks.md). This is deliberate; it indicates the dependency is up but not ready, and re-opening would deny the trickle of traffic it needs to warm. Alert on a breaker recovering for longer than its break. See [Breaker internals](../deep-dives/breaker-internals.md#the-recovery-cliff).

## Handle refused calls

When a breaker refuses a call, it pauses briefly before returning. That stops callers in tight polling loops from busy-spinning on CPU.

<!-- snippet: breaker-rejection -->
```csharp
// A refused call reports itself rather than the dependency's last exception, and it says
// which guard refused it. RetryAfter is there so a caller that schedules its own polling
// does not have to guess.
if (result.Exception is CallRejectedException rejection)
{
    Console.WriteLine(value: rejection.Reason); // DependencyUnavailable, or BudgetExhausted
    Console.WriteLine(value: rejection.RetryAfter); // when to come back, when there is an answer
}
```
<!-- endsnippet -->

The breaker uses `StopReason.DependencyUnavailable` for refusals. The [retry budget](retry-budget.md) uses `BudgetExhausted`.

For more information, see [Guarded rejection](../deep-dives/guarded-rejection.md).

## The breaker's clock
 
A breaker measures its break duration and sliding window with `BreakerSettings.Time`, not the executing policy's clock. That way `State` and `OpenedAt` can be read from health endpoints that have no policy instance. If one breaker is shared by two policies with different clocks, it keeps its own independent time source.
 
When the library creates the breaker for you - [per-host breakers](../http/per-host-scope.md) or breakers from a [configuration section](../di/configuration.md) - it uses the policy's `Time` unless the settings specify a different clock, so one `FakeTimeProvider` on the policy drives them in tests. See [Testing](../testing/index.md).

A breaker you construct yourself uses the clock in its settings. To align it with a policy, give both the same `TimeProvider` instance.

## Manage the breaker

You can read the breaker's state or control it by hand.

<!-- snippet: breaker-admin -->
```csharp
var state = breaker.State; // Closed, Open, HalfOpen, Recovering or Isolated
var since = breaker.OpenedAt; // null while it is closed

breaker.Isolate(); // force it open and keep it there
breaker.Reset(); // close it and forget the history
```
<!-- endsnippet -->

`State` reports `HalfOpen` for an open breaker whose break duration has elapsed, and `Closed` for a recovering one whose ramp has run out. Reading the state does not consume a probe slot. `Isolate` and `Reset` raise no events because no call triggered them.

Transitions raise `BreakerOpened`, `BreakerClosed`, and `BreakerHalfOpened` [events](telemetry.md) on the call that caused the transition. A ramp raises `BreakerClosed` when it starts, because that is where the breaker stops refusing everything; its completion is silent.

For more, see [Breaker internals](../deep-dives/breaker-internals.md).
