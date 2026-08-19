---
title: Retry budget internals
description: Why retries are bounded as a fraction of traffic, why the state is per process, and what the bucket holds.
order: 5
---

# Retry budget internals

## The arithmetic that makes attempt limits insufficient

Retries compose multiplicatively. A frontend that retries three times, calling a backend that retries
three times, calling a database that retries three times, turns one user action into up to 4³ = 64
database attempts. Every layer is configured reasonably. The aggregate is not, and no layer can see it.

A budget expressed as a fraction of traffic fixes that without coordination: if every client
independently funds retries at 10% of its successful traffic, fleet-wide amplification is bounded at
1.1 times whatever the topology looks like. That is the whole mechanism, and it is why the budget is on
by default while the attempt count is merely a bound.

## The bucket

A success deposits `fraction` tokens; a retry spends one. `minimumPerSecond` refills regardless of
traffic, so a client too quiet to fund retries from its own successes can still retry at all. Capacity
is ten seconds of the floor rate, which bounds the *burst* a recovering client can spend at once
without touching the sustained rate.

A cold process starts full. Throttling the first retries a fresh instance makes would penalize
deployment rather than a storm.

Only retries are charged. The first attempt of every call always runs - a budget is not a rate limiter,
and refusing traffic rather than refusing amplification is a different mechanism with a different
failure mode.

## Why the state is per process, and unshared by default

There is no coordination between pods, and that is not a defect: the argument is statistical, and
needing no protocol is the feature. It does follow that a budget which cannot observe enough traffic to
mean anything is worthless - so a budget allocated per `HttpClient` instance, or resolved from a scoped
container, is decoration. Share one instance, or use `RetryBudget.Shared(name)`.

Sharing is opt-in for the same reason breakers are scoped rather than global: a single process-wide
budget would let a storm against payments throttle retries to search, which is the blast-radius
inversion a resilience library exists to prevent.

## Two implementation details that are load-bearing

**The automatic budget cannot live in a field on the policy.** A record's synthesized equality compares
every instance field, so a lazily-created budget would make two identically-configured policies stop
being equal as a side effect of one of them having executed. It lives in a `ConditionalWeakTable` keyed
by policy identity instead.

**A DI registration pins the automatic budget to the registration name.** `Budget = null` keys the
automatic budget by policy *instance*, and a configuration reload produces a new instance - so the
accumulated traffic history would be discarded on every configuration edit, silently, on the default
configuration that nearly everybody runs. Pinning it to the name changes its lifetime and nothing about
its behavior.

## Reading it

`Utilisation` is the number for a dashboard, and a budget sitting near 1 is a client whose retries are
being refused: a symptom to alert on rather than a steady state. The metric that says the same thing
across a fleet is `nresilience.attempts ÷ nresilience.calls` - the retry fraction. See
[telemetry](../features/telemetry.md).

