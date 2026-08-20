---
title: Where the allocations are
description: A detailed breakdown of the memory costs associated with resilience calls and how those costs are minimized and enforced.
order: 2
---

# Where the allocations are

Performance in NResilience is managed through strict allocation budgets. Every figure below represents a ceiling enforced by the build process. These measurements are taken using `GC.GetAllocatedBytesForCurrentThread()` in a Release build on target frameworks, using workstation non-concurrent GC.

For detailed measurements and gate constants, see [`Budgets.cs`](../../tests/NResilience.Gates/Budgets.cs).

| Scenario | Allocation ceiling (above unwrapped callback) |
| :--- | :---: |
| `Resilience.None` (any callback) | **0 B** |
| Sync-completing, no attempt timeout, static lambda with state | **0 B** |
| Sync-completing, full policy | 72 B |
| Suspending, full policy | 448 B |
| Suspending, full policy (with cancellable caller token) | 464 B |
| `TryRunAsync`, full policy | 640 B |
| With a telemetry listener attached (delta) | 72 B |

To prevent performance drift, any regression in these figures fails the continuous integration (CI) pipeline. The ceilings include approximately 15% headroom to account for deterministic but non-identical allocation patterns across different hardware architectures.

## The synchronous allocation floor

A full policy on the synchronous path cannot be completely allocation-free due to a critical safety constraint.

To support attempt timeouts, the callback must receive a token that can be cancelled. The executor cannot determine if a timeout will be necessary until after the callback returns, meaning the cancellation source must be created before the call. 

Furthermore, the executor cannot hand out the pooled timer source's own token because `TryReset` preserves token identity. If a callback outlived its attempt, it would observe the cancellation of the *next* operation. To prevent this, the executor creates one linked source per attempt. This linked source constitutes the allocation floor of 64 bytes (with a 72-byte ceiling).

A design that hands out its pooled token reaches 24 bytes here, which is the exact hazard this design refuses.

## Telemetry costs

Raising a `CallEvent` is allocation-free because it is a struct passed by value to an `Action<CallEvent>`. 

The observed cost of 48–72 bytes when a listener is attached comes from boxing two attempt results. Because a cross-cutting listener cannot be generic over the return type `T`, values must be boxed. When `OnEvent` is `null`, the executor suppresses all event logic, ensuring that the telemetry system is "free when unused."

## Implementation decisions based on measurement

Several design choices were made based on empirical measurement rather than intuition:

- **Pooled vs. Fresh Sources**: Using a pooled cancellation source combined with a linked source is 96 bytes per call cheaper than creating a fresh linked source and calling `CancelAfter`. This is because a pooled source preserves its timer across resets.
- **Avoid `Task.Delay` for Timeouts**: Using `CancelAfter` on a token is significantly more efficient (approx. 96 bytes) than racing the call against a `Task.Delay` (approx. 408 bytes).
- **Task vs. ValueTask for Hooks**: A `Task`-returning `BeforeAttempt` hook is 16 bytes cheaper per suspending call than a `ValueTask`-returning one. This is because Roslyn can share a single hoisted awaiter field between the attempt and the backoff delay.
- **Budget Storage**: The breaker and the retry budget together add 8 bytes to the state-machine box. The breaker costs nothing, because the policy holding it is already a field; the budget costs one reference field, because it must be resolved before the loop rather than cached per-thread (a continuation resumes on whichever pool thread is free, so a per-thread cache would be missed).

## Clarifications on performance claims

### Allocation vs. "Zero Allocation"
NResilience does not claim to be "zero allocation" in an unqualified sense. Every `async` method that suspends must allocate its state machine. The claim is that NResilience uses **one box** instead of one box per layer. This claim is strictly enforced by the build gates.

### Latency vs. Allocations
The library publishes latency as a trend rather than a gated metric. Because shared CI runners are noisy, a latency gate would either be too loose to be useful or too tight to be stable. Since allocations are deterministic, they are the primary metric enforced by the build.
