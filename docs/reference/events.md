---
title: CallEvent
description: Reference for the CallEvent structure and the different types of events emitted by resilience policies.
order: 7
---

# `CallEvent`

`CallEvent` is a `readonly struct` passed to the `Resilience.OnEvent` listener. Raising an event is allocation-free, although the `Result` member boxes value-type results. The `Result` member is only populated if a listener is attached.

| Member | Description |
| :--- | :--- |
| `Kind` | The type of event that occurred. |
| `PolicyName` | The name of the policy (`Resilience.Name`). This allows a single listener to monitor multiple policies. |
| `AttemptNumber` | The 1-based index of the attempt. For `Attempt` events, it is the attempt that just finished. For `Retrying` and pre-attempt events, it is the attempt about to run. |
| `Verdict` | The classification of the most recent attempt. Defaults to `Ok` before any attempt has run. |
| `Duration` | For `Attempt` events, this is the duration of that attempt. For all other event kinds, this is the total time the call has been running. |
| `Delay` | The duration of the pause about to be served. This is the backoff delay for `Retrying` events or the [rejection pause](../deep-dives/guarded-rejection.md) for `Rejected` events. This is `null` for all other event kinds. |
| `Exception` | The exception thrown by the most recent attempt, or `null` if none was thrown. |
| `Result` | The value returned by the most recent attempt, as an `object`. This is `null` if the attempt threw an exception or returned nothing. |
| `Reason` | The `StopReason` indicating why the call stopped. This is populated only for terminal event kinds. |
| `ToString()` | Returns a formatted summary of the event, omitting absent segments. |

## `CallEventKind`

The `CallEventKind` enum defines the types of events emitted during a call.

| Kind | Terminal | `Delay` Populated | `Reason` Populated |
| :--- | :--- | :--- | :--- |
| `Attempt` | No | No | No |
| `Retrying` | No | Yes (Backoff) | No |
| `Succeeded` | Yes | No | Yes (`Succeeded`) |
| `NotRetried` | Yes | No | Yes (`Permanent`) |
| `Exhausted` | Yes | No | Yes (`AttemptsExhausted`) |
| `Rejected` | Yes | Yes (Rejection pause) | Yes (`DependencyUnavailable` or `BudgetExhausted`) |
| `DeadlineExceeded` | Yes | No | Yes (`DeadlineExceeded`) |
| `OrphanedWork` | No | No | No |
| `BreakerOpened` | No | No | No |
| `BreakerClosed` | No | No | No |
| `BreakerHalfOpened` | No | No | No |
| `NestedRetry` | No | No | No |

### Event invariants and behavior

- **Terminal Events**: Every call ends with exactly one terminal event. This invariant ensures that logical operations can be counted reliably.
- **Attempt Events**: Exactly one `Attempt` event fires per attempt.
- **Retrying Events**: `Retrying` events fire **before** the backoff delay is served, allowing listeners to report the expected idle time.
- **Orphaned Work**: `OrphanedWork` fires when an attempt exceeds its time ceiling by more than one second. This event is raised retrospectively the moment the work finally returns.
- **Nested Retries**: `NestedRetry` events are raised exclusively by the HTTP handler.
- **Breaker Transitions**: Breaker state transitions are raised on the call that triggered the transition, outside of the breaker's internal lock.

## Listener contract

The `OnEvent` listener is executed synchronously on the [executor's](index.md) thread. 

- **Blocking**: If a listener blocks, it blocks the entire call.
- **Exceptions**: Any exception thrown by a listener is swallowed by the library to prevent telemetry from crashing the application.
- **Multiple Listeners**: To use multiple listeners, chain them together: `OnEvent = first + second`.
