---
title: Resilience
description: The policy record, its properties, and the eight execution methods.
order: 1
---

# `Resilience`

`sealed partial record Resilience`, in namespace `NResilience`. Immutable; `with` derives a variant.

## Presets

| Member | Value |
| --- | --- |
| `Resilience.None` | Passthrough. One attempt, no bounds, no budget. The executor returns the callback's own task. |
| `Resilience.Default` | Three attempts, 30 s deadline, 10 s attempt timeout, `Backoff.Default`, `Classifier.Default`. |
| `Resilience.Http` | `Default` with `Classifier.Http` and `Name = "http"`. |

## Properties

| Property | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Attempts` | `int` | 3 | Total attempts, including the first. |
| `Deadline` | `TimeSpan` | 30 s | Wall-clock budget for the whole call. `Timeout.InfiniteTimeSpan` for no bound. |
| `AttemptTimeout` | `TimeSpan` | 10 s | Ceiling for one attempt. Effective value is `min(this, time left on the deadline)`. |
| `Backoff` | `Backoff` | `Backoff.Default` | The delay between attempts. |
| `Classify` | `Classifier` | `Classifier.Default` | What counts as what. |
| `Breaker` | `Breaker?` | null | The circuit breaker. Null means no breaking. `with` copies the reference. |
| `Budget` | `RetryBudget?` | null | Null means an automatic budget private to this policy instance. |
| `BeforeAttempt` | `Func<NextAttempt, Task>?` | null | Runs before every attempt, including the first. |
| `OnEvent` | `Action<CallEvent>?` | null | The telemetry listener. Null means nothing is raised and nothing is paid. |
| `Name` | `string?` | null | Used in diagnostics and telemetry tags. |
| `Time` | `TimeProvider` | `TimeProvider.System` | The clock. Leave it alone in production. |

## Methods

| Method | Returns |
| --- | --- |
| `RunAsync<T>(Func<CancellationToken, Task<T>>, CancellationToken)` | `ValueTask<T>` |
| `RunAsync(Func<CancellationToken, Task>, CancellationToken)` | `ValueTask` |
| `RunAsync<TState, T>(Func<TState, CancellationToken, Task<T>>, TState, CancellationToken)` | `ValueTask<T>` |
| `RunAsync<TState>(Func<TState, CancellationToken, Task>, TState, CancellationToken)` | `ValueTask` |
| `TryRunAsync<T>(…)` | `ValueTask<CallResult<T>>` |
| `TryRunAsync(…)` | `ValueTask<CallResult>` |
| `TryRunAsync<TState, T>(…)` | `ValueTask<CallResult<T>>` |
| `TryRunAsync<TState>(…)` | `ValueTask<CallResult>` |
| `Validate()` | `void`. Throws `ResilienceConfigurationException` listing every problem at once. |

The `RunAsync` forms throw: the original exception rethrown with its stack intact, or one of the
[exceptions the library invents](exceptions.md). The `TryRunAsync` forms report instead, and always
materialize the attempt log.

The `TState` overloads exist so the callback can be `static` and allocate no closure. They are the
same length as the closure form at the call site.

`Validate` is not called at construction: a record's `init` setters run after the copy constructor, so
there is no natural hook. Validation happens when you call it, eagerly at DI registration, and lazily
on the first execution of each policy instance.

## `NextAttempt`

What `BeforeAttempt` and `Backoff.Custom` receive. `readonly struct`.

| Member | Meaning |
| --- | --- |
| `Number` | 1-based; 1 on the first attempt. |
| `PreviousVerdict` | How the previous attempt was classified. `Verdict.Ok` on the first. |
| `PreviousException` | What the previous attempt threw, if anything. |
| `Remaining` | Time left on the deadline, or `Timeout.InfiniteTimeSpan`. |
| `CancellationToken` | The caller's token. |

## Equality

Two policies are equal when every property is. A `Breaker` or a `RetryBudget` compares by reference,
because it is a live object rather than configuration. `ToString` prints the configuration.

