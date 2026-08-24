---
title: Protect a dependency
description: Implement a circuit breaker and retry budget scoped to a specific dependency, and expose their state via a health endpoint.
order: 2
---

# Protect a dependency

When a critical dependency - such as a payment gateway - becomes flaky or unavailable, you must prevent your application from "hammering" the service. If you continue to send requests to a failing dependency, you risk exhausting your own resources and preventing the dependency from recovering.

To solve this, you can use a circuit breaker to stop requests to a failing service and a retry budget to limit the total number of retries across your application.

## Implementation example

The following example demonstrates how to define a circuit breaker and retry budget scoped to a single dependency.

<!-- snippet: guide-protect-a-dependency -->
```csharp
public sealed class Dependencies
{
    // One breaker per dependency, held where its lifetime is obvious. A storm against payments
    // must not trip calls to search, and here that is a property of the code.
    public Breaker Payments { get; } = new(settings: new BreakerSettings
    {
        ConsecutiveFailures = 5,
        SlowCallThreshold = TimeSpan.FromSeconds(value: 2),
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

- **Breaker scope**: The [circuit breaker](../features/circuit-breaker.md) is defined as a field. Every policy that uses the `Charge` property shares this breaker, ensuring consistent state across all calls to the payment service.
- **Slow call detection**: The `SlowCallThreshold` ensures the breaker trips during "brownouts" (when the service is slow) as well as during outright failures. This prevents the application from hanging on slow responses.
- **Exponential backoff**: The `BreakDuration` doubles on each consecutive open state (up to `MaxBreakDuration`). This prevents the breaker from reopening on a fixed schedule and overwhelming the dependency during a long outage.
- **Shared retry budget**: The [retry budget](../features/retry-budget.md) is shared by name. Multiple policies (such as charges and refunds) throttle against a single pool, while unrelated services remain unaffected.
- **Observability**: The `Name` property is included in every event and metric tag, allowing you to distinguish this dependency from others in your monitoring dashboard.

## Expose state via a health endpoint

You can monitor the health of your dependencies by reading the state of the circuit breaker.

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

Reading the `State` property does not consume a probe slot, so health checks cannot interfere with the breaker's recovery process.

## Handle call rejections

When a breaker or budget refuses a call, NResilience throws a `CallRejectedException`. You can use the `Reason` property to determine why the call was refused:

- `DependencyUnavailable`: The circuit breaker is open.
- `BudgetExhausted`: The retry budget has been reached.

Distinguishing between these reasons allows you to implement different responses - for example, notifying an operator that a service is down versus slowing down your own retry rate.

To prevent "busy-spinning" (where a caller in a tight loop burns CPU by repeatedly calling and being rejected), NResilience introduces a short pause before returning a refusal.

## For more information

- [Breaker internals](../deep-dives/breaker-internals.md): Learn about the state machine and why probes require two successes.
- [Guarded rejection](../deep-dives/guarded-rejection.md): Understand why refusing a call introduces a short delay.
- [Per-host scope](../http/per-host-scope.md): Learn how the HTTP handler manages this logic automatically per host.
