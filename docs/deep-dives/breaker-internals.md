---
title: Breaker internals
description: A deep dive into the circuit breaker state machine, the reasoning behind default settings, and implementation details.
order: 4
---

# Breaker internals

The circuit breaker prevents a failing dependency from overwhelming both the client and the server. This page explains the reasoning behind its state machine and default settings.

## The state machine

The breaker transitions through the following states:

- **Closed**: The breaker samples every attempt. If trip conditions are met, the breaker moves to the `Open` state and sets a break deadline.
- **Open**: Calls are refused immediately. When a call arrives after the break deadline has elapsed, the breaker transitions to `HalfOpen` and treats that call as a probe.
- **HalfOpen**: A limited number of concurrent trial calls (`HalfOpenProbes`) are allowed. If the required number of `ProbeSuccesses` is reached, the breaker returns to the `Closed` state. A single failure during this phase re-opens the breaker and increases the break duration.
- **Isolated**: This is a manually triggered state that behaves like `Open` but never self-heals.

Health endpoints can monitor the breaker without interfering: reading `State` reports `HalfOpen` for an open breaker whose break has elapsed. The actual transition happens only during call admission, so health checks never consume probe slots.

## Design of the defaults

The defaults are tuned for the characteristics of typical .NET services.

### Consecutive failures vs. rate-based trips
Rate-based trips (a 10% failure ratio, say) need minimum throughput to be accurate. A service doing fewer than 100 calls per 30 seconds may never trip a rate-based breaker. So the default trip condition is consecutive failures, with rate-based trips as an optional addition.

### Why two probe successes?
Closing after a single successful probe is risky. If a dependency is only intermittently available, one lucky probe could close the breaker just as a fleet of accumulated retries hits the service - oscillation, and a metastable failure. Requiring multiple successes proves the dependency has actually recovered.

### Exponential break growth
The break duration doubles with each consecutive trip, up to `MaxBreakDuration`. That is exponential backoff applied to the breaker itself, preventing the flapping of fixed-duration breaks.

### Jitter on the break duration
`Backoff` defaults to full jitter because a narrow band around a shared base still leaves a synchronized pulse. The breaker's own backoff has the same problem and a worse blast radius, so `BreakJitter` defaults to `Jitter.Equal` - see [the synchronized probe](#the-synchronized-probe).

