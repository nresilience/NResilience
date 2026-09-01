---
title: Deep dives
description: Explore the design decisions, measurements, and technical reasoning behind NResilience.
order: 9
---

# Deep dives

These guides provide the technical reasoning and measurements behind the design of NResilience. While you do not need to read these to use the library, they offer insight into why the system is built this way.

| Topic | Key question answered |
| :--- | :--- |
| [One flat executor](one-executor.md) | Why does the library use a flat execution loop instead of a strategy pipeline? |
| [Where the allocations are](allocations.md) | What is the cost of a call, and how is that cost minimized? |
| [The cancellation contract](cancellation.md) | How is cancellation handled, and which token governs each part of the call? |
| [Breaker internals](breaker-internals.md) | Why use consecutive failures, a specific probe count, and growing break durations? |
| [Retry budget internals](retry-budget-internals.md) | Why limit retries as a fraction of total traffic, and why maintain the budget per process? |
| [Guarded rejection](guarded-rejection.md) | Why does the handler introduce a delay when refusing a call? |
| [Admission control](admission-control.md) | Why is a limiter refusal not a new verdict kind, and why is the retry budget exempt from it? |
| [Logging internals](logging-internals.md) | Why are log levels proportional to volume, and what do the records deliberately omit? |
| [Hedging internals](hedging-internals.md) | Why is hedging against a live quantile safe when hedging against a fixed delay is not? |
