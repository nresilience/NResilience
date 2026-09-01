---
title: Where the allocations are
description: A detailed breakdown of the memory costs associated with resilience calls and how those costs are minimized and enforced.
order: 2
---

# Where the allocations are

NResilience manages performance with strict allocation budgets. Every figure below is a ceiling enforced by the build, measured with `GC.GetAllocatedBytesForCurrentThread()` in a Release build, using workstation non-concurrent GC.

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

Any regression in these figures fails CI. The ceilings include roughly 15% headroom for deterministic but non-identical allocation patterns across hardware architectures.

## The synchronous allocation floor

A full policy on the synchronous path cannot be completely allocation-free, for one safety-critical reason.

To support attempt timeouts, the callback must receive a token that can be cancelled. The executor cannot know whether a timeout will be needed until after the callback returns, so the cancellation source must exist before the call.

The executor also cannot hand out the pooled timer source's own token: `TryReset` preserves token identity, so a callback that outlived its attempt would observe the cancellation of the *next* operation. To prevent that, the executor creates one linked source per attempt, which sets a 64-byte allocation floor (72-byte ceiling).

A design that hands out its pooled token reduces this to 24 bytes, creating the hazard this design avoids.

## Callbacks that return `ValueTask`

A callback returning `ValueTask` costs the same as a `Task`-returning one. Both shapes share one attempt loop and one hoisted awaiter field, so the suspending figure is identical to the byte.

The difference is in the callback's own allocation. A `ValueTask` backed by an `IValueTaskSource` - the shape `Socket`, `Channel`, `PipeReader`, and `Stream` hand out - allocates nothing when it completes synchronously. Converting one to a `Task` costs 72 bytes to build a task for an answer already in hand, so a callback written `ct => reader.ReadAsync(ct).AsTask()` pays that on every call. The `ValueTask` overloads pass the result straight to the attempt loop instead:

| Sync-completing callback, trivial policy | Total allocation |
| :--- | :---: |
| `ValueTask` callback | **0 B** |
| The same callback via `.AsTask()` | 72 B |

The executor does this without an `await` on a second awaitable type. A hoisted awaiter field belongs to the generated state-machine type, so awaiting a `ValueTask` anywhere in the loop's source would enlarge the box for every caller, whichever shape they passed. Instead the invoker hands back `null` when the callback already has its result, and the loop reads it from a variable it already keeps. A `ValueTask` that genuinely suspends is converted to a `Task` - the one allocation a `Task`-returning callback would have made anyway.

> [!NOTE]
> The `ValueTask` overloads are extension methods rather than members of `Resilience`. An `async` lambda converts to both delegate shapes with neither conversion better, so declaring both as instance overloads would make `async ct => await client.GetAsync(url, ct)` fail to compile. C# searches for an extension method only when no instance method applies. Consequently, an `async` lambda binds to the `Task` overload, while a lambda that returns a `ValueTask` binds to the extension. Both are called `RunAsync`, and neither needs a `using`.

## Telemetry costs

Raising a `CallEvent` is allocation-free: it is a struct passed by value to an `Action<CallEvent>`.

The 48-72 bytes observed with a listener attached come from boxing two attempt results. A cross-cutting listener cannot be generic over the return type `T`, so values must be boxed. When `OnEvent` is `null`, the executor suppresses all event logic, so telemetry is free when unused.

## The one path that spends

Hedging is the exception to every figure above, and a deliberate one. A hedged call holds a list of legs, runs each in its own `async` local function, races them with `Task.WhenAny` over an array built per wait, and arms a `Task.Delay` for the threshold. There is no version of hedging that does not allocate; a design that pretended otherwise would be a worse design, not a cheaper one.

The measured figure - about 1300 B above the raw callback, on a call where no hedge actually fires - is roughly what a Polly retry-and-timeout pipeline costs *per call*. The difference is who pays it. `Hedge` selects a third execution loop, so a policy without `Hedge` pays nothing for that loop, and that is a gate rather than an intention:

- `A_policy_with_no_Hedge_pays_nothing_for_the_third_execution_path` compares the two arms in one sweep.
- `The_hedged_path_stays_within_its_own_budget` holds the hedged figure to a ceiling of its own.

One number applies to everybody who reads an attempt log: `Attempt` carries a `StartOffset`, which is 8 B per materialized attempt and is what makes overlapping attempts readable. It is charged only where a log is materialized at all - `TryRunAsync` and the failure path - so the suspending figures for the throwing entry points are unchanged.

## Implementation decisions based on measurement

Several design choices come from measurement rather than intuition:

- **Pooled vs. fresh sources**: A pooled cancellation source combined with a linked source is 96 bytes per call cheaper than a fresh linked source plus `CancelAfter`, because a pooled source keeps its timer across resets.
- **Avoid `Task.Delay` for timeouts**: `CancelAfter` on a token is much cheaper (about 96 bytes) than racing the call against a `Task.Delay` (about 408 bytes).
- **Task vs. ValueTask for hooks**: A `Task`-returning `BeforeAttempt` hook is 16 bytes cheaper per suspending call than a `ValueTask`-returning one, because Roslyn can share one hoisted awaiter field between the attempt and the backoff delay. The same reasoning is why the executor converts a pending `ValueTask` callback to a `Task` instead of awaiting it directly.
- **Budget storage**: The breaker and retry budget together add 8 bytes to the state-machine box. The breaker costs nothing - the policy holding it is already a field. The budget costs one reference field, because it must be resolved before the loop rather than cached per-thread (a continuation resumes on whichever pool thread is free, so a per-thread cache would miss).

## Clarifications on performance claims

### Allocation vs. "zero allocation"
NResilience does not claim to be "zero allocation" in an unqualified sense: every `async` method that suspends must allocate its state machine. The claim is one box instead of one box per layer, and the build gates enforce it.

### Latency vs. allocations
The library publishes latency as a trend, not a gated metric. Shared CI runners are noisy, so a latency gate would be either too loose to be useful or too tight to be stable. Allocations are deterministic, so they are the primary enforced metric.