### Tracking slow calls
Dependency degradation often shows up as latency rather than errors: a service answering `200 OK` at 30 times normal latency can exhaust thread and connection pools. `SlowCallThreshold` lets the breaker trip on duration, so slow calls count as failures. `SlowCalls` does the same without asking you for a number - see [the adaptive slow-call threshold](#the-adaptive-slow-call-threshold).

## Evidence and sampling

The breaker only counts `Transient` outcomes as evidence of failure.
- **Throttled responses**: The dependency is working, and defending itself. Tripping on these would turn a functioning rate limiter into a full outage.
- **Permanent failures**: Usually client-side errors (a malformed request) and not a dependency failure.

The breaker samples **attempts**, not logical calls. That is critical: a first attempt that trips the breaker must stop the second attempt immediately, and checking admission once per call would defeat the breaker.

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

One `LatencyWindow` per breaker - about 3.4 KB - allocated whenever `SlowCalls` is set, which at the defaults is every breaker, living on the breaker rather than the policy: the breaker is the object whose scope is explicit, and two policies sharing a breaker are two views of one dependency that should share one idea of its normal latency. Per attempt, on the success path only, the breaker adds one histogram increment and one memoized read, both behind the lock it already holds, sharing the one clock read that `LatencyWindow.RecordAndThreshold` exists to make possible.

## The relative failure ratio

`FailureRatio` is absolute, and an absolute error rate ports nowhere - which is the argument `SlowCalls` makes about latency, left unmade about errors on the very next line of the same settings object.

Two dependencies, one configuration, opposite outcomes. A payments API whose steady-state transient rate is 0.02% is deeply broken at 5%: every downstream is retrying, somebody has been paged, and `FailureRatio = 0.5` has not noticed - the breaker trips when the dependency is ten times worse than the point a human would have escalated. A third-party search backend whose steady-state rate is 30%, because it is flaky and everyone has always retried it, opens its circuit on ordinary variance at the same setting, and the operator's fix is to raise the number until it stops, at which point it detects nothing.

`Failures.Above(5)` is the same trip stated as a multiple of the dependency's own rate. It configures both.

### The two guards

**The floor is not optional.** A baseline of 0.02% times a multiple of 5 is 0.1%, and on a 30-second window at 20 minimum calls that is one failure. Without `AbsoluteFloor`, this feature is a breaker that opens on a single error against any healthy dependency. The floor is the line that says "below 5% absolute, nothing is wrong no matter how quiet the baseline was".

The trip window has the same problem from the other end. At a 5% floor and `MinimumCalls`' default of 20, one transient error *is* the threshold - so a relative trip also requires at least two failures in the window, because a rate estimated from a single event is not a claim about a rate. An absolute `FailureRatio` is deliberately not held to that: a caller who wrote `0.05` over 20 calls asked for exactly that reading, and this feature does not get to second-guess it.

**The baseline needs more samples than a latency quantile does.** `Failures.MinimumSamples` defaults to 100 against `SlowCalls`' 20, because errors are rare by construction and a rate estimated from 20 calls has a resolution of 5% - the floor itself.

### The same race, in errors

An outage contaminates the baseline as it fills it, exactly as a brownout contaminates the latency baseline, and the arithmetic is the mirror image:

- The trip window turns over to failures in `Window` - 30 seconds at the defaults.
- After `t` seconds the baseline reads roughly `t / Failures.Window`, so the trip point reads `Multiple` times that. A trip window can be at most 100% failures, so once the baseline reaches `1 / Multiple` the breaker cannot open on the error rate at all - which takes `Failures.Window / Multiple`, or 60 seconds at the defaults.

`BreakerSettings.Validate` requires the second to be at least twice the first, the same factor of two `SlowCalls` is held to, and the defaults meet it exactly. A consequence worth stating: raising `Multiple` shortens the survival time, so `Failures.Above(10)` on a 30-second trip window wants a 10-minute baseline and is refused with a 5-minute one. That is the honest trade, and the message names all three knobs that resolve it. It is also why the *default* `Failures` derives its baseline from the trip window rather than taking 5 minutes as given: a default the caller never wrote must not be able to turn their `Window` into a configuration error, so it widens instead, and steps aside entirely once the baseline it would need passes an hour.

### Composition, and why this one is not exclusive

`SlowCalls` and `SlowCallThreshold` are the same trip defined two ways, and `Validate` refuses both. `Failures` and `FailureRatio` are a measurement and a ceiling, and setting both is the recommended configuration: the effective trip point is `min(FailureRatio, max(AbsoluteFloor, baseline * Multiple))`. The relative trip can only fire sooner than the absolute one, which is the house rule for every adaptive feature in the library - an estimator may tighten a guard and never loosen one.

### Cost, and what clears it

Two `int[10]` rings per breaker - 80 bytes - allocated whenever `Failures` is set, which at the defaults is every breaker, and nothing on the executor's path at all. The baseline is bucketed over its own window rather than the trip window's, rotated on write like the trip window, and guarded by the same lock.

Nothing clears it. `OpenCore`, `CloseCore` and `Reset` clear the trip window, because those counts are evidence for a decision that has now been made; the baseline is a measurement of the dependency, and forgetting it at the moment the breaker opens would leave the next thirty seconds unjudgeable until the rate had been re-learned. It decays on its own instead: an outage longer than `Failures.Window` leaves the breaker with no baseline, `Breaker.NormalFailureRate` reporting `null`, and the relative trip disarmed until 100 outcomes have re-established it. The consecutive counter and `FailureRatio` are unaffected, which is what covers the cold start.

## The synchronized probe

200 pods, one dependency, one outage. Every pod's breaker opens within a second or two of the others, because they are all watching the same failure, and every one of them sets a break of exactly `BreakDuration`. Fifteen seconds later all 200 transition to half-open in the same second and each sends its one probe. The dependency, which has been getting no traffic and may be halfway through recovering, receives a 200-request synchronized pulse. If it fails them - and a dependency mid-recovery often will - all 200 breakers re-open together, with a doubled break, and do it again at 30 seconds.

`HalfOpenProbes = 1` makes each pod polite and does nothing whatever about the fleet. It is the same mistake the [retry budget](../features/retry-budget.md) identifies about per-call attempt limits: a per-call limit cannot prevent a storm, because every caller independently believes it is being reasonable.

`BreakJitter` is the fix, and it defaults to `Jitter.Equal` rather than the `Jitter.Full` that `Backoff` uses. This is the one place the library prefers equal jitter, because the break duration has a purpose beyond de-correlation - it is how long the dependency gets left alone - and full jitter would let a pod probe after 200 milliseconds of a 15-second break. Equal jitter keeps a floor under the delay, which is exactly the property wanted.

Three details make it honest:

- The jitter is applied once, when the breaker opens, to the already-grown and already-capped duration. Growth is therefore computed from the nominal break, so a short first break does not shorten every break after it, and `MaxBreakDuration` still bounds the result.
- `RetryAfterHint` returns `_breakUntil` minus the elapsed time, so it reports the break actually being served rather than the nominal one, and `CallRejectedException.RetryAfter` is honest by construction.
- `Jitter.None` is the escape hatch. A test that asserts "after exactly `BreakDuration`, the state is half-open" needs it, and that is the whole migration.

## Concurrency and implementation

### Locking mechanism
The breaker uses a standard `lock` rather than a lock-free scheme. Sliding-window rotation is a multi-word operation; `Interlocked` alone could produce silently incorrect failure ratios. An uncontended lock costs about 20 nanoseconds and the protected callback is orders of magnitude slower, so the lock is no bottleneck.

### `Reset` and in-flight probes

`Reset` is an administrative operation: it closes the breaker, clears the sliding window, and zeroes the break-duration growth. It takes the lock, so it is atomic with respect to other state mutations.

A probe admitted before `Reset` that completes after it lands in a breaker that is now `Closed`. Its outcome is processed as a regular closed-state sample rather than being discarded: the early return in `RecordCore` guards only `Isolated` and `Open`, not `Closed`, and `Reset` moved the state to `Closed`. The probe's outcome is real evidence about the dependency, so counting it is defensible, but it means a failing probe landing immediately after `Reset` can re-trip the breaker when `ConsecutiveFailures` is low. This is a narrow race - it requires an admin `Reset` while a probe is in flight - and the behavior follows directly from the state machine.

### Memory efficiency
The window divides into ten buckets (3-second granularity for a 30-second window). Arrays for rate-based tracking are allocated only if a rate-based trip is configured; a breaker relying only on consecutive failures uses three fields and no arrays.

### Event dispatch
Breaker transitions pass back to the executor rather than being raised inside the lock, so arbitrary user code in a listener cannot hold the lock and serialize every call through the breaker.

## Clock ownership

The breaker keeps its own `TimeProvider`. `State` and `OpenedAt` are often read by health endpoints or admin handlers that have no policy instance, and a shared breaker used by policies with different clocks would have no consistent way to measure how long it has been open.
