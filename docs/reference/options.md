---
title: Options and registration
description: Reference for ResilienceOptions, BreakerOptions, IResiliencePolicies, and the AddResilience registration methods.
order: 10
---

# Options and registration

Registration methods live in the `NResilience.Extensions` package as extension methods for `IServiceCollection` and `IHttpClientBuilder`.

## On this page

**Registering a policy**

| | |
| :--- | :--- |
| [`AddResilience` on `IServiceCollection`](#addresilience-on-iservicecollection) | Register a named policy from a value, a callback, or a section. |
| [`AddResilience` on `IHttpClientBuilder`](#addresilience-on-ihttpclientbuilder) | Put the handler on an `HttpClient`. |
| [`IResiliencePolicies`](#iresiliencepolicies) | Resolve a registered policy by name. |

**The bindable shape of a policy.** One section per feature, each with an `Enabled` switch:

| Section | Configures | JSON key |
| :--- | :--- | :--- |
| [`ResilienceOptions`](#resilienceoptions) | The policy itself | *(the policy's own section)* |
| [`BackoffOptions`](#backoffoptions) | The retry delay curve | `Backoff` |
| [`MeasuredBaseOptions`](#measuredbaseoptions) | Measuring that curve's base from latency | `Backoff:MeasuredBase` |
| [`BudgetOptions`](#budgetoptions) | The retry budget | `Budget` |
| [`AttemptCeilingOptions`](#attemptceilingoptions) | Measuring the per-attempt ceiling | `AttemptCeiling` |
| [`BreakerOptions`](#breakeroptions) | The circuit breaker | `Breaker` |
| `FailuresOptions` | Its relative failure trip - keys mirror [`Failures`](breaker.md#failures) | `Breaker:Failures` |
| `SlowCallsOptions` | Its relative brownout trip - keys mirror [`SlowCalls`](breaker.md#slowcalls) | `Breaker:SlowCalls` |
| `RecoveryOptions` | Its recovery ramp - keys mirror [`Recovery`](breaker.md#recovery) | `Breaker:Recovery` |
| [`HedgeOptions`](#hedgeoptions) | Hedging | `Hedge` |
| [`WinRateOptions`](#winrateoptions) | Holding hedges back when they stop winning | `Hedge:WinRate` |

**Limiting, health and observability**

| | |
| :--- | :--- |
| [`AddRateLimit`](#addratelimit-on-ihttpclientbuilder) and [`RateLimitOptions`](#ratelimitoptions) | Rate and concurrency limits. |
| [`Limit`](#limit-and-acquireorthrowasync), [`AdaptiveLimitOptions`](#adaptivelimitoptions), [`AdaptiveLimiter`](#adaptivelimiter) | Building a limiter, including the adaptive one. |
| [`AddResilience` on `IHealthChecksBuilder`](#addresilience-on-ihealthchecksbuilder) and [`ResilienceHealthOptions`](#resiliencehealthoptions) | Health reporting. |
| [`ResilienceTelemetry`](#resiliencetelemetry), [`ResilienceLogging`](#resiliencelogging), [`ResilienceLoggingOptions`](#resilienceloggingoptions) | Metrics and logs. |

**ASP.NET Core middleware**

| | |
| :--- | :--- |
| [`UseResilienceDeadline`](#useresiliencedeadline-on-iapplicationbuilder) | Read an inbound deadline and publish it. |
| [`UseResilienceNestedRetry`](#useresiliencenestedretry-on-iapplicationbuilder) | Read the nested-retry marker. |
| [`AddResilienceExceptionHandler`](#addresilienceexceptionhandler-on-iservicecollection) | Map the library's exceptions to responses. |

## `AddResilience` on `IServiceCollection`

Register resilience policies in the DI container with these methods.

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

Add the `ResilienceHandler` to an `HttpClient` pipeline with these methods.

| Overload | Description |
| :--- | :--- |
| `AddResilience(Resilience? policy = null, Action<HttpResilienceOptions>? configureOptions = null, bool telemetry = true, ResilienceLogProfile? logging = null)` | Adds the handler using the provided policy value, defaulting to `Resilience.Http`. |
| `AddResilience(string policyName, Action<HttpResilienceOptions>? configureOptions = null, bool telemetry = true, ResilienceLogProfile? logging = null)` | Adds the handler using a registered policy, which is resolved when the handler chain is built. |

If `logging` is `null`, the process default is used. Registered policies log under their registration's own profile, so this parameter only affects policies that the registration left unlogged.

If the policy has no name of its own, it is named after the client. That keeps multiple clients using `Resilience.Http` from all reporting under the same name in telemetry.

## `UseResilienceDeadline` on `IApplicationBuilder`

`UseResilienceDeadline` is in the `NResilience.AspNetCore` package, kept separate because it is the only part of NResilience that requires ASP.NET Core. It reads the deadline a caller sent and publishes it for the rest of the request, so every policy with `UseAmbientDeadline` set is bounded by `min(its own deadline, the time the caller is still waiting)`.

| Overload | Description |
| :--- | :--- |
| `UseResilienceDeadline(Action<ResilienceDeadlineOptions>? configure = null)` | Adds the middleware. Register it before anything that makes an outbound call. |

`ResilienceDeadlineOptions` is a `sealed class`:

| Property | Default | Description |
| :--- | :--- | :--- |
| `Header` | `"X-Deadline-Ms"` | The header carrying whole milliseconds left. |
| `Maximum` | `null` | The longest inbound deadline this service believes. A header above it is ignored. `null` believes any of them. |
| `Reserve` | `TimeSpan.Zero` | How much of the inbound deadline is kept back for this service's own work, and therefore withheld from outbound calls. |

The clock is `TimeProvider` from the container when one is registered, `TimeProvider.System` otherwise. An expired inbound deadline does not fail the request; it fails the outbound calls. [Deadline propagation](../features/deadlines.md#propagate-the-deadline-across-a-hop) explains that distinction.

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
| `DeadlineStatusCode` | `504` | The status for `DeadlineExceededException`. |
| `AttemptTimeoutStatusCode` | `504` | The status for `AttemptTimeoutException`. |
| `RejectedStatusCode` | `503` | The status for `CallRejectedException`, with `Retry-After` when the rejection carried a hint. |
| `RateLimitedStatusCode` | `503` | The status for `RateLimitedException`. Not 429: the refusal is self-imposed. Set it to 429 when the limiter is per-caller quota. |
| `IncludeAttemptDetails` | `false` | Whether the body carries the attempt count and elapsed time. Off by default; see the [caution](../http/error-responses.md#read-the-response). |

Status codes are validated at startup; a value outside 100-599 fails registration rather than the first request. See [Error responses](../http/error-responses.md) for what the handler produces.

## `IResiliencePolicies`

The `IResiliencePolicies` service gives access to registered policies.

| Member | Description |
| :--- | :--- |
| `this[string name]` | Returns the current policy for the specified name. Throws a `ResilienceConfigurationException` if the name is not registered. |
| `Names` | A collection of all registered policy names. |
| `TryGet(name, out policy)` | A non-throwing method to retrieve a policy. Returns `Resilience.Default` if no policy is found. |

**Recommendation**: Resolve policies per call. Capturing one at construction creates a snapshot that misses configuration reloads.

## `ResilienceOptions`

`ResilienceOptions` is a `sealed class` for binding configuration to a policy. All properties are nullable; `null` means "leave this property alone". An unrecognized key is an error, not a no-op - see [An unrecognized key is an error](../di/configuration.md#an-unrecognized-key-is-an-error).

**Properties**: the policy's own scalars - `Preset`, `Name`, `Attempts`, `Deadline`, `AttemptTimeout`, `UseAmbientDeadline`, `Adaptive`, `Telemetry`, `Logging` - and one section per optional feature: `Backoff`, `Budget`, `AttemptCeiling`, `Breaker`, `Hedge`. `Backoff` carries a `MeasuredBase` subsection of its own.

- **`ToPolicy(Resilience? baseline = null)`**: Projects the options onto a `Resilience` record. It applies the preset first, then overrides properties that are not null. No validation happens here; that occurs at registration or execution.
- **`Logging`**: A string of `"Off"`, `"Default"`, or `"Verbose"` (case-insensitive). A string rather than an enum, so a typo names the valid values (like `Preset`). Anything outside the set fails at registration.
- **`Deadline`, `AttemptTimeout`**: Use `"Infinite"` for no bound (`"None"` and `"Unbounded"` are the same word, case-insensitive). The duration `Timeout.InfiniteTimeSpan` round-trips as - `"-00:00:00.0010000"` - still binds too. Any other word fails at registration rather than leaving the call quietly unbounded.

### Every section has an `Enabled`

`Budget`, `AttemptCeiling`, `Breaker`, `Hedge`, and the `Failures`, `SlowCalls` and `Recovery` subsections
of `Breaker` each take a nullable `bool Enabled`:

| Value | Meaning |
| :--- | :--- |
| unset | The section means what its presence has always meant: an opt-in feature turns on, an on-by-default feature is only tuned. |
| `false` | The feature is off, whatever else the section says. |
| `true` | Explicitly on. A no-op except on `Budget`, where it turns one on at the defaults. |

`Enabled` is the only way a later configuration layer can remove a feature an earlier one added,
because providers merge sections and never delete a key. It replaces the per-feature magic numbers
that used to stand in for the `null` a section cannot say: `"Multiple": 0`, `"Fraction": 0` and
`"BudgetFraction": 0` now fail at registration with a message naming `"Enabled": false`.

`Backoff` has no `Enabled`, because a policy always has a backoff curve.

## `BackoffOptions`

`BackoffOptions` provides the bindable shape of [`Backoff`](backoff.md), with the same property names. A section that mentions some of the knobs patches the curve the base policy already carried; anything it does not mention keeps that policy's value.

| Property | Default | Description |
| :--- | :--- | :--- |
| `TransientBase` | `200 ms` | The first delay after a `Transient` failure. |
| `ThrottledBase` | `2 s` | The first delay after a `Throttled` failure, which starts higher because the dependency has said so. |
| `MaximumDelay` | `30 s` | The ceiling on any single backoff delay. |
| `Factor` | `2` | The multiplier applied per attempt. `1` makes the backoff constant. |
| `Jitter` | `Full` | How much of the computed delay is randomized. |

`ToPolicy` patches the base policy's `Backoff` with whatever the section named. `Jitter` on its own is a modifier rather than a reason to rebuild, so a section naming only `Jitter` leaves a `Constant` curve constant. A non-exponential baseline whose section sets a curve knob gets a fresh exponential built on the shipped defaults.

## `BudgetOptions`

`BudgetOptions` provides the bindable shape of a [`RetryBudget`](retry-budget.md).

| Property | Default | Description |
| :--- | :--- | :--- |
| `Enabled` | `null` | `false` is `RetryBudget.None`; `true` turns one on at the defaults. |
| `Fraction` | `0.1` | Retries may add at most this much on top of successful traffic. |
| `MinimumPerSecond` | `3` | The floor, in retries per second, below which the fraction does not apply - so a quiet service can still retry at all. |
| `Shared` | `null` | Names a shared budget, so several policies throttle against one pool. Null gives this policy its own. |

`ToPolicy` leaves the base policy's `Budget` alone when the section named nothing. A private budget adopts the policy's `Time`; a shared one does not, because it is process-wide and the first caller's parameters win.

For more information on the configuration structure, see [Configuration](../di/configuration.md).

## `HedgeOptions`

`HedgeOptions` provides the bindable shape of [`Hedge`](../features/hedging.md). The presence of the section is what turns hedging on, and every property has a working default - so `"Hedge": {}` is a complete configuration.

| Property | Default | Description |
| :--- | :--- | :--- |
| `Quantile` | `0.95` | The quantile of recent latency a hedge fires at. Also the extra load: 0.95 costs about 5%. |
| `MaximumConcurrent` | `2` | How many attempts may be in flight at once, counting the first. |
| `MinimumSamples` | `20` | How many recent calls the latency estimate needs before any hedge fires. |
| `MinimumDelay` | `10 ms` | A floor under the hedge delay. |
| `Window` | `30 s` | How much history the latency estimate covers. |
| `Enabled` | `null` | `false` turns hedging off, which is how a later configuration layer takes back a hedge an earlier one added. |
| `SuppressAt` | `0.5` | The fraction of the breaker's trip point at which hedging stops. `1` is the top of the range - suppress only at the trip point itself. |
| `WinRate` | `null` | A [`WinRateOptions`](#winrateoptions) subsection, which holds hedges back once they stop winning. Off unless named. |

There is deliberately no fixed-delay setting. A constant threshold is the failure mode the adaptive one exists to avoid, and it would be one JSON key away if it existed at all.

## `WinRateOptions`

`WinRateOptions` provides the bindable shape of [`WinRate`](../features/hedging.md#stop-hedging-when-hedging-stops-helping). It is a subsection of `Hedge`, and it is off unless the section asks for it.

| Property | Default | Description |
| :--- | :--- | :--- |
| `Enabled` | `null` | `false` drops a loop the base policy carried. |
| `Floor` | `0.2` | The fraction of hedges that has to win. Must be in `(0, 1)`. |
| `Window` | `1 min` | How much history the win rate covers. A quarter of it is one decision. |
| `MinimumSamples` | `10` | How many hedges the window needs before the loop has an opinion. |
| `MinimumAllowance` | `0.05` | The least hedging the loop retreats to. `0` is no floor at all. Must be less than 1. |

Opt-in, unlike the rest of `HedgeOptions`: it is a control loop over a control loop, and its failure mode is that the dependency whose tail no second attempt can route around is exactly the one it retreats from.

## `AttemptCeilingOptions`

`AttemptCeilingOptions` provides the bindable shape of [`AttemptCeiling`](../features/deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it), which the default policy has on. Every property has a working default, so the section is only needed to change one - or to turn the feature off, which is `"AttemptCeiling": { "Enabled": false }`.

| Property | Default | Description |
| :--- | :--- | :--- |
| `Enabled` | `null` | `false` leaves `AttemptTimeout` as the only per-attempt bound. |
| `Multiple` | `3` | How many times the measured quantile an attempt may take. Must be greater than 1. |
| `Quantile` | `0.95` | The quantile of recent successful latency the ceiling is measured from. Between 0.5 and 0.99. |
| `Window` | `5 min` | How much history the estimate covers. |
| `MinimumSamples` | `20` | How many recent successful calls the estimate needs before it bounds anything. |
| `Floor` | `50 ms` | A floor under the measured ceiling. |

There is deliberately no way to make the measured ceiling longer than `AttemptTimeout`. The clamp is what makes the feature safe to leave on, and a key that lifted it would be the one key nobody should have.

## `MeasuredBaseOptions`

`MeasuredBaseOptions` provides the bindable shape of [`MeasuredBase`](backoff.md#backoffbase), the measured [backoff base](../features/retry.md#measure-the-backoff-base-instead-of-guessing-it). It is a subsection of `Backoff`, and it is off unless the section asks for it.

| Property | Default | Description |
| :--- | :--- | :--- |
| `Enabled` | `null` | `false` drops a measured base the base policy carried. |
| `Multiple` | `1` | How many normal calls the first retry waits. Must be greater than zero. |
| `Quantile` | `0.5` | The quantile of recent successful latency that counts as normal. Must be in `(0, 0.5]`. |
| `Window` | `5 min` | How much history the baseline covers. |
| `MinimumSamples` | `20` | How many recent successful calls the baseline needs before it moves anything. |
| `Spread` | `10` | How far the measured base may move from `TransientBase`, as a factor in either direction. |

Naming this section rebuilds a `Constant` or `Custom` base curve into an exponential one, exactly as naming any other `Backoff` knob does - a measured base is only carried by an exponential curve.

## `BreakerOptions`

`BreakerOptions` provides the bindable shape of [`BreakerSettings`](breaker.md) with nullable properties.

`ToPolicy` builds a live `Breaker` instance, named after the policy. A configured breaker is created once per policy and survives configuration reloads, keeping its state.

`Enabled` is `false` for no breaker at all - the only way a later configuration layer can remove one an earlier layer added.

The two relative trips are on by default, as they are on `BreakerSettings`, and a subsection turns one off the same way `AttemptCeiling` does: `"SlowCalls": { "Enabled": false }` or `"Failures": { "Enabled": false }`. Setting `SlowCallThreshold` as well composes with `SlowCalls` rather than replacing it: a call is slow when it is above either threshold. `"Recovery": { "Enabled": false }` turns the ramp back off.

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
| `Multiple` | `2.0` | How many times the baseline latency counts as queueing. Must be greater than 1. |
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

`ResilienceTelemetry` is a `static class` exposing the library's instrumentation.

| Member | Description |
| :--- | :--- |
| `MeterName` | The name of the meter: `"NResilience"`. |
| `ActivitySourceName` | The name of the activity source: `"NResilience"`. |
| `Meter` | The `Meter` instance used to create all instruments. |
| `ActivitySource` | The `ActivitySource` used to provide spans for HTTP operations. |
| `Listener` | An `Action<CallEvent>` that records data to instruments. It is stateless and allocation-free. |
| `WithTelemetry(this Resilience policy)` | An extension method that chains the `Listener` onto the policy's `OnEvent` handler. This operation is idempotent. |

For a list of available instruments, see [Telemetry](../features/telemetry.md).

## `ResilienceLogging`

The `ResilienceLogging` static class holds the log listener and category derivation.

| Member | Description |
| :--- | :--- |
| `CategoryPrefix` | The prefix every category starts with: `"NResilience"`. |
| `CategoryFor(string? policyName)` | The category a policy logs under, based on its name: `NResilience` when the name is null or empty, otherwise `NResilience.<name>`. |
| `Listener(ILogger logger, ResilienceLoggingOptions? options = null, TimeProvider? time = null)` | An `Action<CallEvent>` that writes to the logger. Stateful due to rejection suppression, so create one per policy. |
| `WithLogging(this Resilience policy, ILogger logger, ResilienceLoggingOptions? options = null)` | Chains a listener onto the policy, or returns it unchanged when one is already attached. |
| `WithLogging(this Resilience policy, ILoggerFactory loggerFactory, ResilienceLoggingOptions? options = null)` | The same, but creates the logger under the policy's own category. |

At most one log listener attaches per policy; the first one attached wins.

## `ResilienceLoggingOptions`

| Member | Default | Description |
| :--- | :--- | :--- |
| `Profile` | `Default` | The level at which each record is emitted: `Off`, `Default`, or `Verbose`. |
| `RepeatWindow` | 30 seconds | How often a repeated rejection may warn. `TimeSpan.Zero` warns every time. |
| `IncludeStackTracesOnRetry` | `false` | Attaches the exception object to per-attempt and retry records, not only terminal ones. |
| `Level` | `null` | `Func<EventId, CallEvent, LogLevel?>`. Returns the level for one record, `null` to keep the profile's, or `LogLevel.None` to drop it. |

Because `ResilienceLogProfile.Off` is the enum's zero value, an unset profile is silent.

For the event IDs and the filter, see [Logging in DI](../di/logging.md).
