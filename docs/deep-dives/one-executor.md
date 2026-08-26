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

When measured over a real loopback socket - which more accurately reflects real-world I/O and cancellation token registration - the fused design is **2.41x** cheaper. The build process enforces a minimum ratio of 2.0x to ensure this performance advantage is maintained. The socket figure is the more honest headline of the two, because real I/O registers on the cancellable attempt token and `Task.Yield` does not.

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
- **Outcome Classification**: Use the [`Classifier`](../reference/classifier.md) to determine how outcomes are treated.
- **Custom Delays**: Use `Backoff.Custom` to compute your own backoff logic.
- **Pre-attempt Logic**: Use `BeforeAttempt` to run code before each attempt.
- **Observability**: Use `OnEvent` to monitor every stage of the process.

This restricted surface is a deliberate choice. A smaller public API is more likely to remain stable over time, reducing the need for breaking changes and making the library more reliable for long-term adoption.

For more details on memory management, see [Where the allocations are](allocations.md).
