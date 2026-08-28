---
title: Breaker
description: Reference for the Breaker class, its settings, and the possible states of a circuit breaker.
order: 5
---

# `Breaker`

The `Breaker` is a `sealed class` that implements the circuit breaker pattern. It is a live object; create it and share it across the calls you intend to protect.

| Member | Description |
| :--- | :--- |
| `Breaker(BreakerSettings? settings = null)` | Creates a new breaker. This constructor validates the settings and throws a `ResilienceConfigurationException` if they are invalid. |
| `Name` | An `init`-only property used for diagnostics and health endpoints. |
| `Settings` | The `BreakerSettings` used to configure the breaker. |
| `State` | The current state of the breaker. If a breaker is open but the break duration has elapsed, it reports `HalfOpen` because the next call will be treated as a probe. Reading this property does not consume a probe slot. |
| `OpenedAt` | The timestamp of when the breaker last opened, or `null` if it is currently closed. |
| `NormalLatency` | How long a healthy call to this dependency currently takes, as the breaker measures it. `null` unless `SlowCalls` is configured and the baseline has enough samples. An adaptive breaker trips at `NormalLatency` times `SlowCalls.Multiple`. |
| `Isolate()` | Forces the breaker into the `Isolated` state. An isolated breaker does not self-heal. |
| `Reset()` | Closes the breaker and clears its failure history. `NormalLatency` survives, because it is a measurement of the dependency rather than a decision about it. |

`Isolate` and `Reset` do not raise events because they are administrative actions and not triggered by a specific call.

## `BreakerState`

The `BreakerState` enum defines the possible states of the circuit breaker:

| Value | Description |
| :--- | :--- |
| `Closed` | The breaker is operating normally. Calls pass through, and outcomes are sampled. |
| `Open` | The breaker has tripped. Calls are refused until the break duration expires. |
| `HalfOpen` | The break duration has expired. A limited number of trial calls (probes) are allowed through. |
| `Isolated` | The breaker has been forced open via the `Isolate` method. |

## `BreakerSettings`

`BreakerSettings` is a `sealed record` used to configure the breaker's trip and reset logic. All properties are `init`-only.

| Property | Default | Description |
| :--- | :--- | :--- |
| `ConsecutiveFailures` | 5 | The number of consecutive failures required to trip the breaker. |
| `FailureRatio` | `null` | An optional rate-based trip threshold in the range (0, 1]. This is evaluated alongside the consecutive failure counter. |
| `MinimumCalls` | 20 | The minimum number of sampled calls in the window before a rate-based trip is evaluated. |
| `Window` | 30 s | The sliding window duration over which rates are measured. |
| `SlowCallThreshold` | `null` | A constant duration above which an attempt is considered "slow," even if it succeeded. |
| `SlowCalls` | `null` | The same trip, expressed as a multiple of measured normal latency. Set this or `SlowCallThreshold`, not both. See [`SlowCalls`](#slowcalls). |
| `SlowCallRatio` | 0.5 | The proportion of slow calls in the window that will trip the breaker. |
| `BreakDuration` | 15 s | The duration of the first break. |
| `MaxBreakDuration` | 2 min | The maximum break duration. The break duration doubles with each consecutive trip up to this limit. Set this equal to `BreakDuration` to disable growth. |
| `HalfOpenProbes` | 1 | The number of concurrent trial calls allowed while in the `HalfOpen` state. |
| `ProbeSuccesses` | 2 | The number of successful probes required to return the breaker to the `Closed` state. |
| `Time` | `TimeProvider.System` | The clock used for timing. The breaker maintains its own clock so its state can be read by health endpoints without a policy. When the library builds the breaker (per-host or from configuration), it uses the policy's `Time` if no other clock is specified. See [the breaker's clock](../features/circuit-breaker.md#the-breakers-clock). |
| `Validate()` | N/A | Validates the settings and throws a `ResilienceConfigurationException` listing all found problems. |

### Implementation details

- **Evaluation**: Rate-based trips (including both slow-call forms) are not evaluated until `MinimumCalls` have occurred within the window.
- **Resource Efficiency**: Window arrays are only allocated if a rate-based trip is configured. A breaker relying solely on consecutive failures requires no arrays.
- **Sampling**: The breaker samples individual attempts. Only `Transient` outcomes are counted as evidence of failure.

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
- **Combined validation**: `BreakerSettings.Validate` rejects a configuration where `Quantile` times `SlowCalls.Window` is less than twice `SlowCallRatio` times `Window`. Such a breaker cannot open on latency at all - see [Breaker internals](../deep-dives/breaker-internals.md#the-adaptive-slow-call-threshold).
- **Cost**: one `LatencyWindow` per breaker, allocated only when `SlowCalls` is set.

For a detailed explanation of the logic, see [Breaker internals](../deep-dives/breaker-internals.md).
