---
title: Retry budget
description: Bound retries as a fraction of traffic to prevent retry storms and excessive load on dependencies.
order: 5
---

# Retry budget

A per-call attempt limit cannot prevent a retry storm, because each caller independently thinks its own retries are reasonable. If a frontend, a backend, and a database each allow three retries, one user action can produce 64 attempts at the bottom layer - enough to overwhelm a failing dependency and make the outage worse.

A **retry budget** prevents this by bounding retries as a fraction of total traffic rather than per call.

The budget is on by default. `Resilience.Default` and `Resilience.Http` use `RetryBudget.Automatic`, which creates a budget private to each policy instance with more than one attempt. Disable it with `Budget = null` or `RetryBudget.None`.

## Configure and share the budget

You can tune the parameters or turn the feature off entirely.

<!-- snippet: budget-off -->
```csharp
// Presets use `RetryBudget.Automatic` to provide a private budget by default.
// `RetryBudget.None` disables the budget, which is appropriate for dependencies
// that are not shared. `null` also disables the budget.
var unbudgeted = Resilience.Default with { Budget = RetryBudget.None };

// Or tune it, privately to whoever holds the instance.
var generous = Resilience.Default with { Budget = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10) };
```
<!-- endsnippet -->

| Parameter | Default | Description |
| :--- | :--- | :--- |
| `fraction` | 0.1 | The number of retries funded per successful attempt: 0.1 funds one retry per ten successes in a steady state. |
| `minimumPerSecond` | 3 | The absolute minimum retries allowed per second, so low-traffic clients can still retry. |

### Share a budget across policies

Sharing a budget is opt-in. Use one to bound the aggregate retries across multiple policies.

<!-- snippet: budget-shared -->
```csharp
// Retries compose multiplicatively: three layers each retrying three times is 27 attempts
// at the bottom. A budget bounds retries as a fraction of traffic - 10% here - so the
// aggregate is bounded whether or not anybody coordinates.
var budget = RetryBudget.Shared(name: "payments", fraction: 0.1, minimumPerSecond: 3);

var charge = Resilience.Http with { Budget = budget };
var refund = Resilience.Http with { Budget = budget };
```
<!-- endsnippet -->

Don't use one process-wide budget for unrelated services: a storm against one service would then throttle retries for another, which is exactly what a shared budget is meant to prevent.

### Implementation details

Budget state is per-process. There is no coordination between separate instances of your application. That decentralization still bounds the fleet's total retries without a coordination protocol.

Because state is local, don't allocate a budget per `HttpClient` instance or resolve it from a scoped DI container - the budget gets discarded before it sees enough traffic to work. Share a single instance or use `RetryBudget.Shared`.

## Handle rejected retries

The first attempt always runs; a budget throttles only retries. When a budget cannot fund a retry, the call stops with `StopReason.BudgetExhausted` after a short pause. `CallRejectedException.RetryAfter` indicates how long to wait before the floor rate accrues another token.

<!-- snippet: breaker-rejection -->
```csharp
// A refused call reports itself rather than the dependency's last exception, and it says
// which guard refused it. RetryAfter is there so a caller that schedules its own polling
// does not have to guess.
if (result.Exception is CallRejectedException rejection)
{
    Console.WriteLine(value: rejection.Reason); // DependencyUnavailable, or BudgetExhausted
    Console.WriteLine(value: rejection.RetryAfter); // when to come back, when there is an answer
}
```
<!-- endsnippet -->

## Monitor budget utilization

Watch a budget's utilization to spot retries being refused.

<!-- snippet: budget-utilization -->
```csharp
// For a dashboard: a budget sitting near 1 is a client whose retries are being refused,
// which is a symptom to alert on rather than a steady state.
var spent = budget.Utilization; // 0 to 1
```
<!-- endsnippet -->

For high-level alerting, watch the ratio of `nresilience.attempts` to `nresilience.calls` - see [telemetry](telemetry.md).

The implementation is described in [Retry budget internals](../deep-dives/retry-budget-internals.md).
