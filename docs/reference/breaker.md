---
title: Breaker
description: Reference for the Breaker class, its settings, and the possible states of a circuit breaker.
order: 5
---

# `Breaker`

The `Breaker` is a `sealed class` implementing the circuit breaker pattern. It is a live object: create it and share it across the calls you want to protect.

| Member | Description |
| :--- | :--- |
| `Breaker(BreakerSettings? settings = null)` | Creates a new breaker. This constructor validates the settings and throws a `ResilienceConfigurationException` if they are invalid. |
| `Name` | An `init`-only property used for diagnostics and health endpoints. |
| `Settings` | The `BreakerSettings` used to configure the breaker. |
| `State` | The current state of the breaker. If a breaker is open but the break duration has elapsed, it reports `HalfOpen` because the next call will be treated as a probe; if it is recovering and its ramp has run out, it reports `Closed` for the same reason. Reading this property does not consume a probe slot. |
| `OpenedAt` | The timestamp of when the breaker last opened, or `null` if it is currently closed. A recovering breaker still reports this timestamp, as the ramp is a continuation of the previous open state. |
| `NormalLatency` | How long a healthy call to this dependency currently takes, as the breaker measures it. `null` unless `SlowCalls` is in effect - it is by default - and the baseline has enough samples. An adaptive breaker trips at `NormalLatency` times `SlowCalls.Multiple`. |
| `NormalFailureRate` | How often a call to this dependency fails, as the breaker measures it. `null` unless `Failures` is in effect - it is by default - and the baseline has enough samples. The relative trip point is `max(Failures.AbsoluteFloor, NormalFailureRate * Failures.Multiple)`. |
| `Isolate()` | Forces the breaker into the `Isolated` state. An isolated breaker does not self-heal. |
| `Reset()` | Closes the breaker and clears its failure history. `NormalLatency` and `NormalFailureRate` survive, because they are measurements of the dependency rather than decisions about it. |

`Isolate` and `Reset` raise no events: they are administrative actions, not triggered by a call.

## `BreakerState`

The `BreakerState` enum defines the breaker's states:

| Value | Description |
| :--- | :--- |
| `Closed` | The breaker is operating normally. Calls pass through, and outcomes are sampled. |
| `Open` | The breaker has tripped. Calls are refused until the break duration expires. |
| `HalfOpen` | The break duration has expired. A limited number of trial calls (probes) are allowed through. |
| `Recovering` | The probes succeeded and the breaker is admitting a growing fraction of calls, refusing the rest. Only reachable when `Recovery` is set. |
| `Isolated` | The breaker has been forced open via the `Isolate` method. |

## `BreakerSettings`

`BreakerSettings` is a `sealed record` used to configure the breaker's trip and reset logic. All properties are `init`-only.

