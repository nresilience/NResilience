---
title: Resilience
description: Reference for the Resilience record, including its properties and execution methods.
order: 1
---

# `Resilience`

The `Resilience` type is a `sealed partial record` in the `NResilience` namespace. It is immutable; derive a variant with the `with` expression.

## Presets

The presets cover common scenarios:

| Preset | Behavior |
| :--- | :--- |
| `Resilience.None` | Passthrough. Executes one attempt with no bounds or budget, and `Adaptive` is `false`, which disables measurement. The [executor](index.md) returns the callback's own task. To derive a bound from this preset, enable that bound by name and set `Adaptive` to `true`. |
| `Resilience.Default` | Three attempts, 30-second deadline, 10-second attempt timeout, `Backoff.Default`, and `Classifier.Default`. |
| `Resilience.Http` | A `Default` policy configured with `Classifier.Http` and `Name = "http"`. |

## Properties

| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `Attempts` | `int` | 3 | How many attempts to make. `1` means no retry; `3` means try, then retry twice. A count of calls, not a count of retries. |
| `Deadline` | `TimeSpan` | 30 s | The wall-clock budget for the entire call. Use `Timeout.InfiniteTimeSpan` to disable the bound. |
| `AttemptTimeout` | `TimeSpan` | 10 s | The maximum duration for a single attempt. The effective value is the minimum of this property and the remaining time on the deadline. |
| `AttemptCeiling` | `AttemptCeiling?` | `AttemptCeiling.Above(3)` | A measured attempt ceiling. Set it to `null` to leave `AttemptTimeout` as the only per-attempt bound. The measured term can only lower the ceiling. No default is supplied when `AttemptTimeout` is `Timeout.InfiniteTimeSpan` or at or below `AttemptCeiling.Floor`, because there is no ceiling there to lower. |
| `UseAmbientDeadline` | `bool` | `false` | Whether the deadline is clamped by the one the current call inherited from its caller. When set, the effective deadline is the minimum of `Deadline` and `ResilienceDeadline.Remaining`, resolved once per call. |
| `Backoff` | `Backoff` | `Backoff.Default` | The delay between attempts. |
| `Classifier` | `Classifier` | `Classifier.Default` | The logic used to classify outcomes. |
| `Breaker` | `Breaker?` | `null` | The circuit breaker. A `null` value indicates no breaking is active. |
| `Hedge` | `Hedge?` | `null` | Hedging. A `null` value indicates no hedging. Requires `Attempts` greater than 1. |
| `Budget` | `RetryBudget?` | `RetryBudget.Automatic` | The retry budget. `RetryBudget.Automatic` creates a budget private to the policy instance. `null` and `RetryBudget.None` disable the budget. |
| `BeforeAttempt` | `Func<NextAttempt, Task>?` | `null` | A function that runs before every attempt, including the first. |
| `OnEvent` | `Action<CallEvent>?` | `null` | The telemetry listener. If `null`, no events are raised and no performance cost is incurred. |
| `Adaptive` | `bool` | `true` | Whether the policy measures the dependency and bounds itself by what it measures. `false` suppresses every measured term the library would supply - such as `AttemptCeiling` - and leaves only the constants written here. It does not reach `Breaker`, which has its own switch. Setting it `false` alongside a configured `AttemptCeiling` or `Hedge` results in an error. |
| `Name` | `string?` | `null` | A name used in diagnostics and telemetry tags. |
| `Time` | `TimeProvider` | `TimeProvider.System` | The clock used for timing. Use the system provider in production. |

One property is computed rather than configured:

| Property | Type | Description |
| :--- | :--- | :--- |
| `MeasuredBackoffBase` | `TimeSpan?` | The base delay the next transient retry would wait when `Backoff.MeasuredBase` is configured, after the `Spread` clamp. `null` when no base is being measured or the estimate is still cold. Reading it validates the policy, exactly as executing it does. |
| `MeasuredAttemptCeiling` | `TimeSpan?` | What `AttemptCeiling` currently measures the ceiling to be, before `AttemptTimeout` and the deadline clamp it. `null` when `AttemptCeiling` is not configured or the estimate is still cold. Reading it validates the policy, exactly as executing it does. |

## Methods

