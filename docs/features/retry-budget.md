---
title: Retry budget
description: Bound retries as a fraction of traffic to prevent retry storms and excessive load on dependencies.
order: 5
---

# Retry budget

A per-call attempt limit cannot prevent a retry storm because each caller independently believes its retry behavior is reasonable. For example, if a frontend, a backend, and a database each permit three retries, a single user action can result in 64 attempts at the bottom layer. This can overwhelm a failing dependency and worsen an outage.

A **retry budget** prevents this by bounding retries as a fraction of total traffic rather than per call.

Retry budgets are enabled by default. If a policy has more than one attempt and `Budget` is `null`, NResilience automatically creates a budget private to that policy instance.

## Configure and share the budget

You can tune the budget parameters or opt out of the feature entirely.

<!-- snippet: budget-off -->
```csharp
// Null - the default - is an automatic budget private to this policy instance, so storm
// protection needs no configuration. None is the opt-out, and the only correct
// use is a dependency you know is not shared.
var unbudgeted = Resilience.Default with { Budget = RetryBudget.None };

// Or tune it, privately to whoever holds the instance.
var generous = Resilience.Default with { Budget = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10) };
```
<!-- endsnippet -->

| Parameter | Default | Description |
| :--- | :--- | :--- |
| `fraction` | 0.1 | The number of retries funded per successful attempt. For example, 0.1 funds one retry per ten successes in a steady state. |
| `minimumPerSecond` | 3 | The absolute minimum number of retries allowed per second, ensuring low-traffic clients can still retry. |

### Share a budget across policies

Sharing a budget is opt-in. Use a shared budget to bound the aggregate number of retries across multiple policies.

<!-- snippet: budget-shared -->
```csharp
// Retries compose multiplicatively: three layers each retrying three times is 27 attempts
// at the bottom. A budget bounds retries as a fraction of traffic - 10% here - so the
// aggregate is bounded whether or not anybody coordinates.
var budget = RetryBudget.Shared("payments", fraction: 0.1, minimumPerSecond: 3);

var charge = Resilience.Http with { Budget = budget };
var refund = Resilience.Http with { Budget = budget };
```
<!-- endsnippet -->

Avoid using a single process-wide budget for unrelated services. A storm against one service could throttle retries for another unrelated service, which is the exact failure a shared budget is intended to prevent.

### Implementation details

Budget state is maintained per-process. There is no coordination between separate instances of your application. This decentralized approach bounds the total number of retries across the entire fleet without requiring a coordination protocol.

Because budget state is local, do not allocate a budget per `HttpClient` instance or resolve it from a scoped DI container. Doing so causes the budget to be discarded before it can observe enough traffic to be effective. Instead, share a single instance or use `RetryBudget.Shared`.

## Handle rejected retries

The first attempt always runs; a budget throttles only the retries. When a budget cannot fund a retry, the call stops with `StopReason.BudgetExhausted` after a short pause. The `CallRejectedException.RetryAfter` property indicates how long to wait before the floor rate accrues another token.

<!-- snippet: breaker-rejection -->
```csharp
// A refused call reports itself rather than the dependency's last exception, and it says
// which guard refused it. RetryAfter is there so a caller that schedules its own polling
// does not have to guess.
if (result.Exception is CallRejectedException rejection)
{
    Console.WriteLine(rejection.Reason);      // DependencyUnavailable, or BudgetExhausted
    Console.WriteLine(rejection.RetryAfter);  // when to come back, when there is an answer
}
```
<!-- endsnippet -->

## Monitor budget utilization

You can monitor the utilization of a budget to identify when retries are being refused.

<!-- snippet: budget-utilisation -->
```csharp
// For a dashboard: a budget sitting near 1 is a client whose retries are being refused,
// which is a symptom to alert on rather than a steady state.
double spent = budget.Utilisation;   // 0 to 1
```
<!-- endsnippet -->

For high-level alerting, monitor the ratio of `nresilience.attempts` to `nresilience.calls`. For more information, see [telemetry](telemetry.md).

For a deeper dive into the implementation, see [Retry budget internals](../deep-dives/retry-budget-internals.md).