| Property | Default | Description |
| :--- | :--- | :--- |
| `ConsecutiveFailures` | 5 | The number of consecutive failures required to trip the breaker. |
| `FailureRatio` | `null` | An optional rate-based trip threshold in the range (0, 1]. This is evaluated alongside the consecutive failure counter. |
| `Failures` | `Failures.Above(5)` | The same trip, expressed as a multiple of the dependency's own measured error rate. On by default; set it to `null` to turn it off. Composes with `FailureRatio`, which stays the ceiling when both are set. See [`Failures`](#failures). |
| `MinimumCalls` | 20 | The minimum number of sampled calls in the window before a rate-based trip is evaluated. |
| `TripWindow` | 30 s | The sliding window the trip ratios are measured over. Distinct from the `Window` on `Failures` and `SlowCalls`, which are the baseline windows those trips measure "normal" over. |
| `SlowCallThreshold` | `null` | A constant duration above which an attempt counts as "slow", even if it succeeded. |
| `SlowCalls` | `SlowCalls.Above(3)` | The same trip, expressed as a multiple of measured normal latency. On by default; set it to `null` to turn it off. Composes with `SlowCallThreshold`: a call is slow when it is above either. See [`SlowCalls`](#slowcalls). |
| `SlowCallRatio` | 0.5 | The proportion of slow calls in the window that will trip the breaker. |
| `BreakDuration` | 15 s | The duration of the first break. |
| `MaxBreakDuration` | 2 min | The maximum break duration. The break duration doubles with each consecutive trip up to this limit. Set it equal to `BreakDuration` to disable growth. |
| `BreakJitter` | `Jitter.Equal` | How much randomness is applied to the break duration, so a fleet whose breakers opened together does not probe together. `Jitter.Equal` serves half the computed duration plus a random share of the other half; `Jitter.None` serves it exactly. Jitter never lengthens a break, so `MaxBreakDuration` still bounds it. |
| `HalfOpenProbes` | 1 | The number of concurrent trial calls allowed while `HalfOpen`. |
| `ProbeSuccesses` | 2 | The number of successful probes needed to return the breaker to `Closed`. Also the number of consecutive fast calls a `Recovery` ramp needs before it raises the admitted fraction. |
| `Recovery` | `null` | Hand the traffic back over a ramp rather than a cliff, through a `Recovering` state. Off by default, because a caller refused during the ramp would have been served by a cliffed breaker. See [`Recovery`](#recovery). |
| `Time` | `TimeProvider.System` | The clock used for timing. The breaker maintains its own clock so its state can be read by health endpoints without a policy. When the library builds the breaker (per-host or from configuration), it uses the policy's `Time` if no other clock is specified. See [the breaker's clock](../features/circuit-breaker.md#the-breakers-clock). |
| `Validate()` | N/A | Validates the settings and throws a `ResilienceConfigurationException` listing all found problems. |

### Implementation details

- **Evaluation**: Rate-based trips (including both slow-call forms) are not evaluated until `MinimumCalls` have occurred within the window.
- **Resource Efficiency**: trip-window arrays are only allocated if a rate-based trip is configured. A breaker relying solely on consecutive failures requires no arrays.
- **Sampling**: The breaker samples individual attempts. Only `Transient` outcomes are counted as evidence of failure.
- **Jitter**: The break duration is jittered once, at the moment the breaker opens, and the growth per consecutive open is computed from the nominal duration rather than the jittered one. `RetryAfterHint` and `CallRejectedException.RetryAfter` report the break actually being served.

## `Failures`

`Failures` is a `readonly record struct` that defines "too many failures" relative to how often the dependency normally fails. `Failures.Above(5)` means "five times its own recent error rate", and is a complete configuration; derive variants with `with`.

| Property | Default | Description |
| :--- | :--- | :--- |
| `Multiple` | N/A | How many times the baseline error rate counts as too many. Must be greater than 1. |
| `Window` | 5 min | The window the baseline is measured over. |
| `MinimumSamples` | 100 | Sampled outcomes required before the relative trip is armed. |
| `AbsoluteFloor` | 0.05 | The error rate the relative trip never fires below, whatever the baseline was. Must be in (0, 1]. |
| `Above(double multiple = 5.0)` | N/A | The static factory. |

### Implementation details

- **Sampling**: `Ok` and `Transient` outcomes feed the baseline, the same stream the trip window sees. `Throttled` and `Permanent` outcomes are not evidence about the dependency's health.
- **The baseline is separate from the trip window**: it is not cleared when the breaker opens, closes, or is `Reset`.
- **Trip point**: `min(FailureRatio, max(AbsoluteFloor, NormalFailureRate * Multiple))`, clamped to 1. The relative trip can only fire sooner than `FailureRatio`, never later.
- **Two failures minimum**: a relative trip needs at least two failures in the window whatever the ratio says, because at the default floor a single failure in a 20-call window is already 5%. An absolute `FailureRatio` is not held to that.
- **Combined validation**: `BreakerSettings.Validate` rejects a configuration where `Failures.Window` divided by `Multiple` is less than twice `TripWindow`. Such a breaker cannot open on the error rate at all - see [Breaker internals](../deep-dives/breaker-internals.md#the-relative-failure-ratio).
- **Default baseline**: `Window` widens beyond its 5-minute default when `BreakerSettings.TripWindow` needs it to, and no relative trip is defaulted on at all once that requirement passes an hour. Both apply to the default only - a `Failures` you wrote is used as written, or rejected.
- **Cost**: two `int[10]` rings per breaker, allocated whenever `Failures` is set - which, at the defaults, is always.

## `Recovery`

`Recovery` is a `readonly record struct` that says how a breaker hands the traffic back once the probes succeed. `Recovery.Over(0.25)` means "ramp back over a quarter of the break just served", and is a complete configuration; derive variants with `with`.

| Property | Default | Description |
| :--- | :--- | :--- |
| `Length` | N/A | How long the ramp lasts, as a fraction of the break just served. Must be greater than 0. |
| `MinimumLength` | 1 s | The shortest ramp, however brief the break was. |
| `MaximumLength` | 30 s | The longest ramp, however long the break was. The bound on what the feature can cost. |
| `InitialFraction` | 0.05 | The fraction of *calls* the ramp admits when it starts, and the floor it never drops below. Must be in (0, 1). |
| `Over(double length = 0.25)` | N/A | The static factory. |

### Implementation details

- **Admission**: the admitted fraction is the lower of a clock term, which climbs from `InitialFraction` to 1 across the ramp, and an evidence term. Calls outside it are refused exactly as an open breaker refuses them, through the same guarded rejection pause, with `StopReason.DependencyUnavailable`.
- **Evenly spaced, not random**: admission uses deficit accounting rather than a coin flip, so a ramp admits an even share of what it is offered. The fleet is already de-correlated by `BreakJitter`, which decides when each pod's ramp starts.
- **Evidence is AIMD**: the admitted fraction is adjusted using an additive-increase/multiplicative-decrease (AIMD) approach: a slow call halves the fraction (floored at `InitialFraction`), and `ProbeSuccesses` consecutive fast calls increase it by `InitialFraction` (capped at 1). This prevents the ramp from overshooting and saturating the dependency.
- **Failure**: one `Transient` outcome during the ramp re-opens the breaker, without waiting for the trip conditions a closed breaker is held to. The break it re-opens with is the grown one, because nothing has closed the breaker cleanly yet.
- **Slow calls during a ramp** stall it rather than counting towards the slow-call trip. Slow is the expected reading while a dependency warms, and tripping on it would make the stall unreachable.
- **A slow probe** starts the ramp rather than re-opening the breaker when `Recovery` is set, and the ramp then stalls at `InitialFraction`. Without a ramp there is no third answer available and a slow probe re-opens, as it always has.
- **Telemetry**: `BreakerClosed` is raised when the ramp starts, not when it completes - that is where the breaker stops refusing everything. `RetryAfter` is `null` for a refusal during a ramp, because the caller's next call is likely to be admitted.
- **`Reset`** closes without a ramp. It is administrative, and warming a dependency an operator has vouched for is not part of what "reset" means.
- **Cost**: five fields on the breaker, no allocation, and no executor contact - a ramp refusal takes the branch an open breaker's refusal already takes.

## `SlowCalls`

`SlowCalls` is a `readonly record struct` that defines a slow call relative to how long a call to the dependency normally takes. `SlowCalls.Above(3)` means "three times slower than normal", and is a complete configuration; derive variants with `with`.

| Property | Default | Description |
| :--- | :--- | :--- |
| `Multiple` | N/A | How much slower than normal an attempt has to be to count as slow. Must be greater than 1. |
| `Quantile` | 0.5 | The quantile of recent successful latency that counts as normal. Must be in (0, 0.5]. |
| `Window` | 5 min | The window the baseline is measured over. |
| `MinimumSamples` | 20 | Successful attempts required before the slow-call trip is armed. |
| `Above(double multiple = 3.0)` | N/A | The static factory. |

### Implementation details

- **Sampling**: Only successful attempts feed the baseline. A `Transient`, `Throttled`, or `Permanent` outcome says nothing about how long the dependency takes to do the work.
- **The baseline is separate from the trip window**: it is not cleared when the breaker opens, closes, or is `Reset`.
- **Combined validation**: `BreakerSettings.Validate` rejects a configuration where `Quantile` times `SlowCalls.Window` is less than twice `SlowCallRatio` times `TripWindow`. Such a breaker cannot open on latency at all - see [Breaker internals](../deep-dives/breaker-internals.md#the-adaptive-slow-call-threshold).
- **Default baseline**: `Window` widens beyond its 5-minute default when `BreakerSettings.TripWindow` needs it to, and no brownout trip is defaulted on at all once that requirement passes an hour. Both apply to the default only - a `SlowCalls` you wrote is used as written, or rejected.
- **Cost**: one `LatencyWindow` per breaker, about 3.4 KB, allocated whenever `SlowCalls` is set - which, at the defaults, is always.

For a detailed explanation of the logic, see [Breaker internals](../deep-dives/breaker-internals.md).