The `Resilience` record provides the execution methods.

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
| `RunAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>>, CancellationToken)` | `IAsyncEnumerable<T>` |
| `RunAsync<TState, T>(Func<TState, CancellationToken, IAsyncEnumerable<T>>, TState, CancellationToken)` | `IAsyncEnumerable<T>` |
| `Validate()` | `void` |
| `Validated()` | `Resilience` |
| `WithListener(Action<CallEvent>)` | `Resilience` |

The eight execution overloads have counterparts that take `ValueTask`-returning callbacks, for `Channel`, `PipeReader`, `Socket`, `Stream` and anything else built on `IValueTaskSource`. These counterparts use the same names and argument order:

| Method | Return Type |
| :--- | :--- |
| `RunAsync<T>(Func<CancellationToken, ValueTask<T>>, CancellationToken)` | `ValueTask<T>` |
| `RunAsync(Func<CancellationToken, ValueTask>, CancellationToken)` | `ValueTask` |
| `RunAsync<TState, T>(Func<TState, CancellationToken, ValueTask<T>>, TState, CancellationToken)` | `ValueTask<T>` |
| `RunAsync<TState>(Func<TState, CancellationToken, ValueTask>, TState, CancellationToken)` | `ValueTask` |
| `TryRunAsync<T>(…)` | `ValueTask<CallResult<T>>` |
| `TryRunAsync(…)` | `ValueTask<CallResult>` |
| `TryRunAsync<TState, T>(…)` | `ValueTask<CallResult<T>>` |
| `TryRunAsync<TState>(…)` | `ValueTask<CallResult>` |

These extension methods live in the `NResilience` namespace, so they need no `using` beyond the one for `Resilience`. A lambda returning a `ValueTask` binds to these methods. An `async` lambda binds to the `Task` overload, because C# considers extension methods only when no instance method applies.

<!-- snippet: reference-valuetask-callback -->
```csharp
// ReadAsync returns a ValueTask, so this binds to the ValueTask overload. 
// Buffered reads allocate nothing.
var buffered = await api.RunAsync(
    static (source, attempt) => source.ReadAsync(cancellationToken: attempt),
    state: feed,
    cancellationToken: cancellationToken);

// ReadNameAsync returns a Task, so this binds to the Task overload. Same name, same shape.
var name = await api.RunAsync(attempt => db.ReadNameAsync(id: id, cancellationToken: attempt), cancellationToken: cancellationToken);
```
<!-- endsnippet -->

To use the `ValueTask` path with an `async` lambda, provide an explicit return type: `async ValueTask<int> (ct) => …`. This is rarely necessary, because an `async` lambda allocates its own state machine regardless of return type. See [where the allocations are](../deep-dives/allocations.md) for what the overloads save and why they are shaped this way.

The two streaming overloads take a **cold source** - a callback returning `IAsyncEnumerable<T>` - rather than a task, so a lambda binds to them by return type alone. Each attempt re-invokes the source, retrying until the first element is yielded, then hands the rest of the enumeration to the caller untouched. A policy with `Hedge` configured is refused by these overloads at the call. See [streaming](../features/streaming.md) for the semantics.

### Execution behavior

`RunAsync` methods throw the original exception with its stack trace intact, or one of the [exceptions defined by the library](exceptions.md). `TryRunAsync` methods return a `CallResult` and always materialize the attempt log.

#### Cancellation tokens
Every method signature includes two different `CancellationToken` parameters:
1. **The callback token**: Passed to the execution callback. Cancelled when the attempt hits its `AttemptTimeout` or when the caller's token is cancelled.
2. **The caller token**: The trailing parameter. Cancels the whole operation, including all retries.

For more information, see the [cancellation contract](../deep-dives/cancellation.md).

#### State and allocation
The `TState` overloads allow `static` callbacks, which avoid closure allocations. They behave the same as the closure-based forms.

#### Validation
`Validate` checks the policy configuration and throws `ResilienceConfigurationException` if it finds problems. Validation does not happen at construction; it runs when you call `Validate` explicitly, during eager DI registration, or lazily on a policy instance's first execution.

`Validated()` runs the same check and returns the policy, so a bad configuration throws where the policy is written rather than on the first call. That is the shape for a `static readonly` field, where a lazily-thrown configuration error would otherwise surface much later as a `TypeInitializationException`:

```csharp
public static class Policies
{
    public static readonly Resilience Api = (Resilience.Http with { Deadline = Config.ApiDeadline }).Validated();
}
```

> [!NOTE]
> The parentheses are required. C# does not allow member access directly on a `with` expression.

