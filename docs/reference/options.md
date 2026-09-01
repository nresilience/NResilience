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
| `AddResilienceLogging(Action<ResilienceLoggingOptions>? configure = null)` | Sets the process-wide log listener settings. A registered policy already logs, so this does not turn logging on. |

The optional `configure` parameter is a `Func<Resilience, Resilience>` that runs last, after the configuration section is processed and live objects are re-attached.

## `AddResilience` on `IHttpClientBuilder`

Use these methods to add the `ResilienceHandler` to an `HttpClient` pipeline.

| Overload | Description |
| :--- | :--- |
| `AddResilience(Resilience? policy = null, Action<HttpResilienceOptions>? configureOptions = null, bool telemetry = true, ResilienceLogProfile? logging = null)` | Adds the handler using the provided policy value, defaulting to `Resilience.Http`. |
| `AddResilience(string policyName, Action<HttpResilienceOptions>? configureOptions = null, bool telemetry = true, ResilienceLogProfile? logging = null)` | Adds the handler using a registered policy, which is resolved when the handler chain is built. |

If `logging` is `null`, the process default is used. Registered policies log under their registration's own profile, so this parameter only affects policies that the registration left unlogged.

If the policy does not have its own name, it is named after the client. This prevents multiple clients using `Resilience.Http` from all reporting under the same name in telemetry.

## `UseResilienceDeadline` on `IApplicationBuilder`

`UseResilienceDeadline` is in the `NResilience.AspNetCore` package, which is separate because it is the only part of NResilience that requires ASP.NET Core. It reads the deadline a caller sent and publishes it for the rest of the request, so every policy with `UseAmbientDeadline` set is bounded by `min(its own deadline, the time the caller is still waiting)`.

| Overload | Description |
| :--- | :--- |
| `UseResilienceDeadline(Action<ResilienceDeadlineOptions>? configure = null)` | Adds the middleware. Register it before anything that makes an outbound call. |

`ResilienceDeadlineOptions` is a `sealed class`:

| Property | Default | Description |
| :--- | :--- | :--- |
| `Header` | `"X-Deadline-Ms"` | The header carrying whole milliseconds left. |
| `Maximum` | `null` | The longest inbound deadline this service believes. A header above it is ignored. `null` believes any of them. |
| `Reserve` | `TimeSpan.Zero` | How much of the inbound deadline is kept back for this service's own work, and therefore withheld from outbound calls. |

