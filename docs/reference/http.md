---
title: HTTP reference
description: Reference for ResilienceHandler, HttpResilienceOptions, and the ResilienceHttp utility class.
order: 9
---

# HTTP reference

The HTTP components are located in the `NResilience.Http` namespace within the `NResilience` package.

## `ResilienceHandler`

`ResilienceHandler` is a `sealed class` that inherits from `DelegatingHandler`. It manages the execution of resilience policies specifically for HTTP requests.

| Member | Description |
| :--- | :--- |
| `ResilienceHandler(Resilience? policy = null, HttpResilienceOptions? options = null)` | Creates a handler where the inner handler is assigned later (e.g., by a client factory). |
| `ResilienceHandler(HttpMessageHandler innerHandler, Resilience? policy = null, HttpResilienceOptions? options = null)` | Creates a handler that wraps a specific transport handler. |
| `Policy` | The policy executed by the handler, before per-host scoping is applied. Defaults to `Resilience.Http`. |
| `Options` | The `HttpResilienceOptions` used to configure the handler. |
| `BreakersByHost()` | Returns a snapshot of the circuit breakers currently managed by the handler, keyed by host. |
| `BudgetsByHost()` | Returns a snapshot of the retry budgets currently managed by the handler, keyed by host. |
| `WillRetry(HttpRequestMessage)` | Determines if a request would be retried based on whether the policy allows multiple attempts and whether the request is repeatable. |

Both constructors validate the provided policy. The synchronous `Send` method is not supported and throws a `NotSupportedException`.

## `HttpResilienceOptions`

`HttpResilienceOptions` is a `sealed class` used to configure the `ResilienceHandler`. It is mutable to allow configuration via options callbacks.

| Property | Default | Description |
| :--- | :--- | :--- |
| `RetryUnsafeMethods` | `false` | Determines whether `POST` and `PATCH` methods are retried. |
| `OwnTransportTimeout` | `true` | Whether the client's `Timeout` is set to `Timeout.InfiniteTimeSpan`. Honored by whoever builds the client. |
| `BreakerPerHost` | `true` | Enables per-host circuit breakers. If the policy already carries an explicit `Breaker`, that breaker is used instead. |
| `BreakerSettings` | `null` | The settings used to create per-host breakers. |
| `BudgetPerHost` | `true` | Enables per-host retry budgets. An explicit `Budget` (including `RetryBudget.None`) takes precedence. `RetryBudget.Automatic` does not specify a scope, so per-host scoping applies. |
| `DetectNestedRetries` | `true` | Determines whether the nested-retry header is added to requests and whether nesting is reported. |

## `ResilienceHttp`

`ResilienceHttp` is a `static class` providing utility methods and constants for HTTP resilience.

| Member | Description |
| :--- | :--- |
| `CreateClient(policy = null, options = null, innerHandler = null)` | Creates an `HttpClient` with a `ResilienceHandler` in its pipeline. Disposing the client also disposes the handler chain. |
| `Repeatable` | An `HttpRequestOptionsKey<bool>` used to override the idempotency decision for a specific request. |
| `NestedRetryHeader` | The constant value for the nested-retry header: `"X-NResilience-Retrying"`. |

### Default retryable methods
The handler retries the following methods by default: `GET`, `HEAD`, `PUT`, `DELETE`, `OPTIONS`, and `TRACE`. 

The following are not retried unless configured otherwise: `POST`, `PATCH`, and any HTTP method not recognized by the library.
