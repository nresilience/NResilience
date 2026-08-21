---
title: Logging internals
description: Why log levels are proportional to volume, how flood control works, and what the records deliberately omit.
order: 8
---

# Logging internals

The log listener is a translation layer: it turns the `CallEvent` stream into records that say what each event means. The design questions are which level each record gets, how a pathological state is kept quiet, and what the records deliberately leave out.

## Why levels are proportional to volume

A record's level is proportional to its volume.

| Volume scales with | Level | Reason |
| :--- | :--- | :--- |
| Traffic (per call, per attempt) | `Trace` or `Debug` | Metrics already count these; one line per call would duplicate `nresilience.calls` at significant cost. |
| Incidents (state transitions, first sightings) | `Warning` or `Information` (for recovery) | One line per incident is readable. |
| Caller-visible failures | `Debug` | The exception reaches you, and you log it with the business context the library does not have. |
| Policy resolution | `Debug` | One line per policy per reload. Provenance, not traffic. |

Failed calls are recorded at `Debug` because they end by throwing an exception to the caller. The library records internal details - such as the attempt number, backoff, and verdict - rather than duplicating your own error logs.

The `Verbose` profile exists to lift traffic records above a sink's ingestion threshold. A platform that only ingests `Information` and above would otherwise never show a `Debug` retry record, no matter how the filter is set.

## How flood control works

Three noise types use three different mechanisms, because each has a different shape.

- **Rejections** are traffic-proportional: an open breaker refuses every call for the duration of the break. Events 1010 and 1011 warn at most once per `RepeatWindow` per policy and reason. Within the window, rejections are counted and written as event 1012 at `Debug`, and the count is included in the `Suppressed` field of the next warning. No records are dropped, only demoted, so the count is never lost.
- **Footguns** (`OrphanedWork` and `NestedRetry`) are configuration errors, not events. Each warns the first time it is detected for a policy and remains quiet thereafter, because a repeated warning adds no information.
- **Unretried exception types** are first-sighting events. Event 1007 names an exception type the first time a policy declines to retry it. HTTP status codes are classified from responses and arrive without exceptions, so they follow the quiet path even for the ten thousandth 404.

The suppression state is why the listener is stateful: it holds the per-policy, per-reason window and the first-sighting flags. This is also why at most one log listener attaches per policy, and the first one attached wins.

## What the records do not carry

Gaps in the records are intentional to avoid duplication; correlation is more efficient.

| Not in the records | Where it lives |
| :--- | :--- |
| The request URI, method and status code | `Microsoft.Extensions.Http`'s own `System.Net.Http.HttpClient.<name>.LogicalHandler` category. |
| The breaker's state and break duration | `Breaker.State`, `Breaker.OpenedAt` and `Breaker.Settings`, which a health endpoint already reads. |
| The retry budget's utilization | `RetryBudget.Utilisation`, which is the documented dashboard number. |
| The full attempt history | `AttemptLog.Of(exception)` on the thrown exception. |
| Anything for a call the caller cancelled | Nothing. Caller cancellation rethrows before any event is raised, so a cancelled call is silent by construction. |

Exception objects attach to terminal records, allowing providers to render stack traces. Per-attempt and retry records include the exception type in the message and only attach the object when `IncludeStackTracesOnRetry` is enabled. This prevents a three-attempt call from writing three stack traces for a single failure.

## Go deeper

- [Logging](../features/logging.md): The profiles, the levels, and how to instrument a policy you built yourself.
- [Event IDs](../reference/events.md#log-event-ids): The full table, which is the contract an alert is built on.
