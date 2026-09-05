---
title: Protect a dependency
description: Implement a circuit breaker and retry budget scoped to a specific dependency, and expose their state via a health endpoint.
order: 2
---

# Protect a dependency

When a critical dependency - a payment gateway, say - goes flaky or unavailable, you need to stop hammering it. Keep sending requests to a failing dependency and you exhaust your own resources while making it harder for the dependency to recover.

A circuit breaker stops requests to a failing service, and a retry budget limits how many retries you send across the application.

## Implementation example

This example defines a circuit breaker and retry budget scoped to a single dependency.

<!-- snippet: guide-protect-a-dependency -->
```csharp
public sealed class Dependencies
{
    // One breaker per dependency, held where its lifetime is obvious. A storm against payments
    // must not trip calls to search, and here that is a property of the code.
    public Breaker Payments { get; } = new(settings: new BreakerSettings
    {
        ConsecutiveFailures = 5,
        SlowCalls = SlowCalls.Above(multiple: 3), // a brownout is 3x normal, whatever normal is
        BreakDuration = TimeSpan.FromSeconds(value: 15),
    })
    {
        Name = "payments",
    };

    public RetryBudget PaymentsBudget { get; } = RetryBudget.Shared(name: "payments");

    public Resilience Charge => Resilience.Http with
    {
        Name = "payments",
        Breaker = Payments,
        Budget = PaymentsBudget,
        Deadline = TimeSpan.FromSeconds(value: 8),
    };
}
```
<!-- endsnippet -->

### Key implementation details

- **Breaker scope**: The [circuit breaker](../features/circuit-breaker.md) is defined as a field. Every policy that uses the `Charge` property shares this breaker, so state is consistent across all calls to the payment service.
- **Slow call detection**: `SlowCalls` makes the breaker trip during brownouts (the service is slow but not failing) as well as outright failures. It measures what normal looks like for this dependency, so you supply a multiple rather than a millisecond figure. It is on by default; stating it here keeps the example self-contained. See [Trip on brownouts](../features/circuit-breaker.md#trip-on-brownouts-without-guessing-a-number).
- **Exponential backoff**: `BreakDuration` doubles on each consecutive open state (up to `MaximumBreakDuration`), so the breaker does not reopen on a fixed schedule and pile onto the dependency during a long outage.
- **Shared retry budget**: The [retry budget](../features/retry-budget.md) is shared by name. Multiple policies (charges and refunds, say) throttle against one pool, while unrelated services are unaffected.
- **Observability**: The `Name` property appears in every event and metric tag, so you can tell this dependency from others on your dashboard.

## Expose state via a health endpoint

Read the breaker's state to report dependency health.

<!-- snippet: guide-health-endpoint -->
```csharp
// A breaker is an object with a name and a state, so an operator can be told about it.
var report = dependencies.Payments.State switch
{
    BreakerState.Closed => "healthy",
    BreakerState.HalfOpen => "recovering",
    BreakerState.Isolated => "isolated by an operator",
    _ => $"open since {dependencies.Payments.OpenedAt:O}",
};
```
<!-- endsnippet -->

Reading `State` does not consume a probe slot, so health checks cannot interfere with the breaker's recovery.

## Handle call rejections

When a breaker or budget refuses a call, NResilience throws `CallRejectedException`. The `Reason` property says why:

- `DependencyUnavailable`: The circuit breaker is open.
- `BudgetExhausted`: The retry budget is spent.

The distinction lets you respond differently - notify an operator that a service is down, versus slow down your own retry rate.

To stop a caller in a tight loop from burning CPU by calling and being rejected over and over, NResilience pauses briefly before returning a refusal.

## For more information

- [Breaker internals](../deep-dives/breaker-internals.md): The state machine, and why probes require two successes.
- [Guarded rejection](../deep-dives/guarded-rejection.md): Why refusing a call introduces a short delay.
- [Per-host scope](../http/per-host-scope.md): How the HTTP handler manages this automatically per host.
