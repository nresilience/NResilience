---
title: RetryBudget
description: Reference for the RetryBudget class and how it limits retries as a fraction of total traffic.
order: 6
---

# `RetryBudget`

The `RetryBudget` is a `sealed class` that bounds the number of retries based on a fraction of the total traffic. Like a circuit breaker, it is a live object that tracks state over time.

| Member | Description |
| :--- | :--- |
| `RetryBudget.Of(fraction = 0.1, minimumPerSecond = 3, time = null)` | Creates a budget private to the instance that holds it. |
| `RetryBudget.Shared(name, fraction = 0.1, minimumPerSecond = 3)` | Creates or retrieves a process-wide budget looked up by name. Policies that share the same name share the same budget. The parameters provided by the first caller are used. |
| `RetryBudget.None` | Disables the budget. Every retry allowed by other policy bounds is funded. |
| `Name` | The name used to look up a shared budget, if applicable. |
| `Utilisation` | A value from 0 to 1 indicating how much of the current budget has been spent. |

Both factories throw a `ResilienceConfigurationException` if the `fraction` is outside the range (0, 1] or if `minimumPerSecond` is negative. To disable the budget, use `RetryBudget.None` rather than a fraction of zero.

## Behavior

The retry budget uses a token-bucket mechanism to control retries:

- **Deposits**: Every successful attempt deposits tokens into the bucket based on the `fraction` value. For example, a `fraction` of `0.1` funds one retry for every ten successes.
- **Spending**: Every retry attempt spends one token.
- **Floor Rate**: The `minimumPerSecond` parameter refills the bucket at a constant rate regardless of traffic. This ensures that a quiet client can still perform retries. A value of zero means only successful traffic funds retries.
- **Burst Bound**: The bucket capacity is limited to ten seconds of the floor rate. This bounds the burst of retries a recovering client can spend at once.
- **Cold Start**: A new process starts with a full bucket. This prevents deployment from being penalized by throttling the first few retries of a fresh instance.
- **Charging**: Only retries are charged. The first attempt of every call always executes.

### Failure handling
When the budget is exhausted, the handler stops the call with `StopReason.BudgetExhausted` after a short pause. The `CallRejectedException.RetryAfter` property indicates how long the caller must wait before the floor rate accrues another token.

## Configuration

If `Resilience.Budget` is set to `null`, the library creates an automatic budget with default settings that is private to that policy instance. 

When using dependency injection, the budget is pinned to the registration name. This ensures that a configuration reload does not discard the traffic history.

For a detailed explanation of the mechanism, see [Retry budget internals](../deep-dives/retry-budget-internals.md).
