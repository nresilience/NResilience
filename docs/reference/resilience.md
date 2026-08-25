---
title: Resilience
description: Reference for the Resilience record, including its properties and execution methods.
order: 1
---

# `Resilience`

The `Resilience` type is a `sealed partial record` in the `NResilience` namespace. It is immutable; use the `with` expression to derive a variant.

## Presets

NResilience provides several presets for common scenarios:

| Preset | Behavior |
| :--- | :--- |
| `Resilience.None` | Passthrough. Executes one attempt with no bounds or budget. The [executor](index.md) returns the callback's own task. |
| `Resilience.Default` | Three attempts, 30-second deadline, 10-second attempt timeout, `Backoff.Default`, and `Classifier.Default`. |
| `Resilience.Http` | A `Default` policy configured with `Classifier.Http` and `Name = "http"`. |

## Properties

| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Attempts` | `int` | 3 | The total number of attempts, including the first. |
| `Deadline` | `TimeSpan` | 30 s | The wall-clock budget for the entire call. Use `Timeout.InfiniteTimeSpan` to disable the bound. |
| `AttemptTimeout` | `TimeSpan` | 10 s | The maximum duration for a single attempt. The effective value is the minimum of this property and the remaining time on the deadline. |
| `Backoff` | `Backoff` | `Backoff.Default` | The delay between attempts. |
| `Classify` | `Classifier` | `Classifier.Default` | The logic used to classify outcomes. |
| `Breaker` | `Breaker?` | `null` | The circuit breaker. A `null` value indicates no breaking is active. |
| `Budget` | `RetryBudget?` | `RetryBudget.Automatic` | The retry budget. `RetryBudget.Automatic` creates a budget private to the policy instance. `null` and `RetryBudget.None` disable the budget. |
| `BeforeAttempt` | `Func<NextAttempt, Task>?` | `null` | A function that runs before every attempt, including the first. |
| `OnEvent` | `Action<CallEvent>?` | `null` | The telemetry listener. If `null`, no events are raised and no performance cost is incurred. |
| `Name` | `string?` | `null` | A name used in diagnostics and telemetry tags. |
| `Time` | `TimeProvider` | `TimeProvider.System` | The clock used for timing. Use the system provider in production. |

## Methods

The `Resilience` record provides methods to execute calls with the defined resilience policy.

| Method | Return Type |
| :--- | :--- |
| `RunAsync<T>(Func<CancellationToken, Task<T>>, CancellationToken)` | `ValueTask<T>` |
| `RunAsync(Func<CancellationToken, Task>, CancellationToken)` | `ValueTask` |
| `RunAsync<TState, T>(Func<TState, CancellationToken, Task<T>>, TState, CancellationToken)` | `ValueTask<T>` |
| `RunAsync<TState>(Func<TState, CancellationToken, Task>, TState, CancellationToken)` | `ValueTask` |
| `TryRunAsync<T>(…)` | `ValueTask<CallResult<T>>` |
| `TryRunAsync(…)` | `ValueTask<CallResult>` |
| `TryRunAsync<TState, T>(…)` | `ValueTask<CallResult<T>>` |
| `TryRunAsync<TState>(…)` | `ValueTask<CallResult>` |
| `Validate()` | `void` |
| `Validated()` | `Resilience` |

### Execution behavior

`RunAsync` methods throw the original exception with its stack trace intact, or one of the [exceptions defined by the library](exceptions.md). `TryRunAsync` methods return a `CallResult` and always materialize the attempt log.

#### Cancellation tokens
Every method signature includes two different `CancellationToken` parameters:
1. **The callback token**: Passed to the execution callback. It is cancelled when the attempt hits its `AttemptTimeout` or when the caller's token is cancelled.
2. **The caller token**: The trailing parameter. It cancels the entire operation, including all retries.

For more information, see the [cancellation contract](../deep-dives/cancellation.md).

#### State and allocation
The `TState` overloads allow you to use `static` callbacks, which avoids closure allocations. These overloads provide the same functionality as the closure-based forms.

#### Validation
The `Validate` method checks the policy configuration for errors and throws a `ResilienceConfigurationException` if any are found. Validation does not occur at construction; it happens when you call `Validate` explicitly, during eager DI registration, or lazily on the first execution of a policy instance.

`Validated()` runs the same check and returns the policy, so a bad configuration throws where the policy is written rather than on the first call. This is the shape for a `static readonly` field, where a lazily-thrown configuration error would otherwise surface as a `TypeInitializationException` much later:

```csharp
public static class Policies
{
    public static readonly Resilience Api = (Resilience.Http with { Deadline = Config.ApiDeadline }).Validated();
}
```

> [!NOTE]
> The parentheses are required. C# does not allow member access directly on a `with` expression.

## `NextAttempt`

The `NextAttempt` `readonly struct` is passed to `BeforeAttempt` and `Backoff.Custom`.

| Member | Description |
| :--- | :--- |
| `Number` | The 1-based index of the attempt (1 for the first attempt). |
| `PreviousVerdict` | The classification of the previous attempt. Defaults to `Verdict.Ok` for the first attempt. |
| `PreviousException` | The exception thrown by the previous attempt, if any. |
| `Remaining` | The time remaining on the deadline, or `Timeout.InfiniteTimeSpan`. |
| `CancellationToken` | The caller's cancellation token. |

## Equality

Two policies are considered equal if all their properties are equal. `Breaker` and `RetryBudget` are compared by reference because they are live state objects rather than configuration. `ToString` returns the policy configuration.
