---
title: Options and registration
description: Reference for ResilienceOptions, BreakerOptions, IResiliencePolicies, and the AddResilience registration methods.
order: 10
---

# Options and registration

Registration methods are located in the `NResilience.Extensions` package and are provided as extension methods for `IServiceCollection` and `IHttpClientBuilder`.

## `AddResilience` on `IServiceCollection`

Use these methods to register resilience policies within the dependency injection container.

| Overload | Description |
| :--- | :--- |
| `AddResilience(name, Resilience policy, configure = null)` | Registers a specific policy instance. This method validates the policy eagerly. |
| `AddResilience(name, Action<ResilienceOptions> configureOptions, configure = null)` | Registers a policy configured via code. |
| `AddResilience(name, IConfiguration section, configure = null)` | Registers a policy bound to a configuration section. Supports live reloading. |
| `AddResilience(IConfiguration section)` | Registers every child of the configuration section as a policy, using the keys as names. |
| `AddResilience()` | Registers the `IResiliencePolicies` service without any initial policies. |

The optional `configure` parameter is a `Func<Resilience, Resilience>` that runs last, after the configuration section is processed and live objects are re-attached.

## `AddResilience` on `IHttpClientBuilder`

Use these methods to add the `ResilienceHandler` to an `HttpClient` pipeline.

| Overload | Description |
| :--- | :--- |
| `AddResilience(Resilience? policy = null, Action<HttpResilienceOptions>? configureOptions = null, bool telemetry = true)` | Adds the handler using the provided policy value, defaulting to `Resilience.Http`. |
| `AddResilience(string policyName, Action<HttpResilienceOptions>? configureOptions = null, bool telemetry = true)` | Adds the handler using a registered policy, which is resolved when the handler chain is built. |

If the policy does not have its own name, it is named after the client. This prevents multiple clients using `Resilience.Http` from all reporting under the same name in telemetry.

## `IResiliencePolicies`

The `IResiliencePolicies` service provides access to registered policies.

| Member | Description |
| :--- | :--- |
| `this[string name]` | Returns the current policy for the specified name. Throws a `ResilienceConfigurationException` if the name is not registered. |
| `Names` | A collection of all registered policy names. |
| `TryGet(name, out policy)` | A non-throwing method to retrieve a policy. Returns `Resilience.Default` if no policy is found. |

**Recommendation**: Resolve policies per call. Capturing a policy at construction creates a snapshot that will not reflect configuration reloads.

## `ResilienceOptions`

`ResilienceOptions` is a `sealed class` used for binding configuration to a policy. All properties are nullable; a `null` value indicates that the property should not be overridden.

**Properties**:
`Preset`, `Name`, `Attempts`, `Deadline`, `AttemptTimeout`, `TransientBaseDelay`, `ThrottledBaseDelay`, `MaxDelay`, `BackoffFactor`, `Jitter`, `BudgetFraction`, `BudgetMinimumPerSecond`, `SharedBudget`, `Breaker`, `Telemetry`.

- **`ToPolicy(Resilience? baseline = null)`**: Projects the options onto a `Resilience` record. It applies the preset first, then overrides properties that are not null. This method does not perform validation; validation occurs at registration or execution.
- **Budget Disabling**: Setting `BudgetFraction = 0` disables the retry budget.

For more information on the configuration structure, see [Configuration](../di/configuration.md).

## `BreakerOptions`

`BreakerOptions` provides the bindable shape of [`BreakerSettings`](breaker.md) with nullable properties.

- **`ToBreaker(string? name = null)`**: Builds a live `Breaker` instance. A configured breaker is created once per policy and persists through configuration reloads to maintain its state.

## `AddRateLimit` on `IHttpClientBuilder`

Adds the rate limit handler. Call it **after** `AddResilience` on the same client; the other order is refused with a `ResilienceConfigurationException`.

| Overload | Description |
| :--- | :--- |
| `AddRateLimit(RateLimiter, string?)` | Uses a limiter you own. It is not disposed with the handler, so one limiter can be shared across clients. |
| `AddRateLimit(Action<RateLimitOptions>)` | Builds a limiter from options, per host by default. |
| `AddRateLimit(IConfiguration)` | Binds `RateLimitOptions` from a section. Bound once, at registration time - a limiter holds live permits, so it does not reload. |

## `RateLimitOptions`

Set exactly one of `PermitsPerSecond`, `Permits` with `Window`, or `Concurrency`. Anything else is a `ResilienceConfigurationException` listing every problem at once.

| Property | Default | Description |
| :--- | :--- | :--- |
| `PermitsPerSecond` | `null` | Calls allowed per second, with one second of burst. |
| `Permits` | `null` | Calls allowed per `Window`. |
| `Window` | `null` | The window `Permits` applies to. Slides in eight segments. |
| `Concurrency` | `null` | Calls allowed in flight at once - the bulkhead. |
| `QueueLimit` | `0` | How many callers may wait for a permit. Zero refuses immediately. |
| `PerHost` | `true` | Whether each host gets its own quota, scoped by the same `host:port` key the breakers and budgets use. |
| `Name` | `null` | Reported on `RateLimitedException.Limiter` and in the metrics. Defaults to the client's name. |

| Member | Description |
| :--- | :--- |
| `Validate()` | Throws `ResilienceConfigurationException` if the options do not describe exactly one limiter. |
| `ToLimiter()` | Validates, then builds the limiter. The caller owns it. |

## `Limit` and `AcquireOrThrowAsync`

| Member | Description |
| :--- | :--- |
| `Limit.PerSecond(int, int)` | A token bucket: permits per second, with one second of burst. |
| `Limit.PerWindow(int, TimeSpan, int)` | A sliding window in eight segments. |
| `Limit.Concurrency(int, int)` | A concurrency limit - the bulkhead. |
| `RateLimiter.AcquireOrThrowAsync(...)` | Acquires one permit, or throws `RateLimitedException` carrying the limiter's own hint. |
| `PartitionedRateLimiter<TKey>.AcquireOrThrowAsync(...)` | The same, for one partition. |

Call `AcquireOrThrowAsync` inside the callback you hand to `RunAsync`, so the permit is taken once per attempt.

## `ResilienceTelemetry`

`ResilienceTelemetry` is a `static class` that provides access to the library's instrumentation.

| Member | Description |
| :--- | :--- |
| `MeterName` | The name of the meter: `"NResilience"`. |
| `ActivitySourceName` | The name of the activity source: `"NResilience"`. |
| `Meter` | The `Meter` instance used to create all instruments. |
| `ActivitySource` | The `ActivitySource` used to provide spans for HTTP operations. |
| `Listener` | An `Action<CallEvent>` that records data to instruments. It is stateless and allocation-free. |
| `WithTelemetry(this Resilience policy)` | An extension method that chains the `Listener` to the policy's `OnEvent` handler. This operation is idempotent. |

For a list of available instruments, see [Telemetry](../features/telemetry.md).