`BreakerSettings`, `HttpResilienceOptions` and `GrpcResilienceOptions` each have the same `Validate()` / `Validated()` pair, for the same reason.

#### Adding a listener
`WithListener(listener)` returns the policy with one more listener on `OnEvent`, *added* to whatever is already there:

```csharp
var counted = Policies.Api.WithListener(e => Metrics.Record(e.Kind));
```

Assigning `OnEvent` in a `with` expression replaces it instead, which silently drops the telemetry and logging a container registration attached. `WithListener` is what `WithTelemetry()` and `WithLogging()` do to each other. Listeners run in the order they were added.

`BeforeAttempt` and `Admit` have no equivalent and are single slots by design: two pieces of setup are one hook that does both, and combining two admission guards needs a rule for which refusal wins that belongs to your system rather than to the library.

## `NextAttempt`

The `NextAttempt` `readonly struct` is passed to `BeforeAttempt`, `Admit` and `Backoff.Custom`.

| Member | Description |
| :--- | :--- |
| `Number` | The 1-based index of the attempt (1 for the first attempt). |
| `PreviousVerdict` | The classification of the previous attempt. Defaults to `Verdict.Ok` for the first attempt. |
| `PreviousException` | The exception thrown by the previous attempt, if any. |
| `Remaining` | The time remaining on the deadline, or `Timeout.InfiniteTimeSpan`. |
| `CancellationToken` | The caller's cancellation token. |

## `ResilienceDeadline`

`ResilienceDeadline` is a `static class` holding the deadline the current logical call inherited, plus the two helpers that put one on a wire. The executor reads it only for a policy whose `UseAmbientDeadline` is set.

| Member | Description |
| :--- | :--- |
| `Header` | The default header name: `"X-Deadline-Ms"`. |
| `Remaining` | How long the inbound deadline has left, or `null` when the call inherited none. `TimeSpan.Zero` when it inherited one that has expired. |
| `Begin(remaining, time = null)` | Publishes an inbound deadline for the current logical call. Returns a `DeadlineScope` that restores the previous value when disposed. `Timeout.InfiniteTimeSpan` clears the deadline for the scope rather than publishing an unbounded one. |
| `TryParse(value, out remaining)` | Reads a header value: whole milliseconds as a positive integer. Anything else - empty, zero, negative, unit-suffixed, or above `int.MaxValue` milliseconds - is no deadline, and the failure is silent. |
| `Format(remaining)` | Writes a header value: whole milliseconds, rounded down, never below 1. `null` when there is nothing to say. |

The effective deadline is `min(Deadline, Remaining)`, resolved once when the call starts. See [deadline propagation](../features/deadlines.md#propagate-the-deadline-across-a-hop) for both halves, and [the cancellation contract](../deep-dives/cancellation.md) for what the ambient read costs.

## `PolicyScope<TKey>`

`PolicyScope<TKey>` is a `sealed class` holding one policy per key, each with its own breaker, retry budget, and hedging latency estimate. `TKey` must be non-nullable. Every member is thread-safe. Hold one for the life of the process - see [keyed policy scope](../features/policy-scope.md).

| Member | Description |
| :--- | :--- |
| `PolicyScope(template, shape = null, maximumKeys = 1024, comparer = null)` | Creates a scope. `template` is validated eagerly. `shape` derives a key's policy on first sight. `maximumKeys` must be at least 1. `comparer` defaults to `EqualityComparer<TKey>.Default`. |
| `For(key)` | The policy for one key, derived on first sight and cached. |
| `Breakers()` | A snapshot of the breakers, by key. Empty when the template carries no breaker. |
| `Budgets()` | A snapshot of the retry budgets, by key. |
| `Template` | The policy every key starts from, as handed in. |
| `MaximumKeys` | How many keys the scope keeps. |
| `Count` | How many keys it currently holds. Approximate under concurrency, and briefly above `MaximumKeys` while a sweep catches up. |

A `Breaker` on the template is a **prototype**: each key gets its own breaker with those settings, and the template's instance is never executed against. A `Budget` that is `null` or `RetryBudget.Automatic` becomes one budget per key; an explicit instance, such as `RetryBudget.Shared(name)`, is left alone.

## Equality

Two policies are equal when all their properties are equal. `Breaker` and `RetryBudget` are compared by reference because they are live state objects rather than configuration. `ToString` returns the policy configuration.