The clock is `TimeProvider` from the container when one is registered, and `TimeProvider.System` otherwise. An expired inbound deadline does not fail the request; it fails the outbound calls, which is the distinction [deadline propagation](../features/deadlines.md#propagate-the-deadline-across-a-hop) explains.

## `UseResilienceNestedRetry` on `IApplicationBuilder`

`UseResilienceNestedRetry` is in the `NResilience.AspNetCore` package. It reads the nested-retry marker a retrying caller sent and publishes it for the rest of the request, so the outbound handler reports `NestedRetry` for this request's own outbound calls.

| Overload | Description |
| :--- | :--- |
| `UseResilienceNestedRetry(Action<ResilienceNestedRetryOptions>? configure = null)` | Adds the middleware. Register it before anything that makes an outbound call. |

`ResilienceNestedRetryOptions` is a `sealed class`:

| Property | Default | Description |
| :--- | :--- | :--- |
| `Header` | `"X-NResilience-Retrying"` | The header carrying the marker. |

Only the value `"1"` counts as the marker. Like the deadline middleware, it reports and does not intervene; see [Nested retries](../http/nested-retries.md).

## `AddResilienceExceptionHandler` on `IServiceCollection`

`AddResilienceExceptionHandler` is in the `NResilience.AspNetCore` package. It registers an `IExceptionHandler` that maps the exceptions NResilience throws to the HTTP responses they mean, so no endpoint needs its own try/catch.

| Overload | Description |
| :--- | :--- |
| `AddResilienceExceptionHandler(Action<ResilienceExceptionHandlerOptions>? configure = null)` | Registers the handler. The parameterless `UseExceptionHandler()` overload requires `AddProblemDetails()` as well. |

`ResilienceExceptionHandlerOptions` is a `sealed class`:

| Property | Default | Description |
| :--- | :--- | :--- |
| `TimeoutStatusCode` | `504` | The status for `DeadlineExceededException` and `AttemptTimeoutException`. |
| `RejectedStatusCode` | `503` | The status for `CallRejectedException`, with `Retry-After` when the rejection carried a hint. |
| `RateLimitedStatusCode` | `503` | The status for `RateLimitedException`. Not 429: the refusal is self-imposed. Set it to 429 when the limiter is per-caller quota. |
| `IncludeAttemptDetails` | `false` | Whether the body carries the attempt count and elapsed time. Off by default; see the [caution](../http/error-responses.md#read-the-response). |

Status codes are validated at startup; a value outside 100-599 fails registration rather than the first request. See [Error responses](../http/error-responses.md) for what the handler produces.

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
`Preset`, `Name`, `Attempts`, `Deadline`, `AttemptTimeout`, `UseAmbientDeadline`, `TransientBaseDelay`, `ThrottledBaseDelay`, `MaxDelay`, `BackoffFactor`, `Jitter`, `BudgetFraction`, `BudgetMinimumPerSecond`, `SharedBudget`, `Breaker`, `Hedge`, `Telemetry`, `Logging`.

- **`ToPolicy(Resilience? baseline = null)`**: Projects the options onto a `Resilience` record. It applies the preset first, then overrides properties that are not null. This method does not perform validation; validation occurs at registration or execution.
- **Budget Disabling**: Setting `BudgetFraction = 0` disables the retry budget.
- **`Logging`**: A string of `"Off"`, `"Default"`, or `"Verbose"` (case-insensitive). A string is used instead of an enum so that typos name the valid values (similar to `Preset`). Values outside this set fail at registration.

For more information on the configuration structure, see [Configuration](../di/configuration.md).

## `HedgeOptions`

`HedgeOptions` provides the bindable shape of [`Hedge`](../features/hedging.md). The presence of the section is what turns hedging on, and every property has a working default - so `"Hedge": {}` is a complete configuration.

| Property | Default | Description |
| :--- | :--- | :--- |
| `Quantile` | `0.95` | The quantile of recent latency a hedge fires at. Also the extra load: 0.95 costs about 5%. |
| `MaxConcurrent` | `2` | How many attempts may be in flight at once, counting the first. |
| `MinimumSamples` | `20` | How many recent calls the latency estimate needs before any hedge fires. |
| `MinimumDelay` | `10 ms` | A floor under the hedge delay. |
| `Window` | `30 s` | How much history the latency estimate covers. |

There is deliberately no fixed-delay setting. A constant threshold is the failure mode the adaptive one exists to avoid, and it would be one JSON key away if it existed at all.

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

Set exactly one of `PermitsPerSecond`, `Permits` with `Window`, `Concurrency`, or `Adaptive`. Anything else is a `ResilienceConfigurationException` listing every problem at once.

| Property | Default | Description |
| :--- | :--- | :--- |
| `PermitsPerSecond` | `null` | Calls allowed per second, with one second of burst. |
| `Permits` | `null` | Calls allowed per `Window`. |
| `Window` | `null` | The window `Permits` applies to. Slides in eight segments. |
| `Concurrency` | `null` | Calls allowed in flight at once - the bulkhead. |
| `Adaptive` | `null` | A concurrency limit discovered from latency. The section's presence turns it on; every property inside has a default. |
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
| `Limit.Adaptive(AdaptiveLimitOptions, int, string?, TimeProvider?)` | A concurrency limit discovered from latency. Returns an `AdaptiveLimiter`. |
| `RateLimiter.AcquireOrThrowAsync(...)` | Acquires one permit, or throws `RateLimitedException` carrying the limiter's own hint. |
| `PartitionedRateLimiter<TKey>.AcquireOrThrowAsync(...)` | The same, for one partition. |

Call `AcquireOrThrowAsync` inside the callback you hand to `RunAsync`, so the permit is taken once per attempt.

## `AdaptiveLimitOptions`

The range an [adaptive concurrency limit](../features/rate-limiting.md) may move within, and how fast it may move. Every property has a working default.

| Property | Default | Description |
| :--- | :--- | :--- |
| `Initial` | `20` | Where the limit starts, before there is anything to measure. |
| `Minimum` | `4` | The floor. A liveness guarantee: without one, a persistently slow dependency drives the limit to zero and the recovery is never sampled. |
| `Maximum` | `200` | The ceiling. What bounds the damage when the baseline is measured wrong. |
| `Threshold` | `2.0` | How many times the baseline latency counts as queueing. Must be greater than 1. |
| `DecreaseFactor` | `0.9` | What the limit is multiplied by on a congested round. Strictly between 0 and 1. |

| Member | Description |
| :--- | :--- |
| `Validate()` | Throws `ResilienceConfigurationException` listing every problem at once. |

## `AdaptiveLimiter`

A `RateLimiter`, so it composes everywhere the other three do. These members exist so a dashboard can read what was discovered.

| Member | Description |
| :--- | :--- |
| `CurrentLimit` | The permit count the loop has settled on. |
| `Baseline` | What a fast call to this dependency recently looked like, or `null` while the estimate is cold. |
| `InFlight` | Permits currently held. |
| `GetStatistics()` | Available permits, queued count, and the running lease totals. |

## `AddResilience` on `IHealthChecksBuilder`

| Overload | Description |
| :--- | :--- |
| `AddResilience(string name = "resilience", Action<ResilienceHealthOptions>? configure = null, IEnumerable<string>? tags = null)` | Registers a health check reporting every breaker's state and every retry budget's utilization. Validates the options eagerly, so a bad threshold fails at startup rather than on the first probe. |

`ResilienceHealthChecksBuilderExtensions.DefaultName` is the name used when none is given. See [Health checks](../di/health-checks.md).

## `ResilienceHealthOptions`

| Member | Default | Description |
| :--- | :--- | :--- |
| `BreakerOpenStatus` | `Degraded` | `Degraded` instead of `Unhealthy`. An open breaker indicates a dependency is down and the process is shedding load correctly. |
| `BudgetExhaustedStatus` | `Degraded` | What a retry budget at or above `BudgetThreshold` reports. |
| `BudgetThreshold` | `0.9` | The utilization at which a budget counts as exhausted, from just above 0 to 1. |
| `IncludeHttpClients` | `true` | Whether the per-host breakers and budgets held by clients registered with `AddResilience()` are included. |
| `Watch(string name, Breaker breaker)` | - | Also report a breaker the container does not own, such as one in a `static readonly` field. Returns these options. |
| `Watch(string name, RetryBudget budget)` | - | Also report a retry budget the container does not own. Returns these options. |
| `Validate()` | - | Throws `ResilienceConfigurationException` listing every problem at once. |

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

## `ResilienceLogging`

The `ResilienceLogging` static class holds the log listener and category derivation.

| Member | Description |
| :--- | :--- |
| `CategoryPrefix` | The prefix every category starts with: `"NResilience"`. |
| `CategoryFor(string? policyName)` | The category a policy logs under based on its name. If the name is null or empty, it uses `NResilience`; otherwise, it uses `NResilience.<name>`. |
| `Listener(ILogger logger, ResilienceLoggingOptions? options = null, TimeProvider? time = null)` | An `Action<CallEvent>` that writes to the logger. This listener is stateful due to rejection suppression, so create one per policy. |
| `WithLogging(this Resilience policy, ILogger logger, ResilienceLoggingOptions? options = null)` | Chains a listener onto the policy, or returns it unchanged when one is already attached. |
| `WithLogging(this Resilience policy, ILoggerFactory loggerFactory, ResilienceLoggingOptions? options = null)` | The same, but creates the logger under the policy's own category. |

At most one log listener attaches per policy. The first listener attached takes precedence.

## `ResilienceLoggingOptions`

| Member | Default | Description |
| :--- | :--- | :--- |
| `Profile` | `Default` | The level at which each record is emitted: `Off`, `Default`, or `Verbose`. |
| `RepeatWindow` | 30 seconds | How often a repeated rejection may warn. `TimeSpan.Zero` warns every time. |
| `IncludeStackTracesOnRetry` | `false` | Attaches the exception object to per-attempt and retry records, not only terminal ones. |
| `Level` | `null` | `Func<EventId, CallEvent, LogLevel?>`. Returns the level for one record, `null` to keep the profile's, or `LogLevel.None` to drop it. |

Because `ResilienceLogProfile.Off` is the enum's zero value, an unset profile is silent.

For the event IDs and the filter, see [Logging in DI](../di/logging.md).
