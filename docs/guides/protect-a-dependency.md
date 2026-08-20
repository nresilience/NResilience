---
title: Protect a dependency
description: A breaker and a budget scoped to one dependency, and a health endpoint that reads them.
order: 2
---

# Protect a dependency

## Scenario

Payments is flaky. When it degrades you want to stop hammering it, you do not want that to affect
calls to anything else, and you want an operator to be able to see the state and act on it.

## Complete example

<!-- snippet: guide-protect-a-dependency -->
```csharp
public sealed class Dependencies
{
    // One breaker per dependency, held where its lifetime is obvious. A storm against payments
    // must not trip calls to search, and here that is a property of the code.
    public Breaker Payments { get; } = new(new BreakerSettings
    {
        ConsecutiveFailures = 5,
        SlowCallThreshold = TimeSpan.FromSeconds(2),
        BreakDuration = TimeSpan.FromSeconds(15),
    })
    {
        Name = "payments",
    };

    public RetryBudget PaymentsBudget { get; } = RetryBudget.Shared("payments");

    public Resilience Charge => Resilience.Http with
    {
        Name = "payments",
        Breaker = Payments,
        Budget = PaymentsBudget,
        Deadline = TimeSpan.FromSeconds(8),
    };
}
```
<!-- endsnippet -->

## What's happening

- **The [breaker](../features/circuit-breaker.md) is a field**, so its scope is visible at the point
  of construction. A breaker is a switch that stops calling a dependency when it is failing. Every
  policy derived from `Charge` shares it, and nothing else does.
- **`SlowCallThreshold`** makes it trip on brownouts (slow responses) as well as errors. An
  error-rate breaker that only counts failures can stay closed while the dependency is returning
  errors slowly - it looks healthy by error count, but it is already struggling.
- **`BreakDuration` doubles** on each consecutive open, up to `MaxBreakDuration`, so a long outage
  does not mean a breaker reopening on a fixed schedule forever, hammering the dependency each time
  it closes.
- **The [retry budget](../features/retry-budget.md) is shared by name**, so charges and refunds
  throttle against one pool - and search, which does not name it, is unaffected. A retry budget caps
  retries as a fraction of traffic so a failing dependency is not overwhelmed by too many clients
  retrying at once.
- **`Name`** appears in every event and every metric tag, which is how a dashboard tells this
  dependency from the others.

## Read it from a health endpoint

<!-- snippet: guide-health-endpoint -->
```csharp
// A breaker is an object with a name and a state, so an operator can be told about it.
string report = dependencies.Payments.State switch
{
    BreakerState.Closed => "healthy",
    BreakerState.HalfOpen => "recovering",
    BreakerState.Isolated => "isolated by an operator",
    _ => $"open since {dependencies.Payments.OpenedAt:O}",
};
```
<!-- endsnippet -->

Reading `State` never consumes the probe slot a real call needs, so a liveness check cannot starve the
breaker's own recovery. There is no `IHealthCheck` wrapper in the box: `State` and `OpenedAt` are what
a health endpoint needs, and wrapping them would bind the package to another dependency to save a
five-line class.

## Handle the outcome

A refused call fails with `CallRejectedException`, and `Reason` tells you which guard refused it:
`DependencyUnavailable` for the breaker, `BudgetExhausted` for the budget. Those two facts call for
opposite responses - "the dependency is down" against "we are retrying too hard" - so they are
distinguishable everywhere, including in the metrics.

A refusal is not instant. It serves a short pause first, because without that pause a caller in a
tight polling loop would busy-spin (repeatedly calling and being rejected without waiting), burning
CPU for nothing.

## When to go deeper

- [Breaker internals](../deep-dives/breaker-internals.md) - the state machine and why probes need two
  successes.
- [Guarded rejection](../deep-dives/guarded-rejection.md) - why refusing costs 100 ms.
- [Per-host scope](../http/per-host-scope.md) - for HTTP, where the handler does this per host already.

