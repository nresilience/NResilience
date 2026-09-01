---
title: One flat executor
description: Explore why NResilience uses a fused execution loop instead of a strategy pipeline to minimize allocations and improve performance.
order: 1
---

# One flat executor

Most resilience libraries use a layered composition pattern: a retry strategy wraps a timeout strategy, which in turn wraps a circuit breaker, which finally wraps the call. While this is a clean architectural design, it introduces a hidden runtime cost.

In .NET, every `async` method that suspends heap-allocates its own state machine. In a layered pipeline, each strategy adds a new frame to the call stack. For a typical resilience chain, this means four or more state machine allocations for every single I/O call - regardless of whether the fast path is taken or the system is under contention. These layers are not free abstractions; they are per-call allocations.

To eliminate this overhead, NResilience uses a "flat" executor. Admission, deadline tracking, the attempt loop, per-attempt timeouts, classification, backoff, and the attempt log are all fused into a single `async` method. This reduces the overhead to one state machine box, whose size is the sum of the necessary state rather than the sum of multiple frames.

## Performance measurements

The following data compares the memory overhead of a fused loop against a comparable layered pipeline. The measurements represent bytes allocated above an identical unwrapped suspending callback.

| Configuration | NResilience (Fused) | Layered Pipeline | Ratio (Gate) |
| :--- | :---: | :---: | :---: |
| Full policy (Retry + Timeout) | 448 B | ~1,291 B (harness range 1,100-1,600) | **>= 2.5x** (Measured 3.2x) |
| Trivial policy (Empty) | 368 B | ~304 B (harness range 250-400) | **<= 1.25x** (Measured 1.05x) |

When measured over a real loopback socket - which more accurately reflects real-world I/O and cancellation token registration - the fused design is **2.41x** cheaper. The build process enforces a minimum ratio of 2.0x on Linux and macOS to ensure this performance advantage is maintained; Windows is excluded because Polly's arm does not measure repeatably there, while the deterministic `Task.Yield` gate runs on all three. The socket figure is the more honest headline of the two, because real I/O registers on the cancellable attempt token and `Task.Yield` does not.

**Key takeaway**: Composition overhead scales with the number of layers. A flat loop's overhead does not. The fused design's advantage grows in proportion to the complexity of the configured policy. At the trivial end, there is effectively nothing to win, which is why the trivial policy ratio is near 1.0x.

## The cost of the fused design

The primary trade-off for this performance is increased implementation complexity. A fused loop is harder to write and extend because every "strategy" is a branch inside one large method. State that would have been a local variable in a small frame becomes a field in a budgeted state box. 

For example, caching the policy's `Backoff` in a local variable for readability can add 56 bytes to every suspending call, potentially offsetting the gains of the flat loop. This level of scrutiny is required throughout the executor's implementation.

However, the fused design provides several architectural advantages:
- **No composition errors**: There is no "wrong order" to assemble the pipeline. You never have to wonder if the circuit breaker sees individual attempts or whole operations; it always samples attempts.
- **Value-based policies**: A policy is a value rather than a built pipeline. This makes deriving a variant a single expression and ensures that equality is structural.

## Trade-offs in extensibility

By choosing a flat executor, NResilience gives up extensibility through composition. You cannot write a custom strategy and insert it into a chain because there is no chain.

Instead, the library provides targeted extension points:

