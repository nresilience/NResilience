---
title: CallEvent
description: Reference for the CallEvent structure and the different types of events emitted by resilience policies.
order: 7
---

# `CallEvent`

`CallEvent` is a `readonly struct` passed to the `Resilience.OnEvent` listener. Raising an event is allocation-free, though the `Result` member boxes value-type results. `Result` is populated only when a listener is attached.

| Member | Description |
| :--- | :--- |
| `Kind` | The type of event that occurred. |
| `PolicyName` | The name of the policy (`Resilience.Name`). This allows a single listener to monitor multiple policies. |
| `AttemptNumber` | The 1-based index of the attempt. For `Attempt` events, it is the attempt that just finished. For `Retrying` and pre-attempt events, it is the attempt about to run. |
| `Verdict` | The classification of the most recent attempt. Defaults to `Ok` before any attempt has run. |
| `Duration` | For `Attempt` events, this is the duration of that attempt. For all other event kinds, this is the total time the call has been running. |
| `Delay` | The duration of the pause about to be served. This is the backoff delay for `Retrying` events or the [rejection pause](../deep-dives/guarded-rejection.md) for the two rejection events. This is `null` for all other event kinds. |
| `Exception` | The exception thrown by the most recent attempt, or `null` if none was thrown. |
| `Result` | The value returned by the most recent attempt, as an `object`. This is `null` if the attempt threw an exception or returned nothing. |
| `Reason` | The `StopReason` indicating why the call stopped. This is populated only for terminal event kinds. |
| `IsRejection` | `true` for `RejectedByBreaker` and `RejectedByBudget`, for a listener that treats the two refusals alike. |
| `IsTerminal` | `true` for the kinds that end a call. Exactly one of these is raised per call. |
| `ToString()` | Returns a formatted summary of the event, omitting absent segments. |
| `Create(kind, ...)` | Static. Builds a `CallEvent` for [testing a listener](../testing/index.md#test-a-custom-listener) without the executor. Every parameter but `kind` is defaulted. |

## `CallEventKind`

The `CallEventKind` enum defines the event types raised during a call.

| Kind | Terminal | `Delay` Populated | `Reason` Populated |
| :--- | :--- | :--- | :--- |
| `Attempt` | No | No | No |
| `Retrying` | No | Yes (Backoff) | No |
| `Succeeded` | Yes | No | Yes (`Succeeded`) |
| `NotRetried` | Yes | No | Yes (`Permanent`) |
| `Exhausted` | Yes | No | Yes (`AttemptsExhausted`) |
| `RejectedByBreaker` | Yes | Yes (Rejection pause) | Yes (`DependencyUnavailable`) |
| `RejectedByBudget` | Yes | Yes (Rejection pause) | Yes (`BudgetExhausted`) |
| `DeadlineExceeded` | Yes | No | Yes (`DeadlineExceeded`) |
| `OrphanedWork` | No | No | No |
| `BreakerOpened` | No | No | No |
| `BreakerClosed` | No | No | No |
| `BreakerHalfOpened` | No | No | No |
| `NestedRetry` | No | No | No |
| `HedgeStarted` | No | Yes (the latency threshold) | No |
| `HedgeWon` | No | No | No |
| `HedgeDiscarded` | No | No | No |

### Event invariants and behavior

- **Terminal events**: Every call ends with exactly one terminal event, which is what makes logical operations countable. The `IsTerminal` property identifies them.
- **Rejections**: A refusal names the guard that made it: `RejectedByBreaker` indicates that the dependency is unavailable; `RejectedByBudget` indicates that the client is retrying too hard. `IsRejection` covers both.
- **Attempt events**: Exactly one `Attempt` event fires per attempt.
- **Retrying events**: `Retrying` fires **before** the backoff delay is served, so listeners can report the expected idle time.
- **Orphaned work**: `OrphanedWork` fires when an attempt exceeds its ceiling by more than one second, raised retrospectively the moment the work finally returns.
- **Nested retries**: `NestedRetry` events are raised only by the HTTP handler.
- **Hedging**: `HedgeStarted` carries the live latency quantile that triggered it on `Delay`. `HedgeDiscarded` fires when a leg is cancelled because a sibling answered first; its `Duration` is how long that leg ran. A discarded leg raises no `Attempt` event, because nothing classified it. See [Hedging](../features/hedging.md).
- **Breaker transitions**: Breaker state transitions are raised on the call that triggered the transition, outside the breaker's internal lock.

## Listener contract

The `OnEvent` listener runs synchronously on the [executor's](index.md) thread. 

- **Blocking**: A listener that blocks blocks the whole call.
- **Exceptions**: Any exception a listener throws is swallowed, so telemetry cannot crash the application.
- **Multiple listeners**: Chain them: `OnEvent = first + second`.

## Log event IDs

These are the `ILogger` records a registered policy writes. An event ID is a contract the moment an alert is built on it, so the numbers below are stable and gated by a test. See [Logging in DI](../di/logging.md) for how to filter them.

| ID | Name | `Default` | `Verbose` | Message |
| :--- | :--- | :--- | :--- | :--- |
| 1000 | `AttemptSucceeded` | `Trace` | `Information` | `{Policy} attempt {Attempt} succeeded in {ElapsedMs} ms` |
| 1001 | `AttemptFailed` | `Debug` | `Information` | `{Policy} attempt {Attempt} failed in {ElapsedMs} ms: {Verdict} {ErrorType}` |
| 1002 | `AttemptLimited` | `Debug` | `Information` | `{Policy} attempt {Attempt} was refused by a local limiter before it left the process` |
| 1003 | `Retrying` | `Debug` | `Information` | `{Policy} waiting {DelayMs} ms before attempt {Attempt} after a {Verdict} outcome` |
| 1004 | `CallSucceeded` | `Trace` | `Information` | `{Policy} succeeded in {ElapsedMs} ms` |
| 1005 | `CallSucceededAfterRetries` | `Debug` | `Information` | `{Policy} succeeded on attempt {Attempt} after {ElapsedMs} ms` |
| 1006 | `NotRetried` | `Debug` | `Information` | `{Policy} stopped after attempt {Attempt}: the outcome was classified Permanent` |
| 1007 | `NotRetriedFirstSighting` | `Warning` | `Warning` | `{Policy} did not retry {ErrorType} on attempt {Attempt} because the classifier called it Permanent.` |
| 1008 | `Exhausted` | `Debug` | `Information` | `{Policy} used all {Attempt} attempts in {ElapsedMs} ms and failed with {ErrorType}` |
| 1009 | `DeadlineExceeded` | `Debug` | `Information` | `{Policy} ran out of deadline after {ElapsedMs} ms on attempt {Attempt}` |
| 1010 | `RejectedDependencyUnavailable` | `Warning` | `Warning` | `{Policy} refused a call because its circuit breaker is open. Rejections logged quietly since the previous warning: {Suppressed}.` |
| 1011 | `RejectedBudgetExhausted` | `Warning` | `Warning` | `{Policy} refused a retry because the retry budget is exhausted.` |
| 1012 | `RejectedRepeat` | `Debug` | `Information` | `{Policy} refused a call: {Reason}` |
| 1013 | `BreakerOpened` | `Warning` | `Warning` | `{Policy} opened its circuit breaker on attempt {Attempt}.` |
| 1014 | `BreakerHalfOpened` | `Information` | `Information` | `{Policy} is probing its dependency: the break duration elapsed and this call is the probe` |
| 1015 | `BreakerClosed` | `Information` | `Information` | `{Policy} closed its circuit breaker and is taking traffic again` |
| 1016 | `OrphanedWork` | `Warning` | `Warning` | `{Policy} attempt {Attempt} kept running after its timeout, so that work is still going unobserved.` |
| 1017 | `OrphanedWorkRepeat` | `Debug` | `Information` | `{Policy} attempt {Attempt} kept running after its timeout` |
| 1018 | `NestedRetry` | `Warning` | `Warning` | `{Policy} is retrying a request that is already inside a retrying client.` |
| 1019 | `NestedRetryRepeat` | `Trace` | `Information` | `{Policy} is retrying inside another retrying client` |
| 1020 | `PolicyResolved` | `Debug` | `Information` | `{Policy} resolved: {Effective}` |
| 1021 | `PolicyClassifier` | `Trace` | `Debug` | `{Policy} classifier: {Rules}` |
| 1022 | `HedgeStarted` | `Trace` | `Information` | `{Policy} started hedge attempt {Attempt}: the call has been running longer than {ThresholdMs} ms` |
| 1023 | `HedgeWon` | `Trace` | `Information` | `{Policy} answered from hedge attempt {Attempt} after {ElapsedMs} ms` |
| 1024 | `HedgeDiscarded` | `Trace` | `Information` | `{Policy} discarded attempt {Attempt} after {ElapsedMs} ms because a sibling answered first` |

Field names are shared with the metric tag vocabulary wherever both exist (`Policy`, `Verdict`, `Reason`), so a structured record and a metric describe the same call with the same words.

Events 1010, 1011, 1016 and 1018 are rate-limited per policy - see [flood control](../features/logging.md#flood-control). Events 1007, 1012, 1017 and 1019 are the quiet forms the suppressed occurrences take.
