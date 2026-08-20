---
title: Features
description: One page per knob - what it is, whether it is on by default, and how to read what it produces.
order: 3
---

# Features

Each feature is one knob: what it is, whether it is on by default, and how to read what it produces.

| Feature | On by default? | Page |
| --- | --- | --- |
| Retry, backoff and jitter | Yes - three attempts, exponential, full jitter | [Retry](retry.md) |
| Deadline and attempt timeout | Yes - 30 s and 10 s | [Deadlines and attempt timeouts](deadlines.md) |
| Classification | Yes - `Classifier.Default`, or `Classifier.Http` on the HTTP preset | [Classification](classification.md) |
| Retry budget | Yes - 10% of successful traffic, private per policy | [Retry budget](retry-budget.md) |
| Circuit breaker | No - it is an object you construct and scope | [Circuit breaker](circuit-breaker.md) |
| Telemetry | No for a hand-built policy, yes for a registered one | [Telemetry](telemetry.md) |

Two things are not features here. **Fallback** is an `if` on a
[`CallResult<T>`](../reference/call-result.md). **Hedging** is not implemented; see the
[FAQ](../faq.md).