| Need | Mechanism |
| :--- | :--- |
| Decide what counts as what | [`Classifier`](../features/classification.md) |
| Compute your own delay between attempts | `Backoff.Custom` |
| Run code before each attempt (refresh a token, build a fresh request) | `BeforeAttempt` |
| Monitor every stage of the process | `OnEvent` |
| Add a custom admission-control guard (a distributed lock, a hand-rolled limiter, anything that should refuse a call before it reaches the dependency) | The callback, plus a classifier rule - see [Building a custom guard](admission-control.md#building-a-custom-guard) |
| Refuse an attempt as a value, without throwing | `Admit`, an opt-in second execution path - see [The Admit hook](admission-control.md#the-admit-hook) |
| Compose arbitrary logic around an HTTP call | Chain another `DelegatingHandler` alongside `ResilienceHandler` |

The two admission rows and the HTTP row are easy to miss because none reads as "the pipeline": a
custom guard is an ordinary exception (or, via `Admit`, an ordinary return value) classified to a
verdict rather than a strategy object, and HTTP composition happens through `DelegatingHandler`, not
through the policy. All three get the same treatment as the built-in ones - correct backoff curve,
breaker exemption, retry-budget exemption, telemetry - without adding a layer to the executor.
`Admit` is the one exception to "without adding a layer": configuring it selects a second execution
path with one extra hoisted field, paid only by callers who configure it. See
[The Admit hook](admission-control.md#the-admit-hook).

> [!CAUTION]
> `BeforeAttempt` is awaited outside the executor's classification logic. An exception it throws is
> not retried, not logged to the attempt log, and raises no `CallEvent` - it propagates out of the
> call unchanged. Use it for setup that always has to run (refreshing a token, building a fresh
> request), not for anything that should be able to refuse an attempt. A guard that needs retry,
> backoff, or telemetry belongs in the callback instead - see
> [Building a custom guard](admission-control.md#building-a-custom-guard).

This restricted surface is a deliberate choice. A smaller public API is more likely to remain stable over time, reducing the need for breaking changes and making the library more reliable for long-term adoption.

## The streaming path

The executor has a fourth loop, and it exists for one reason: **the attempt source must outlive the attempt.**

A call's attempt owns nothing that outlives it, so the executor tears everything down when the attempt ends - the linked attempt source disposed, the pooled ceiling source returned. That contract is wrong for a stream. A streaming attempt that produces its first element hands a live enumerator and its token to the caller, who finishes the enumeration arbitrarily later and on another thread. If the executor disposed the attempt source at attempt end, the surviving enumerator would hold a token whose registrations start throwing `ObjectDisposedException` mid-enumeration.

So the streaming loop keeps the same shell - admission, deadline, `BeforeAttempt`, the two-source timeout arrangement, `AfterAttempt` - and changes only which exit owns teardown:

- **Losing attempts** tear down exactly like a call's: enumerator disposed (which runs the source's own `finally` blocks, so a transport cleans up a losing call), linked source disposed, timer returned to the pool. An empty source is a success that tears down the same way, because nothing survived.
- **The winning attempt** hands its enumerator, linked source and timer to a handover block past the loop, and the **consumer's** enumeration drives the epilogue: the `finally` around the yields disposes the enumerator, then the linked source, then the timer - **disposed, never returned to the pool**. This last rule is the one whose violation is silent: `CtsPool.TryReset` preserves token identity, so a source returned while a surviving stream still holds a registration on its token lets the *next* tenant's `CancelAfter` cancel that stream, arbitrarily later and on another thread. The winning attempt's pooled timer is a one-shot cost, paid deliberately and recorded in the gate's budget ledger.
- **The disarm race** is closed with one bool read. The timer is disarmed the moment an element is in hand (`CancelAfter(Timeout.InfiniteTimeSpan)`), and then tested - because the timer can fire in the window between `MoveNextAsync` returning `true` and the disarm landing. If it fired, the attempt overran its ceiling before the element was in hand: the element is dropped, the attempt is judged a timeout like any other, and the consumer never receives an element whose attempt was already dead.

Two C# restrictions do real work in this design. `yield return` cannot appear inside a `try` that has a `catch`, which is why the classified region - the only part of the loop that judges outcomes - contains no yields, and the compiler enforces the "post-start faults belong to the consumer" rule for free. And a lambda cannot be an iterator, which is why the public entry points take a source *factory* and the loop itself is the only iterator.

`Admit` composes inside this one path rather than forking a fifth, and that decision is a measurement rather than doctrine: the "one bit, zero bytes" argument that split the call paths is about fields every caller pays for. This path's floor is already an iterator box plus a surviving token source, so one more hoisted awaiter field is below its own noise. The same reasoning runs the other way for the hedge: two interleaved enumerables is a buffering problem, not a hedge, so the streaming overloads refuse a hedged policy at the call rather than pretend.

For more details on memory management, see [Where the allocations are](allocations.md).
