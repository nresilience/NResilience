---
title: Breaker internals
description: The state machine, the defaults that differ from Polly's, and why closing needs two probes.
order: 4
---

# Breaker internals

## The state machine

`Closed` samples every attempt. A trip moves it to `Open` with a break deadline. When a call arrives
after that deadline it becomes the breaker's probe and the state moves to `HalfOpen`; up to
`HalfOpenProbes` calls are in flight there at once. `ProbeSuccesses` successes close it; one failure
re-opens it with a longer break. `Isolated` is `Open` that never self-heals.

Reading `State` reports `HalfOpen` for an open breaker whose break has elapsed, because that is what
the next call will find - but the transition itself happens on admission, so a health endpoint polling
the state cannot consume the probe slot a real call needs.

## Every default here differs from Polly's, deliberately

Polly v8 removed classic consecutive-failure breaking, leaving a rate-based trip at `FailureRatio` 0.1
over a minimum throughput of 100 calls per 30 seconds. That means a service doing fewer than 100 calls
per 30 seconds can never open its breaker - and that is the median .NET service. So consecutive
failures is the default trip condition here, and the rate-based trip is opt-in alongside it rather
than instead of it.

**Two probe successes, not one.** Closing a breaker on a single lucky probe, in front of a dependency
that is still broken and a client fleet whose accumulated retries are waiting, is how breakers
oscillate and how a metastable failure sustains itself.

**The break duration doubles**, up to `MaxBreakDuration`, and the counter resets on a clean close. This
is exponential backoff applied to the breaker itself, and its absence is why breakers flap on a fixed
cadence forever.

**Slow calls count.** The most common real degradation is not a dependency returning errors, it is one
returning 200s at 30 times normal latency while your thread pool and connection pool fill up. An
error-rate breaker sits closed through the entire incident, which is why `SlowCallThreshold` exists and
why the attempt's duration is handed to the breaker along with its verdict.

## What counts as evidence

Only `Transient` outcomes. A `Throttled` response means the dependency is working correctly and
defending itself - tripping on it would turn a working rate limiter into an outage. A `Permanent`
outcome is overwhelmingly a client-side fact: your request was malformed, and the dependency is fine.

And it samples **attempts**, not operations, because that is the only reading that produces a useful
failure signal. It is also why admission is checked per attempt rather than once per call: a first
attempt that trips the breaker must stop the second, which is the entire point of having tripped.

## Concurrency

An uncontended `lock`, not a lock-free scheme. Sliding-window rotation is a multi-word operation whose
failure mode under `Interlocked` alone is a silently incorrect failure ratio, which is far worse than
being slow. An uncontended lock is roughly 20 nanoseconds and the callback it guards dominates by
orders of magnitude.

The window is ten buckets - 3-second granularity on the default 30-second window, finer than any trip
decision needs - and the arrays exist only when a rate-based trip is actually configured. A
consecutive-failures breaker is three fields and no array.

Transitions are handed back to the executor rather than raised under the lock, because a listener is
arbitrary user code and one slow listener holding that lock would serialize every call through the
breaker.

## Why the breaker owns its own clock

`State` and `OpenedAt` are read from health endpoints and admin handlers that have no policy in hand.
And one breaker shared by two policies with different clocks would have no single answer to "how long
have you been open?".

