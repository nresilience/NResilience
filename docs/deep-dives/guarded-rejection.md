---
title: Guarded rejection
description: Learn why NResilience introduces a mandatory delay when a call is refused by a guard.
order: 6
---

# Guarded rejection

Both an open circuit breaker and a depleted retry budget refuse calls. While the intuitive implementation is to return the refusal immediately, doing so can create a significant performance problem.

If a caller places a resilient call inside a tight polling loop, an immediate rejection results in a CPU spin. Without a forced pause, a tripped breaker generates errors at the speed of a method call, spiking the client's CPU usage and potentially generating more traffic than the original call would have. In this scenario, the guard intended to shed load instead becomes a load generator. AWS carves out an explicit exception for exactly this on its long-polling operations.

To prevent this, NResilience introduces a 100-millisecond pause before reporting a refusal. **Guarded rejection is not a "fail-fast" mechanism**, and this distinction is critical in the exact scenarios where guards are most needed.

## Pause constraints

The rejection pause is subject to the following constraints:

- **Deadline bound**: The pause is capped by the time remaining on the [deadline](../features/deadlines.md). A refusal will never cause a call to exceed the budget set by the caller. For example, if only 40 milliseconds remain on the deadline, the pause is 40 milliseconds.
- **Cancellation**: The pause observes the caller's cancellation token. Cancelling the operation during a rejection pause causes it to abort immediately.
- **Telemetry**: The pause is announced before it is served. The rejection event (`RejectedByBreaker` or `RejectedByBudget`, depending on the guard) includes the pause duration in the `Delay` field, allowing listeners to report the impending delay.

## Why the pause is not configurable

The rejection pause is not configurable because its purpose is to establish a minimum floor for the rate of a rejection loop, not to be a tunable performance parameter. 

A 100-millisecond delay is short enough to be negligible for most refused calls, yet long enough to make a CPU spin impossible. Adding a configuration option for this value would introduce unnecessary complexity without providing a practical benefit.

For callers who manage their own retries, `CallRejectedException.RetryAfter` provides a more useful value: it contains the remaining break duration of the circuit breaker or the time required for the retry budget's floor rate to accrue a new token.
