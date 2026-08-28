---
title: Where the allocations are
description: A detailed breakdown of the memory costs associated with resilience calls and how those costs are minimized and enforced.
order: 2
---

# Where the allocations are

Performance in NResilience is managed through strict allocation budgets. Every figure below represents a ceiling enforced by the build process. These measurements are taken using `GC.GetAllocatedBytesForCurrentThread()` in a Release build on target frameworks, using workstation non-concurrent GC.

For detailed measurements and gate constants, see [`Budgets.cs`](https://github.com/nresilience/NResilience/blob/main/tests/NResilience.Gates/Budgets.cs).

| Scenario | Allocation ceiling (above unwrapped callback) |
| :--- | :---: |
| `Resilience.None` (any callback) | **0 B** |
| Sync-completing, no attempt timeout, static lambda with state | **0 B** |
| Sync-completing, full policy | 72 B |
| Suspending, full policy | 448 B |
| Suspending, full policy (with cancellable caller token) | 464 B |
| `TryRunAsync`, full policy | 640 B |
| With a telemetry listener attached (delta) | 72 B |
| Sync-completing `ValueTask` callback, no attempt timeout | **0 B** |
| Sync-completing `ValueTask` callback, full policy | 72 B |
| Suspending `ValueTask` callback, full policy | 448 B |
| Suspending, hedging configured (no hedge firing) | 1500 B |

To prevent performance drift, any regression in these figures fails the continuous integration (CI) pipeline. The ceilings include approximately 15% headroom to account for deterministic but non-identical allocation patterns across different hardware architectures.

## The synchronous allocation floor

A full policy on the synchronous path cannot be completely allocation-free due to a critical safety constraint.

To support attempt timeouts, the callback must receive a token that can be cancelled. The executor cannot determine if a timeout will be necessary until after the callback returns, meaning the cancellation source must be created before the call. 

Furthermore, the executor cannot hand out the pooled timer source's own token because `TryReset` preserves token identity. If a callback outlived its attempt, it would observe the cancellation of the *next* operation. To prevent this, the executor creates one linked source per attempt. The linked source creates an allocation floor of 64 bytes (with a 72-byte ceiling).

A design that hands out its pooled token reduces this to 24 bytes, creating the hazard this design avoids.

## Callbacks that return `ValueTask`

A callback that returns `ValueTask` has the same cost as a `Task`-returning one. Both shapes share a single attempt loop and a single hoisted awaiter field, so the suspending figure is identical to the byte.

The difference lies in the callback's own allocation. A `ValueTask` backed by an `IValueTaskSource` - the shape `Socket`, `Channel`, `PipeReader` and `Stream` hand out - allocates nothing when it completes synchronously. Converting one to a `Task` costs 72 bytes to build a task for an answer already in hand, so a callback written `ct => reader.ReadAsync(ct).AsTask()` pays that on every call. The `ValueTask` overloads pass the result directly to the attempt loop instead:

| Sync-completing callback, trivial policy | Total allocation |
| :--- | :---: |
| `ValueTask` callback | **0 B** |
| The same callback via `.AsTask()` | 72 B |

The executor reaches this without an `await` on a second awaitable type. A hoisted awaiter field belongs to the generated state-machine type, so awaiting a `ValueTask` anywhere in the loop's source would enlarge the box for every caller, whichever shape they passed. Instead the invoker hands back `null` when the callback already has its result, and the loop reads it from a variable it already keeps. A `ValueTask` that genuinely suspends is converted to a `Task`, which costs the one allocation a `Task`-returning callback would have made anyway.

> [!NOTE]
> The `ValueTask` overloads are extension methods rather than members of `Resilience`. An `async` lambda converts to both delegate shapes with neither conversion better, so declaring both as instance overloads would make `async ct => await client.GetAsync(url, ct)` fail to compile. C# searches for an extension method only when no instance method applies. Consequently, an `async` lambda binds to the `Task` overload, while a lambda that returns a `ValueTask` binds to the extension. Both are called `RunAsync`, and neither needs a `using`.

## Telemetry costs

Raising a `CallEvent` is allocation-free because it is a struct passed by value to an `Action<CallEvent>`. 

The observed cost of 48–72 bytes when a listener is attached comes from boxing two attempt results. Because a cross-cutting listener cannot be generic over the return type `T`, values must be boxed. When `OnEvent` is `null`, the executor suppresses all event logic, ensuring that the telemetry system is "free when unused."

## The one path that spends

Hedging is the exception to every figure above, and a deliberate one. A hedged call holds a list of legs, runs each in its own `async` local function, races them with `Task.WhenAny` over an array built per wait, and arms a `Task.Delay` for the threshold. There is no version of hedging that does not allocate; a design that pretended otherwise would be a worse design, not a cheaper one.

The measured figure - about 1300 B above the raw callback, on a call where no hedge actually fires - is roughly what a Polly retry-and-timeout pipeline costs *per call*. The difference is who pays it. `Hedge` selects a third execution loop, so a policy without `Hedge` pays nothing for that loop, and that is a gate rather than an intention:

- `A_policy_with_no_Hedge_pays_nothing_for_the_third_execution_path` compares the two arms in one sweep.
- `The_hedged_path_stays_within_its_own_budget` holds the hedged figure to a ceiling of its own.

One number applies to everybody who reads an attempt log: `Attempt` carries a `StartOffset`, which is 8 B per materialized attempt and is what makes overlapping attempts readable. It is charged only where a log is materialized at all - `TryRunAsync` and the failure path - so the suspending figures for the throwing entry points are unchanged.

## Implementation decisions based on measurement

Several design choices were made based on empirical measurement rather than intuition:

- **Pooled vs. Fresh Sources**: Using a pooled cancellation source combined with a linked source is 96 bytes per call cheaper than creating a fresh linked source and calling `CancelAfter`. This is because a pooled source preserves its timer across resets.
- **Avoid `Task.Delay` for Timeouts**: Using `CancelAfter` on a token is significantly more efficient (approx. 96 bytes) than racing the call against a `Task.Delay` (approx. 408 bytes).
- **Task vs. ValueTask for Hooks**: A `Task`-returning `BeforeAttempt` hook is 16 bytes cheaper per suspending call than a `ValueTask`-returning one. This is because Roslyn can share a single hoisted awaiter field between the attempt and the backoff delay. The same reasoning is why the executor converts a pending `ValueTask` callback to a `Task` rather than awaiting it directly.
- **Budget Storage**: The breaker and the retry budget together add 8 bytes to the state-machine box. The breaker costs nothing, because the policy holding it is already a field; the budget costs one reference field, because it must be resolved before the loop rather than cached per-thread (a continuation resumes on whichever pool thread is free, so a per-thread cache would be missed).

## Clarifications on performance claims

### Allocation vs. "Zero Allocation"
NResilience does not claim to be "zero allocation" in an unqualified sense. Every `async` method that suspends must allocate its state machine. The claim is that NResilience uses **one box** instead of one box per layer. This claim is strictly enforced by the build gates.

### Latency vs. Allocations
The library publishes latency as a trend rather than a gated metric. Because shared CI runners are noisy, a latency gate would either be too loose to be useful or too tight to be stable. Since allocations are deterministic, they are the primary metric enforced by the build.
