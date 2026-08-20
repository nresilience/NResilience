---
title: Deep dives
description: Why the library is built this way, and what was measured to find out.
order: 8
---

# Deep dives

Nothing here is needed to use the library. It is the reasoning and the measurements behind the
decisions the rest of the docs simply state.

| Page | The question |
| --- | --- |
| [One flat executor](one-executor.md) | Why is there no pipeline? |
| [Where the allocations are](allocations.md) | What does a call cost, and how is that enforced? |
| [The cancellation contract](cancellation.md) | Which token cancels what, and who decides what a cancellation meant? |
| [Breaker internals](breaker-internals.md) | Why consecutive failures, two probes, and a growing break? |
| [Retry budget internals](retry-budget-internals.md) | Why a fraction of traffic, and why per process? |
| [Guarded rejection](guarded-rejection.md) | Why does refusing a call take 100 milliseconds? |

