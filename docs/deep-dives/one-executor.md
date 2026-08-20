---
title: One flat executor
description: Why there is no pipeline, and what fusing the layers into one async frame buys.
order: 1
---

# One flat executor

Every composed resilience library builds a chain: a retry strategy wrapping a timeout strategy
wrapping a breaker wrapping the call. It is a clean design and it has a cost that is invisible in the
source and unavoidable at run time.

Every `async` method that actually suspends heap-allocates its own state machine, and depth is a
linear multiplier. A chain of four strategies pays four of those boxes on the path that every real I/O
call takes - not on the fast path, not under contention, but always. The layers are not free
abstractions; each one is an allocation per call.

So this library has no layers. Admission, the deadline, the attempt loop, the per-attempt timeout,
classification, backoff and the attempt log are one `async` method. There is one box, and its size is
roughly the sum of the state that has to live across the awaits rather than the sum of the frames.

## What that buys, measured

Bytes above an identical un-wrapped suspending callback, one process, one run. The NResilience
figures are the ceilings the build enforces; the reference figures are the published values the same
harness reproduces within a range. Each ratio is a gate that fails the build: the fused loop must
stay at least 2.5x cheaper than a comparable layered pipeline on the yield harness, and at least 2.0x
cheaper over a real loopback socket.

| Arm | Ceiling | Ratio (gate) |
| --- | ---: | ---: |
| NResilience, full policy | 448 B | |
| Comparable layered pipeline (retry + timeout), same harness | ~1,291 B (harness range 1,100-1,600) | **>= 2.5x** (measured 3.2x) |
| NResilience, trivial policy | 368 B | |
| Comparable layered pipeline, empty | ~304 B (harness range 250-400) | **<= 1.25x** (measured 1.05x) |

Over a real loopback socket the same comparison measures **2.38x**, and the build gates that ratio
at 2.0x.

The last row is the honest shape of the result rather than a footnote. **Composition overhead scales
with layer count and a flat loop's does not**, so the fused design wins in proportion to how much
policy is configured - and at the trivial end there is nothing to win. That row is published and gated
alongside the flattering ones.

The socket figure is the more honest headline of the two, because real I/O registers on the cancellable
attempt token and `Task.Yield` does not.

## What it costs to build

The reason most libraries do not do this is that the fused loop is harder to write and harder to
extend. Everything that would have been a strategy is a branch inside one method, and the state that
would have been a local in a small frame is a field in a box whose size is a budgeted number. Caching
the policy's `Backoff` in a local, for readability, costs 56 bytes on every suspending call to save a
field load the JIT keeps in a register anyway. That is the tenor of the whole file.

The compensations are real, though. There is no composition order to get wrong, so the recurring
question for layered pipelines - whether the breaker sees attempts or whole operations, which
depends on whether the breaker was added before or after the retry - does not exist here. The
breaker samples attempts, always. And a policy is a value rather than a built pipeline, so deriving a
variant is one expression and equality is structural.

## What was given up

Extensibility through composition. You cannot write a strategy and insert it into the chain, because
there is no chain. What you can do is classify outcomes ([`Classifier`](../reference/classifier.md)),
compute your own delays (`Backoff.Custom`), run work before each attempt (`BeforeAttempt`) and observe
everything (`OnEvent`). That is a deliberately smaller opening, and the argument for it is that a small
public surface is the only thing that keeps an API stable long enough to be worth adopting: a library
that rewrites its core API loses the users who built on the old one, and the smaller the surface, the
less there is to get wrong later.

Go deeper: [where the allocations are](allocations.md).
