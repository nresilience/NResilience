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
| `Restarts` | `int` | 3 | How many times a checkpointed stream may reconnect after failing part-way through. Read only by the `RunAsync`/`TryRunAsync` overloads that take a checkpoint. Each restart is a fresh attempt sequence with its own `Attempts`, so this bounds the total separately. See [Checkpointed resume](../features/streaming.md#checkpointed-resume). |
| `Deadline` | `TimeSpan` | 30 s | The wall-clock budget for the entire call. Use `Timeout.InfiniteTimeSpan` to disable the bound. |
| `AttemptTimeout` | `TimeSpan` | 10 s | The maximum duration for a single attempt. The effective value is the minimum of this property and the remaining time on the deadline. |
| `AttemptCeiling` | `AttemptCeiling?` | `AttemptCeiling.Above(3)` | A measured attempt ceiling. Set it to `null` to leave `AttemptTimeout` as the only per-attempt bound. The measured term can only lower the ceiling. No default is supplied when `AttemptTimeout` is `Timeout.InfiniteTimeSpan` or at or below `AttemptCeiling.Floor`, because there is no ceiling there to lower. |
| `UseAmbientDeadline` | `bool` | `false` | Whether the deadline is clamped by the one the current call inherited from its caller. When set, the effective deadline is the minimum of `Deadline` and `AmbientDeadline.Remaining`, resolved once per call. |
| `UseAmbientCriticality` | `bool` | `false` | Whether the policy reads how much the current call matters. When set, a `Sheddable` call is never hedged and is refused a retry once the `Budget` bucket is more than half spent; every other level is unaffected. Requires a `Hedge`, or more than one attempt and a `Budget` other than `RetryBudget.None`. See [Criticality](../features/criticality.md). |
| `Backoff` | `Backoff` | `Backoff.Default` | The delay between attempts. |
| `Classifier` | `Classifier` | `Classifier.Default` | The logic used to classify outcomes. |
| `Breaker` | `Breaker?` | `null` | The circuit breaker. A `null` value indicates no breaking is active. |
| `Hedge` | `Hedge?` | `null` | Hedging. A `null` value indicates no hedging. Requires `Attempts` greater than 1. |
| `Saturation` | `Saturation?` | `null` | Local saturation awareness. When set, the policy stops feeding `AttemptCeiling`, `Backoff.MeasuredBase` and the `Hedge` threshold while this process's thread pool queues more than `Multiple` times its own normal delay. It only declines to record: nothing is refused and no bound moves. Requires at least one of those three features to be configured. See [Local saturation](../features/saturation.md). |
| `Budget` | `RetryBudget` | `RetryBudget.Automatic` | The retry budget. `RetryBudget.Automatic` creates a budget private to the policy instance, or to each key when the policy is scoped. `RetryBudget.None` is no budget. Any other instance is shared wherever the instance is shared. |
| `BeforeAttempt` | `Func<NextAttempt, Task>?` | `null` | A function that runs before every attempt, including the first. |
| `OnEvent` | `Action<CallEvent>?` | `null` | The telemetry listener. If `null`, no events are raised and no performance cost is incurred. |
| `Adaptive` | `bool` | `true` | Whether the policy measures the dependency and bounds itself by what it measures. `false` suppresses every measured term the library would supply - such as `AttemptCeiling` - and leaves only the constants written here. It does not reach `Breaker`, which has its own switch. Setting it `false` alongside a configured `AttemptCeiling`, `Hedge` or `Saturation` results in an error. |
| `Name` | `string?` | `null` | A name used in diagnostics and telemetry tags. |
| `Time` | `TimeProvider` | `TimeProvider.System` | The clock used for timing. Use the system provider in production. |

One property is computed rather than configured:

| Property | Type | Description |
| :--- | :--- | :--- |
| `Measured` | `MeasuredValues` | What the policy is currently measuring. See [`MeasuredValues`](#measuredvalues). |

## `MeasuredValues`

`policy.Measured` is the one place a dashboard looks. Every property is a *reading*, never configuration - the configuration that produces it is `AttemptCeiling`, `Backoff.MeasuredBase`, `Hedge` and `Saturation` respectively. Each returns `null` when its feature is not configured, or when the estimate is still cold, and reading one validates the policy exactly as executing it does.

| Property | Type | Description |
| :--- | :--- | :--- |
| `AttemptCeiling` | `TimeSpan?` | What `AttemptCeiling` currently measures the ceiling to be, before `AttemptTimeout` and the deadline clamp it. A value above `AttemptTimeout` means the clamp is what bounds the attempt. |
| `BackoffBase` | `TimeSpan?` | The base delay the next transient retry would wait when `Backoff.MeasuredBase` is configured, after the `Spread` clamp. |
| `HedgeThreshold` | `TimeSpan?` | How long a call has to run before a hedge arms, after `Hedge.MinimumDelay` has floored it. This is the latency at which the library starts duplicating load. The gates that can still refuse a hedge - the breaker, the win rate, the concurrency ceiling, the remaining deadline - are asked when the threshold fires, so a reading is when a hedge *would* be considered rather than a promise that one starts. |
| `QueueDelay` | `TimeSpan?` | How long a work item currently waits for a thread in this process's pool, when `Saturation` is configured. A non-null reading is not the same as "saturated" - the comparison is against the process's own recent median, so what matters is how far the number has moved. `SaturationDetected` is what says the comparison fired. |

The estimates are private to the policy instance. The HTTP handler derives one policy per host, so each host is measured independently. `QueueDelay` is the exception: there is one thread pool, so every policy in the process reports the same number for it.

## Explaining a policy

`Explain()` returns what the policy will do, as text: which bound binds first, the worst-case timeline attempt by attempt, the load a call can add, what each measured term currently reads, and what the breaker and the classifier will do about a failure.

The worst-case wall clock of a call is a function of `Attempts`, `Deadline`, `AttemptTimeout`, `AttemptCeiling`, `Backoff`, the breaker's state and the budget's fill. `Explain()` computes it.

<!-- snippet: explain-call -->
```csharp
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 5), Name = "api" };

// At a REPL, or once at startup. Nothing here contacts a dependency or runs your callback.
Console.WriteLine(value: api.Explain());

// The allocation-free form, for a log that wants it on a stream rather than on the heap.
api.Explain(writer: Console.Out);
```
<!-- endsnippet -->

For `Resilience.Http with { Deadline = TimeSpan.FromSeconds(5), Name = "api" }`:

<!-- snippet: explain-api.txt -->
```text
Policy "api" - 3 attempts, 5s deadline, 10s attempt timeout

  Bound first by:  the deadline. Attempt 1 is clamped to 5.00s, 50% of the 10s attempt timeout,
                   and attempts 2-3 never start.

  Worst case:      attempt 1  0.00s -> 5.00s  min(10s attempt timeout, 5.00s left)
                   deadline   5.00s           DeadlineExceededException
                   (a throttled attempt uses the 1s throttled base; Retry-After wins over both)

  Load:            up to 3.0x per call from retries. The automatic retry budget, which is
                   private to this policy, holds the sustained rate to 1.1x - 10% of successful
                   attempts, with a 3/s floor - and it is 0% spent.

  Measured now:    attempt ceiling   -  cold - 3x p95 over 5m, 20 samples minimum
                   backoff base      -  not configured
                   hedge threshold   -  not configured
                   pool queue delay  -  not configured

  Breaker:         not configured, so nothing stops a call reaching a dependency that is already
                   down.

  Classifier:      Http
                     exception HttpRequestException -> Transient
                     exception TimeoutException -> Transient
                     exception IOException -> Transient
                     exception SocketException -> Transient
                     result HttpResponseMessage -> (predicate)
                     any other exception -> Permanent
                     any other result -> Ok
```
<!-- endsnippet -->

Two lines carry most of the value:

- **`Bound first by`** names the bound that ends the worst case, and what it costs. An open breaker or a spent retry budget comes before any arithmetic, because a call refused by either never reaches the timeline below it.
- **`Measured now`** distinguishes *configured* from *in effect*. A measured term is invisible until it has samples, and a live reading is the only way to tell a cold estimate from a warm one.

<!-- snippet: explain-configured-versus-measured -->
```csharp
var api = Resilience.Http with { AttemptTimeout = TimeSpan.FromSeconds(value: 30) };

// Configured: 30s. In effect: nothing yet - the ceiling is cold until it has samples, and
// until then the attempt gets the 30 seconds. The "Measured now" block reports both.
var configured = api.AttemptTimeout;
var inEffect = api.Measured.AttemptCeiling;
```
<!-- endsnippet -->

`Explain()` is safe to call on a policy that cannot be executed: an invalid policy is reported as such and the timeline is still laid out, because the timeline is usually what makes the problem obvious. Nothing in it runs the callback, contacts a dependency, or changes the policy.

The same text reaches three other places:

| Where | What it shows |
| :--- | :--- |
| `ResilienceConfigurationException.Message` | The header and the worst-case timeline, after the list of problems. |
| The [health check](../di/health-checks.md) payload | One line per registered policy, under the key `policy:<name>`. |
| [`NRES004`](analyzers.md#nres004-attempt-timeout-exceeds-deadline) | The same durations and the same clamp clause, computed at build time. |

A call that does not ask for an explanation pays nothing for the method: no field on the record, no branch in the [executor](index.md), and no allocation. Reading the measured terms validates the policy, exactly as `Measured` does.

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
| `TryRunAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>>, CancellationToken)` | `ValueTask<CallResult<IAsyncEnumerable<T>>>` |
| `TryRunAsync<TState, T>(Func<TState, CancellationToken, IAsyncEnumerable<T>>, TState, CancellationToken)` | `ValueTask<CallResult<IAsyncEnumerable<T>>>` |
| `RunAsync<TCheckpoint, T>(Func<TCheckpoint, CancellationToken, IAsyncEnumerable<T>>, TCheckpoint, Func<T, TCheckpoint>, CancellationToken)` | `IAsyncEnumerable<T>` |
| `TryRunAsync<TCheckpoint, T>(Func<TCheckpoint, CancellationToken, IAsyncEnumerable<T>>, TCheckpoint, Func<T, TCheckpoint>, CancellationToken)` | `ValueTask<CallResult<IAsyncEnumerable<T>>>` |
| `Explain()` | `string` |
| `Explain(TextWriter)` | `void` |
| `Validate()` | `void` |
| `Validated()` | `Resilience` |
| `WithListener(Action<CallEvent>)` | `Resilience` |

The eight buffered execution overloads have counterparts that take `ValueTask`-returning callbacks, for `Channel`, `PipeReader`, `Socket`, `Stream` and anything else built on `IValueTaskSource`. These counterparts use the same names and argument order:

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

The six streaming overloads take a **cold source** - a callback returning `IAsyncEnumerable<T>` - rather than a task, so a lambda binds to them by return type alone. Each attempt re-invokes the source, retrying until the first element is yielded, then hands the rest of the enumeration to the caller untouched. A policy with `Hedge` configured is refused by these overloads at the call. See [streaming](../features/streaming.md) for the semantics.

The streaming `TryRunAsync` awaits to the **first element** and reports what a failed `RunAsync` would have thrown from the first `MoveNextAsync`. The value of a successful `CallResult<IAsyncEnumerable<T>>` is the started enumeration: it is enumerable once, and it implements `IAsyncDisposable` for a caller who reads `IsSuccess` and then decides not to consume it. A fault after the first element is not part of the result - it throws from `MoveNextAsync`, because the result was decided before that element existed.

The two checkpointed overloads take a `start` checkpoint and a `checkpoint` reader alongside the source, and restart the source from the last one accepted instead of stopping when a fault arrives after the first element - see [checkpointed resume](../features/streaming.md#checkpointed-resume). `CallResult<IAsyncEnumerable<T>>.Attempts` is `AttemptLog.Empty` on a successful checkpointed `TryRunAsync`: the log on the throwing form's failure is one restart's own, and a checkpointed stream's history spans restarts no single `AttemptLog` was shaped to describe.

> [!NOTE]
> A lambda that only throws needs an explicit return type here - `TryRunAsync(Task<int> (ct) => throw new IOException())` - because a lambda with no return statement is ambiguous between the `Task<T>` and `IAsyncEnumerable<T>` overloads.

### Execution behavior

`RunAsync` methods throw the original exception with its stack trace intact, or one of the [exceptions defined by the library](exceptions.md). `TryRunAsync` methods return a `CallResult` and always materialize the attempt log - including the streaming form, whose log covers the attempts up to the first element.

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

## `AmbientDeadline`

`AmbientDeadline` is a `static class` holding the deadline the current logical call inherited, plus the two helpers that put one on a wire. The executor reads it only for a policy whose `UseAmbientDeadline` is set.

| Member | Description |
| :--- | :--- |
| `Header` | The default header name: `"X-Deadline-Ms"`. |
| `Remaining` | How long the inbound deadline has left, or `null` when the call inherited none. `TimeSpan.Zero` when it inherited one that has expired. |
| `Begin(remaining, time = null)` | Publishes an inbound deadline for the current logical call. Returns an `AmbientDeadline.Scope` that restores the previous value when disposed. `Timeout.InfiniteTimeSpan` clears the deadline for the scope rather than publishing an unbounded one. |
| `TryParse(value, out remaining)` | Reads a header value: whole milliseconds as a positive integer. Anything else - empty, zero, negative, unit-suffixed, or above `int.MaxValue` milliseconds - is no deadline, and the failure is silent. |
| `Format(remaining)` | Writes a header value: whole milliseconds, rounded down, never below 1. `null` when there is nothing to say. |

The effective deadline is `min(Deadline, Remaining)`, resolved once when the call starts. See [deadline propagation](../features/deadlines.md#propagate-the-deadline-across-a-hop) for both halves, and [the cancellation contract](../deep-dives/cancellation.md) for what the ambient read costs.

## `AmbientCriticality`

`AmbientCriticality` is a `static class` holding how much the current logical call matters, plus the two helpers that put a level on a wire. The executor reads it only for a policy whose `UseAmbientCriticality` is set.

| Member | Description |
| :--- | :--- |
| `Header` | The default header name: `"X-Criticality"`. |
| `Current` | The level this call is running at. `Criticality.Critical` when nothing published one. |
| `Begin(criticality)` | Publishes a level for the current logical call. Returns an `AmbientCriticality.Scope` that restores the previous value when disposed. An undeclared level throws `ArgumentOutOfRangeException`. |
| `TryParse(value, out criticality)` | Reads a header value: one of the four level names, matched without regard to case. `"CriticalPlus"` parses and clamps to `Critical`, because a caller cannot escalate itself. Anything else is no level, the failure is silent, and the out value is `Critical`. |
| `Format(criticality)` | Writes a header value: the level's name. An undeclared level throws `ArgumentOutOfRangeException`. |

`Criticality` is a `byte`-backed enum ordered from least to most important: `Sheddable`, `SheddablePlus`, `Critical`, `CriticalPlus`. See [Criticality](../features/criticality.md) for both halves and for what the executor does with a level.

## `NestedRetry`

`NestedRetry` is a `static class` holding whether the caller of the current logical call is already retrying, plus the header and marker that carry that fact across a hop. It is the flag counterpart to `AmbientDeadline`: a bool rather than a value with state, because unlike a deadline it does not decay.

| Member | Description |
| :--- | :--- |
| `Header` | The header a retrying client stamps on every request it can retry: `"X-NResilience-Retrying"`. |
| `Marker` | The only value that header ever carries: `"1"`. |
| `IsCallerRetrying` | Whether the caller said it was already retrying. `false` when nobody published a flag. |
| `Begin(callerRetrying)` | Publishes the flag for the current logical call. Returns a `NestedRetry.Scope` that restores the previous value when disposed. |
| `IsMarker(value)` | Whether a header value is `Marker`. The check is exact, because a value this library did not write is not evidence of anything. |

See [nested retries](../http/nested-retries.md) for both halves, and [`UseResilienceNestedRetry`](options.md#useresiliencenestedretry-on-iapplicationbuilder) for the inbound middleware.

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

A `Breaker` on the template is a **prototype**: each key gets its own breaker with those settings, and the template's instance is never executed against. A `Budget` of `RetryBudget.Automatic` becomes one budget per key; any other value, including `RetryBudget.None` and `RetryBudget.Shared(name)`, is left alone.

## Equality

Two policies are equal when all their properties are equal. `Breaker` and `RetryBudget` are compared by reference because they are live state objects rather than configuration. `ToString` returns the policy configuration.
