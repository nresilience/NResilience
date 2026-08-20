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
Dependency degradation often manifests as high latency rather than explicit errors. A service returning `200 OK` at 30 times the normal latency can exhaust thread and connection pools. The `SlowCallThreshold` allows the breaker to trip based on duration, ensuring that "slow" calls are treated as failures.

## Evidence and sampling

The breaker only counts `Transient` outcomes as evidence of failure.
- **Throttled responses**: These indicate the dependency is working correctly by defending itself. Tripping on these would turn a functioning rate limiter into a full outage.
- **Permanent failures**: These are typically client-side errors (e.g., malformed requests) and do not indicate a dependency failure.

The breaker samples **attempts**, not logical operations. This is critical because a first attempt that trips the breaker must stop the second attempt immediately; checking admission only once per call would defeat the purpose of the breaker.

## Concurrency and implementation

### Lock strategy
The breaker uses a standard `lock` rather than a lock-free scheme. Sliding-window rotation is a multi-word operation; implementing this with `Interlocked` alone could lead to silently incorrect failure ratios. Since an uncontended lock takes approximately 20 nanoseconds and the protected callback is orders of magnitude slower, the lock does not introduce a bottleneck.

### Memory efficiency
To minimize overhead, the window is divided into ten buckets (providing 3-second granularity for a 30-second window). The arrays required for rate-based tracking are only allocated if a rate-based trip is configured. A breaker relying solely on consecutive failures uses only three fields and no arrays.

### Event dispatch
Breaker transitions are passed back to the executor rather than being raised inside the lock. This prevents arbitrary user code in a listener from holding the lock and serializing all calls through the breaker.

## Clock ownership

The breaker maintains its own `TimeProvider`. This is necessary because `State` and `OpenedAt` are often read by health endpoints or admin handlers that do not have access to a specific policy. Additionally, a shared breaker used by multiple policies with different clocks would have no consistent way to determine how long it has been open.
