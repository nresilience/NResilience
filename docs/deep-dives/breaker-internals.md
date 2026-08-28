---
title: Breaker internals
description: A deep dive into the circuit breaker state machine, the reasoning behind default settings, and implementation details.
order: 4
---

# Breaker internals

The circuit breaker in NResilience is designed to prevent a failing dependency from overwhelming both the client and the server. This guide explains the technical reasoning behind its state machine and default configurations.

## The state machine

The breaker transitions through the following states:

- **Closed**: The breaker samples every attempt. If trip conditions are met, the breaker moves to the `Open` state and sets a break deadline.
- **Open**: Calls are refused immediately. When a call arrives after the break deadline has elapsed, the breaker transitions to `HalfOpen` and treats that call as a probe.
- **HalfOpen**: A limited number of concurrent trial calls (`HalfOpenProbes`) are allowed. If the required number of `ProbeSuccesses` is reached, the breaker returns to the `Closed` state. A single failure during this phase re-opens the breaker and increases the break duration.
- **Isolated**: This is a manually triggered state that behaves like `Open` but never self-heals.

To ensure health endpoints can monitor the breaker without interfering with it, reading the `State` property reports `HalfOpen` for an open breaker whose break has elapsed. However, the actual transition to `HalfOpen` only occurs during call admission, meaning health checks do not consume probe slots.

## Design of the defaults

The default settings are chosen to handle the characteristics of typical .NET services.

### Consecutive failures vs. rate-based trips
While rate-based trips (e.g., a 10% failure ratio) are common, they require a minimum throughput to be accurate. For a service doing fewer than 100 calls per 30 seconds, a rate-based breaker may never trip. Therefore, NResilience uses consecutive failures as the default trip condition, with rate-based trips available as an optional addition.

### Why two probe successes?
Closing a breaker after a single successful probe is risky. If a dependency is only intermittently available, a single "lucky" probe could close the breaker just as a fleet of accumulated retries hits the service, leading to oscillation and metastable failure. Requiring multiple successes ensures the dependency has truly recovered.

### Exponential break growth
The break duration doubles with each consecutive trip, up to `MaxBreakDuration`. This applies exponential backoff to the breaker itself, preventing the "flapping" behavior seen with fixed-duration breaks.

