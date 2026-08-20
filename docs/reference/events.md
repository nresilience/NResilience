---
title: CallEvent
description: The one event type, its fields, and which kinds set which of them.
order: 7
---

# `CallEvent`

`readonly struct CallEvent`, passed by value to `Resilience.OnEvent`. Raising one allocates nothing;
`Result` boxes a value-type result, and is populated only when a listener is attached.

| Member | Meaning |
| --- | --- |
| `Kind` | What happened. |
| `PolicyName` | `Resilience.Name`, so one listener can serve many policies. |
| `AttemptNumber` | 1-based. The attempt that just finished, or - on `Retrying` and the pre-attempt kinds - the one about to run. |
| `Verdict` | How the most recent attempt was classified. `Ok` when nothing has run yet. |
| `Duration` | The attempt's duration on `Attempt`; how long the call has been running on every other kind. |
| `Delay` | The pause about to be served: the backoff on `Retrying`, the [rejection pause](../deep-dives/guarded-rejection.md) on `Rejected`. Null elsewhere. |
| `Exception` | What the most recent attempt threw, or null. |
| `Result` | What the most recent attempt returned, as `object`, or null when it threw or returned nothing. |
| `Reason` | Why the call stopped, on the terminal kinds. Null elsewhere. |
| `ToString()` | `[PolicyName] Kind #N VerdictKind ExceptionType (duration) +delay`, with each optional segment omitted when it is absent. |

## `CallEventKind`

| Kind | Terminal | `Delay` | `Reason` |
| --- | --- | --- | --- |
| `Attempt` | | | |
| `Retrying` | | The backoff | |
| `Succeeded` | Yes | | `Succeeded` |
| `NotRetried` | Yes | | `Permanent` |
| `Exhausted` | Yes | | `AttemptsExhausted` |
| `Rejected` | Yes | The [rejection pause](../deep-dives/guarded-rejection.md) | `DependencyUnavailable` or `BudgetExhausted` |
| `DeadlineExceeded` | Yes | | `DeadlineExceeded` |
| `OrphanedWork` | | | |
| `BreakerOpened` | | | |
| `BreakerClosed` | | | |
| `BreakerHalfOpened` | | | |
| `NestedRetry` | | | |

**Every call ends with exactly one terminal event**, and never two. A count of logical operations is
only trustworthy if that holds, so it is an invariant with a test rather than a tendency.

Exactly one `Attempt` event fires per attempt. `Retrying` fires **before** the backoff is served, so a
listener can report how long the call is about to sit idle.

`OrphanedWork` fires when an attempt overran its ceiling by more than a second, retrospectively - the
moment the work finally returns. `NestedRetry` is raised by the HTTP handler; nothing else can detect
it.

Breaker transitions are raised outside the breaker's lock, on the call that caused them.

## Listener contract

Synchronous, on the [executor's](index.md) thread. A listener that blocks blocks the call. An exception a listener
throws is swallowed. Two listeners is `OnEvent = first + second`.

