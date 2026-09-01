---
title: Retry budget internals
description: A deep dive into the token-bucket mechanism, the reasoning for traffic-based bounding, and implementation details of the retry budget.
order: 5
---

# Retry budget internals

Retry budgets prevent request amplification in distributed systems. This page explains why bounding retries as a fraction of traffic beats fixed attempt limits.

## The problem with attempt limits

Retries compose multiplicatively across service boundaries. If a frontend retries three times, a backend retries three times, and a database retries three times, one user action becomes up to $4^3 = 64$ database attempts.

Each layer's configuration looks reasonable in isolation; the aggregate is an amplification storm. No single layer can see the whole call chain, so fixed attempt limits cannot prevent this.

A budget expressed as a fraction of traffic solves this without coordination. If every client independently funds retries at 10% of its successful traffic, fleet-wide amplification is bounded at 1.1 times the original traffic volume, whatever the topology. That is why the retry budget is on by default and the attempt count is only a maximum.

## The token-bucket mechanism

The retry budget uses a token-bucket algorithm to track and fund retries:

- **Deposits**: Every successful attempt deposits tokens based on the `fraction` value.
- **Spending**: Every retry spends one token.
- **Floor rate**: `minimumPerSecond` refills the bucket at a constant rate, so a quiet client can still retry without successful traffic.
- **Burst bound**: The bucket capacity is ten seconds of the floor rate, limiting the burst a recovering client can spend without changing the sustained rate.
- **Cold start**: A new process starts with a full bucket, so a fresh deployment is not penalized on its first retries.

**Note**: Only retries are charged; the first attempt of every call always executes. The retry budget is not a rate limiter - it refuses amplification, not traffic.

## State and scope

### Per-process state
Retry budgets are per process. The mechanism is statistical and needs no coordination between pods.

A budget that sees too little traffic is useless, though: one allocated per `HttpClient` instance or resolved from a scoped container provides little value. Share one instance or use `RetryBudget.Shared(name)`.

### Opt-in sharing
Sharing is opt-in to prevent blast-radius inversion: a single process-wide budget would let a failure storm against one dependency (a payment gateway) throttle retries for a different, healthy one (a search index).

## Implementation details

### Policy equality and lifetime
`RetryBudget.Automatic` is a marker. The bucket it resolves to is stored in a `ConditionalWeakTable` keyed by policy identity to ensure that lazy initialization does not affect record equality.

### DI registration and persistence
In dependency injection, the marker resolves to a budget pinned to the registration name. This preserves traffic history when a configuration reload creates a new policy instance.

## Monitoring the budget

The `Utilization` property feeds dashboards. A budget consistently near 1 indicates that retries are being refused - a symptom that should trigger an alert.

To monitor the retry fraction across a fleet, use:

`nresilience.attempts ÷ nresilience.calls`

For more information on available metrics, see [Telemetry](../features/telemetry.md).
