---
title: Features
description: Overview of the resilience features provided by NResilience.
order: 3
---

# Features

NResilience provides several features to improve the resilience of your applications. The following table summarizes these features and their default settings.

| Feature | Default setting | Documentation |
| :--- | :--- | :--- |
| Retry, backoff, and jitter | Three attempts, exponential backoff, and full jitter | [Retry](retry.md) |
| Deadlines and attempt timeouts | 30 s deadline and 10 s attempt timeout | [Deadlines](deadlines.md) |
| Classification | `Classifier.Default`, or `Classifier.Http` for the HTTP preset | [Classification](classification.md) |
| Retry budget | 10% of successful traffic, private per policy | [Retry budget](retry-budget.md) |
| Circuit breaker | Disabled (requires manual construction and scoping) | [Circuit breaker](circuit-breaker.md) |
| Rate limiting | Disabled (opt-in, and queueing off when enabled) | [Rate limiting](rate-limiting.md) |
| Hedging | Disabled (opt-in, and never against a fixed delay) | [Hedging](hedging.md) |
| Deadline propagation | Disabled (opt-in on both halves) | [Deadlines](deadlines.md#propagate-the-deadline-across-a-hop) |
| Telemetry | Enabled for registered policies; disabled for hand-built policies | [Telemetry](telemetry.md) |
| Logging | Enabled for registered policies; opt-in for hand-built policies | [Logging](logging.md) |

Fallback is handled as a conditional check on a [`CallResult<T>`](../reference/call-result.md).
