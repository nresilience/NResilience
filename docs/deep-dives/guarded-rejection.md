---
title: Guarded rejection
description: Learn why NResilience introduces a mandatory delay when a call is refused by a guard.
order: 6
---

# Guarded rejection

Both an open circuit breaker and a depleted retry budget refuse calls. The intuitive implementation returns the refusal immediately, and that creates a performance problem.

Put a resilient call in a tight polling loop and an immediate rejection becomes a CPU spin: a tripped breaker generates errors at the speed of a method call, spiking the client's CPU and potentially generating more traffic than the original call would have. The guard meant to shed load becomes a load generator. AWS carves out an explicit exception for exactly this on its long-polling operations.

To prevent it, NResilience pauses 100 milliseconds before reporting a refusal. **Guarded rejection is not a "fail-fast" mechanism**, and the distinction matters most exactly where guards are most needed.

## Pause constraints

The rejection pause is subject to the following constraints:

- **Deadline bound**: Capped by the time remaining on the [deadline](../features/deadlines.md). A refusal never pushes a call past the caller's budget: 40 milliseconds left on the deadline means a 40-millisecond pause.
- **Cancellation**: The pause observes the caller's cancellation token, so cancelling during a rejection pause aborts immediately.
- **Telemetry**: The pause is announced before it is served. The rejection event (`RejectedByBreaker` or `RejectedByBudget`) carries the pause duration in `Delay`, so listeners can report the impending delay.

## Why the pause is not configurable

The pause is not configurable because its purpose is to set a floor under the rate of a rejection loop, not to be a tunable performance parameter.

A 100-millisecond delay is short enough to be negligible for most refused calls and long enough to make a CPU spin impossible. A configuration option for it would add complexity without practical benefit.

For callers who manage their own retries, `CallRejectedException.RetryAfter` provides a more useful value: it contains the remaining break duration of the circuit breaker or the time required for the retry budget's floor rate to accrue a new token.
