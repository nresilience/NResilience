---
title: Retry budget internals
description: A deep dive into the token-bucket mechanism, the reasoning for traffic-based bounding, and implementation details of the retry budget.
order: 5
---

# Retry budget internals

Retry budgets prevent request amplification in distributed systems. This guide explains why bounding retries as a fraction of traffic is more effective than using fixed attempt limits.

## The problem with attempt limits

Retries compose multiplicatively across service boundaries. For example, if a frontend service retries a call three times, a backend service retries three times, and a database retries three times, a single user action can result in up to $4^3 = 64$ database attempts. 

While each layer's configuration may seem reasonable in isolation, the aggregate effect is an amplification storm. Because no single layer can see the entire call chain, fixed attempt limits cannot prevent this.

A budget expressed as a fraction of traffic solves this without requiring coordination. If every client independently funds retries at 10% of its successful traffic, fleet-wide amplification is bounded at 1.1 times the original traffic volume, regardless of the system topology. This is why the retry budget is enabled by default, while the attempt count serves only as a maximum bound.

## The token-bucket mechanism

The retry budget uses a token-bucket algorithm to track and fund retries:

- **Deposits**: Every successful attempt deposits tokens into the bucket based on the `fraction` value.
- **Spending**: Every retry attempt spends one token.
- **Floor Rate**: The `minimumPerSecond` parameter refills the bucket at a constant rate. This ensures that a quiet client can still perform retries even without successful traffic.
- **Burst Bound**: The bucket capacity is limited to ten seconds of the floor rate. This limits the burst of retries a recovering client can spend at once without impacting the sustained rate.
- **Cold Start**: A new process starts with a full bucket. This prevents new deployments from being penalized by throttling the first few retries of a fresh instance.

**Note**: Only retries are charged. The first attempt of every call always executes. The retry budget is not a rate limiter; its purpose is to refuse amplification, not to refuse traffic.

## State and scope

### Per-process state
Retry budgets are maintained per process. Because the mechanism is statistical, it does not require coordination between pods. 

However, a budget that does not observe enough traffic is ineffective. A budget allocated per `HttpClient` instance or resolved from a scoped container provides little value. To ensure the budget is effective, you should share one instance or use `RetryBudget.Shared(name)`.

### Opt-in sharing
Sharing is opt-in to prevent "blast-radius inversion." A single process-wide budget would allow a failure storm against one dependency (e.g., a payment gateway) to throttle retries for an entirely different, healthy dependency (e.g., a search index).

## Implementation details

### Policy equality and lifetime
The automatic budget cannot be stored as a field within the `Resilience` record. Since records use synthesized equality to compare instance fields, a lazily created budget would cause two identically configured policies to be unequal simply because one had been executed. Instead, the budget is stored in a `ConditionalWeakTable` keyed by policy identity.

### DI registration and persistence
When using dependency injection, the automatic budget is pinned to the registration name rather than the policy instance. This ensures that when a configuration reload produces a new policy instance, the accumulated traffic history is not discarded.

## Monitoring the budget

The `Utilisation` property provides a value for dashboards. A budget consistently near 1 indicates that retries are being refused, which is a symptom that should trigger an alert.

To monitor the retry fraction across a fleet, use the following metric:
`nresilience.attempts ÷ nresilience.calls`

For more information on available metrics, see [Telemetry](../features/telemetry.md).
