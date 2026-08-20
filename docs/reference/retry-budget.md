---
title: RetryBudget
description: The budget that bounds retries as a fraction of traffic.
order: 6
---

# `RetryBudget`

`sealed class RetryBudget`. A live object, like a breaker.

| Member | Meaning |
| --- | --- |
| `RetryBudget.Of(fraction = 0.1, minimumPerSecond = 3, time = null)` | A budget private to whoever holds the instance. |
| `RetryBudget.Shared(name, fraction = 0.1, minimumPerSecond = 3)` | A process-wide budget looked up by name. Two policies naming the same string share it, and the first caller's parameters win. |
| `RetryBudget.None` | No budget at all. Every retry the policy's other bounds allow is funded. |
| `Name` | The name a shared budget was looked up by, if any. |
| `Utilisation` | How much of the bucket is spent, from 0 to 1. |

Both factories throw `ResilienceConfigurationException` when `fraction` is outside (0, 1] or
`minimumPerSecond` is negative. Use `None` to disable rather than a fraction of zero.

## Behavior

- A successful attempt **deposits** `fraction` tokens. A retry **spends** one. So in steady state,
  `fraction = 0.1` funds one retry per ten successes.
- `minimumPerSecond` refills the bucket regardless of traffic, so a quiet client can still retry. Zero
  means only traffic funds retries.
- The bucket holds ten seconds of the floor rate, which bounds the burst a recovering client can spend
  at once. The sustained rate is set by the floor and by deposits.
- A cold process starts **full**. Throttling the first retries a fresh instance makes would penalize
  deployment rather than a storm.
- Only retries are charged. The first attempt of every call always runs.
- A refused retry stops the call with `StopReason.BudgetExhausted` after a short pause, and
  `CallRejectedException.RetryAfter` carries how long until the floor rate accrues a whole token.

`Resilience.Budget = null` means an automatic budget with these defaults, private to that policy
instance. A DI registration pins it to the registration name instead, so a configuration reload does
not discard the traffic history.

Go deeper: [Retry budget internals](../deep-dives/retry-budget-internals.md).