### Tracking slow calls
Dependency degradation often manifests as high latency rather than explicit errors. A service returning `200 OK` at 30 times the normal latency can exhaust thread and connection pools. `SlowCallThreshold` allows the breaker to trip based on duration, ensuring that "slow" calls are treated as failures. `SlowCalls` does the same without asking you for the number - see [the adaptive slow-call threshold](#the-adaptive-slow-call-threshold).

## Evidence and sampling

The breaker only counts `Transient` outcomes as evidence of failure.
- **Throttled responses**: These indicate the dependency is working correctly by defending itself. Tripping on these would turn a functioning rate limiter into a full outage.
- **Permanent failures**: These are typically client-side errors (e.g., malformed requests) and do not indicate a dependency failure.

The breaker samples **attempts**, not logical operations. This is critical because a first attempt that trips the breaker must stop the second attempt immediately; checking admission only once per call would defeat the purpose of the breaker.

## The adaptive slow-call threshold

`SlowCallThreshold` is a constant you must guess, in milliseconds, per dependency, before that dependency has ever run in production. `SlowCalls` replaces it with a multiple of measured normal latency. The interesting part is what "normal" has to mean for the trip to work at all.

### Why the obvious version cannot open

The library already estimates a latency quantile, for [hedging](hedging-internals.md). The obvious move is to reuse it: derive the slow-call threshold from a high quantile - the p99, say - of the same distribution the breaker trips over.

That design cannot trip, for the same reason the quantile is safe for hedging. A threshold set at the p99 of recent latency has about 1% of calls above it *by construction*, whatever the dependency is doing, because the threshold is defined as the point 1% of calls exceed. `SlowCallRatio` defaults to 0.5. One percent never reaches half.

Nor does a brownout rescue it. Degradation gets one slice of the estimator's window - a few seconds - before the quantile catches up and the fraction falls back to 1%. That transient is bounded by one slice against the trip window, and is nowhere near `SlowCallRatio`. The breaker does not open late; it never opens.

This is the whole difference between the two features. Hedging *wants* a threshold that chases the dependency, because that is what bounds the extra load at `1 - Quantile`. Breaking wants a threshold that does not, because the point is to notice that the dependency moved.

### Two changes, both required

**Read normal from a low quantile.** `SlowCalls.Quantile` defaults to 0.5 and is capped there. A brownout only starts moving the median once it accounts for more than half the window it is measured over; at the p99 it moves once it accounts for 1%. The lower the quantile, the longer the baseline remembers what healthy looked like - the p25 survives a brownout occupying three quarters of its window, at the cost of calling a threshold "normal" that a quarter of healthy calls already beat.

**Measure it over a much longer window.** `SlowCalls.Window` defaults to 5 minutes against the trip window's 30 seconds. The baseline is the memory of what healthy was; the trip window is what reacts.

Together they turn the trip into a race between two clocks, and the configuration has to win it:

- The trip window fills with slow calls in `SlowCallRatio` times `Window` - 15 seconds at the defaults.
- The baseline survives for `Quantile` times `SlowCalls.Window` - 150 seconds at the defaults.

`BreakerSettings.Validate` requires the second to be at least twice the first. The factor of two is margin: the estimate lags the traffic by up to a quarter of its own window, and a real brownout is neither total nor instant. The defaults win by a factor of ten.

A configuration that loses this race is the failed design above, reached by a different route. Rejecting it at construction is cheaper than discovering it during an incident.

### What feeds the baseline, and what clears it

Only successful attempts. A `Transient` failure may be a connection refused in a microsecond or a socket hanging until the attempt timeout, a `Throttled` response is the dependency defending itself, and a `Permanent` one is usually a client-side fact. None of them is a sample of how long this dependency takes to do the work.

Nothing clears it. `OpenCore` and `CloseCore` clear the sliding window - those counts are evidence for a decision that has now been made - but the baseline is a measurement of the dependency rather than a verdict on it, and `Reset` leaves it alone for the same reason. This is what makes recovery work: a breaker that opened on a brownout still remembers the pre-brownout latency when its first probe lands, so a probe that succeeds slowly is still recognisably a dependency that has not recovered.

The baseline decays on its own, because an idle estimator reports nothing rather than something stale. An outage longer than `SlowCalls.Window` leaves the breaker with no baseline, `Breaker.NormalLatency` reporting `null`, and the latency trip disarmed until `MinimumSamples` successful calls have re-established it. The other trip conditions are unaffected.

### Cost

One `LatencyWindow` per breaker, allocated only when `SlowCalls` is set, living on the breaker rather than the policy: the breaker is the object whose scope is explicit, and two policies sharing a breaker are two views of one dependency that should share one idea of its normal latency. Per attempt, on the success path only, the breaker adds one histogram increment and one memoized read, both behind the lock it already holds, sharing the one clock read that `LatencyWindow.RecordAndThreshold` exists to make possible.

## Concurrency and implementation

### Lock strategy
The breaker uses a standard `lock` rather than a lock-free scheme. Sliding-window rotation is a multi-word operation; implementing this with `Interlocked` alone could lead to silently incorrect failure ratios. Since an uncontended lock takes approximately 20 nanoseconds and the protected callback is orders of magnitude slower, the lock does not introduce a bottleneck.

### `Reset` and in-flight probes

`Reset` is an administrative operation: it closes the breaker, clears the sliding window, and zeroes the break-duration growth. It takes the lock, so it is atomic with respect to other state mutations.

A probe admitted before `Reset` that completes after it lands in a breaker that is now `Closed`. Its outcome is processed as a regular closed-state sample rather than being discarded: the early return in `RecordCore` guards only `Isolated` and `Open`, not `Closed`, and `Reset` moved the state to `Closed`. The probe's outcome is real evidence about the dependency, so counting it is defensible, but it means a failing probe landing immediately after `Reset` can re-trip the breaker when `ConsecutiveFailures` is low. This is a narrow race - it requires an admin `Reset` while a probe is in flight - and the behavior follows directly from the state machine.

### Memory efficiency
To minimize overhead, the window is divided into ten buckets (providing 3-second granularity for a 30-second window). The arrays required for rate-based tracking are only allocated if a rate-based trip is configured. A breaker relying solely on consecutive failures uses only three fields and no arrays.

### Event dispatch
Breaker transitions are passed back to the executor rather than being raised inside the lock. This prevents arbitrary user code in a listener from holding the lock and serializing all calls through the breaker.

## Clock ownership

The breaker maintains its own `TimeProvider`. This is necessary because `State` and `OpenedAt` are often read by health endpoints or admin handlers that do not have access to a specific policy. Additionally, a shared breaker used by multiple policies with different clocks would have no consistent way to determine how long it has been open.
