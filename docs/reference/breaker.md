---
title: Breaker
description: The breaker object, its settings, and its four states.
order: 5
---

# `Breaker`

`sealed class Breaker`. A live object: construct it, hold it, and share it exactly as widely as you
intend.

| Member | Meaning |
| --- | --- |
| `Breaker(BreakerSettings? settings = null)` | Creates one. Validates the settings, throwing `ResilienceConfigurationException`. |
| `Name` | `init`-only. Used in diagnostics and health endpoints. |
| `Settings` | The settings it was built with. |
| `State` | What it is doing now. An open breaker whose break has elapsed reports `HalfOpen`, because that is what the next call will find. Reading it never consumes a probe slot. |
| `OpenedAt` | When it last opened, or null while closed. |
| `Isolate()` | Force it open. Never self-heals. |
| `Reset()` | Close it and forget the history. |

`Isolate` and `Reset` raise no events, because there is no call to attribute them to.

## `BreakerState`

| Value | Meaning |
| --- | --- |
| `Closed` | Calls pass through. Outcomes are being sampled. |
| `Open` | Calls are refused until the break duration expires. |
| `HalfOpen` | A trickle of trial calls is allowed through. |
| `Isolated` | Forced open by `Isolate`. |

## `BreakerSettings`

`sealed record`. Every property is `init`-only.

| Property | Default | Meaning |
| --- | --- | --- |
| `ConsecutiveFailures` | 5 | Consecutive failures before opening. |
| `FailureRatio` | null | Optional rate-based trip, in (0, 1]. Evaluated alongside the counter. |
| `MinimumCalls` | 20 | Sampled calls in the window before any rate is evaluated. |
| `Window` | 30 s | The sliding window rates are measured over. |
| `SlowCallThreshold` | null | An attempt slower than this counts as slow, even when it succeeded. |
| `SlowCallRatio` | 0.5 | The proportion of slow calls in the window that opens it. |
| `BreakDuration` | 15 s | How long the first break lasts. |
| `MaxBreakDuration` | 2 min | The break doubles per consecutive open, up to this. Set equal to `BreakDuration` to disable growth. |
| `HalfOpenProbes` | 1 | Concurrent trial calls while half-open. |
| `ProbeSuccesses` | 2 | Successful probes required to close. |
| `Time` | `TimeProvider.System` | The clock. A breaker owns its own, because its state is read from health endpoints that hold no policy. |
| `Validate()` | | Throws `ResilienceConfigurationException` listing every problem at once. |

Nothing rate-based, `SlowCallThreshold` included, is evaluated until `MinimumCalls` outcomes have
landed in the window. The window arrays are allocated only when a rate-based trip is configured: a
consecutive-failures breaker is three fields and no array.

The breaker samples individual attempts, and only `Transient` outcomes count as failure evidence.

Go deeper: [Breaker internals](../deep-dives/breaker-internals.md).

