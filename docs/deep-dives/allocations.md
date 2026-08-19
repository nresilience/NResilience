---
title: Where the allocations are
description: What a call costs, why the synchronous floor is 64 bytes rather than zero, and how the budgets are enforced.
order: 2
---

# Where the allocations are

Every figure below is a test that fails the build. They are measured with
`GC.GetAllocatedBytesForCurrentThread()` in a plain xunit project that depends on no benchmark
harness, on both target frameworks, in Release, under workstation non-concurrent GC.

| Scenario | Overhead above an identical un-wrapped callback |
| --- | ---: |
| `Resilience.None`, any callback | **0** |
| Sync-completing, no attempt timeout, static lambda with state | **0** |
| Sync-completing, full policy | 64 B |
| Suspending, full policy | 384 B |
| Suspending, full policy, over a real loopback socket | 528 B |
| A listener attached, delta | 48 B |

## Why the synchronous floor is 64 bytes, not zero

An earlier revision of the design budgeted zero for a full policy on the synchronous path. It is not
reachable, and the reason is worth stating because it is a constraint rather than an implementation
failure.

The callback must receive a token the attempt timeout can cancel. Whether the timeout was needed
cannot be known until *after* the callback returns, so the source cannot be created lazily. And the
pooled timer source's own token must never be handed to user code, because `TryReset` preserves token
identity - a callback that outlived its attempt would observe the next operation's cancellation. One
linked source per attempt is therefore the floor, and the floor measures 64 bytes.

Polly reaches 24 bytes here by handing out its pooled token, which is the exact hazard this design
refuses.

## Why a listener costs 48 bytes and not zero

`CallEvent` is a struct passed by value to an `Action<CallEvent>`, so raising one allocates nothing.
The 48 bytes are two boxed attempt results, and they exist only because a genuinely cross-cutting
listener has no `T` to be generic over. With `OnEvent = null` the executor raises nothing and pays
nothing, which is what pay-for-play has to mean if it is to mean anything.

## The things that were measured rather than reasoned about

**A pooled cancellation source plus a linked one beats one fresh linked source.** The tempting shortcut
- create one linked source from the caller's token and call `CancelAfter` on it, dodging the second
source - measures 96 bytes per call **worse**. A pooled source keeps its timer across `TryReset`, so
its `CancelAfter` allocates nothing, and a fresh source's cannot.

**Never implement a timeout by racing `Task.Delay`.** `CancelAfter` costs 96 bytes against roughly 408
for a created-then-cancelled delay.

**A `Task`-returning `BeforeAttempt` is cheaper than a `ValueTask`-returning one**, by 16 bytes on
every suspending call whether the hook is set or not, because Roslyn shares one hoisted awaiter field
between await sites of the same awaiter type and the attempt and the backoff delay already need that
field.

**The breaker and the budget together cost 8 bytes** of state-machine box. The breaker costs nothing,
because the policy holding it is already a field; the budget costs one reference field, because it has
to be resolved before the loop rather than after each await - a continuation resumes on whichever pool
thread is free, so a per-thread cache would be missed.

## What is not claimed

"Zero allocation", unqualified. Every `async` method that actually awaits allocates its state machine,
and no library-side trick removes it. What is claimed is one box instead of one per layer, and that
claim is gated.

Latency is published as a trend and never gated. Shared CI runners are noisy enough that a latency
gate is either loose enough to catch nothing or tight enough to flake weekly, and a flaky gate gets
disabled within a month. Allocations are deterministic; those are what the build enforces.

The full conditions, the arm list and the Native AOT figures - identical to the byte - are in
[`plans/phase-0b-results.md`](../../plans/phase-0b-results.md).

