---
title: RetryBudget
description: Reference for the RetryBudget class and how it limits retries as a fraction of total traffic.
order: 6
---

# `RetryBudget`

The `RetryBudget` is a `sealed class` that bounds retries as a fraction of total traffic. Like a circuit breaker, it is a live object that tracks state over time.

| Member | Description |
| :--- | :--- |
| `RetryBudget.Of(fraction = 0.1, minimumPerSecond = 3, time = null)` | Creates a budget private to the instance that holds it. |
| `RetryBudget.Shared(name, fraction = 0.1, minimumPerSecond = 3)` | Creates or retrieves a process-wide budget looked up by name. Policies that share the same name share the same budget. The parameters provided by the first caller are used. |
| `RetryBudget.None` | Disables the budget. Every retry allowed by other policy bounds is funded. |
| `RetryBudget.Automatic` | The default. A marker that resolves to a budget private to the policy instance, or to each key when the policy is scoped. |
| `Name` | The name used to look up a shared budget, if applicable. |
| `Utilization` | A value from 0 to 1 indicating how much of the current budget has been spent. Reads 0 on `RetryBudget.None` and on `RetryBudget.Automatic` itself, neither of which holds tokens. The bucket `Automatic` resolves to is reported by `HttpResilienceHandler.BudgetsByHost()`, `ResilienceInterceptor.Budgets()` and `PolicyScope<TKey>.Budgets()`. |

Both factories throw `ResilienceConfigurationException` when `fraction` is outside (0, 1] or `minimumPerSecond` is negative. Disable the budget with `RetryBudget.None`, not a fraction of zero.

## Behavior

The retry budget uses a token-bucket mechanism:

- **Deposits**: Every successful attempt deposits tokens based on the `fraction` value. A `fraction` of `0.1` funds one retry for every ten successes.
- **Spending**: Every retry spends one token.
- **Floor rate**: `minimumPerSecond` refills the bucket at a constant rate regardless of traffic, so a quiet client can still retry. Zero means only successful traffic funds retries.
- **Burst bound**: The bucket capacity is ten seconds of the floor rate, bounding the burst a recovering client can spend at once.
- **Cold start**: A new process starts with a full bucket, so a fresh instance is not penalized on its first retries.
- **Charging**: Only retries are charged; the first attempt of every call always executes.

### Failure handling
When the budget is exhausted, the call stops with `StopReason.BudgetExhausted` after a short pause. `CallRejectedException.RetryAfter` indicates how long to wait before the floor rate accrues another token.

## Configuration

`Resilience.Default` and `Resilience.Http` use `RetryBudget.Automatic`; each policy instance resolves that marker to its own private budget on first execution. Set `Budget` to `null` or `RetryBudget.None` to disable the budget.

With DI, the budget is pinned to the registration name, so a configuration reload does not discard the traffic history.

For a detailed explanation of the mechanism, see [Retry budget internals](../deep-dives/retry-budget-internals.md).
