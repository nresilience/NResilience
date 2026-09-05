---
title: HTTP reference
description: Reference for ResilienceHandler, HttpResilienceOptions, and the HttpResilience utility class.
order: 9
---

# HTTP reference

The HTTP components live in the `NResilience` namespace in the `NResilience` package.

## `ResilienceHandler`

`ResilienceHandler` is a `sealed class` deriving from `DelegatingHandler`. It runs resilience policies around HTTP requests.

| Member | Description |
| :--- | :--- |
| `ResilienceHandler(Resilience? policy = null, HttpResilienceOptions? options = null)` | Creates a handler where the inner handler is assigned later (e.g., by a client factory). |
| `ResilienceHandler(HttpMessageHandler innerHandler, Resilience? policy = null, HttpResilienceOptions? options = null)` | Creates a handler that wraps a specific transport handler. |
| `Policy` | The policy executed by the handler, before per-host scoping is applied. Defaults to `Resilience.Http`. |
| `Options` | The `HttpResilienceOptions` used to configure the handler. |
| `BreakersByHost()` | Returns a snapshot of the circuit breakers currently managed by the handler, keyed by host. |
| `BudgetsByHost()` | Returns a snapshot of the retry budgets currently managed by the handler, keyed by host. |
| `WillRetry(HttpRequestMessage)` | Says whether a request would be retried, based on whether the policy allows multiple attempts and whether the request is repeatable. |

Both constructors validate the provided policy. The synchronous `Send` method is not supported and throws a `NotSupportedException`.

## `HttpResilienceOptions`

`HttpResilienceOptions` is a `sealed class` used to configure the `ResilienceHandler`. It is mutable to allow configuration via options callbacks.

| Property | Default | Description |
| :--- | :--- | :--- |
| `RetryUnsafeMethods` | `false` | Whether `POST` and `PATCH` methods are retried. |
| `OwnTransportTimeout` | `true` | Whether the client's `Timeout` is set to `Timeout.InfiniteTimeSpan`. Honored by whoever builds the client. |
| `BreakerPerHost` | `true` | Enables per-host circuit breakers. If the policy already carries an explicit `Breaker`, that breaker is used instead. |
| `BreakerSettings` | `null` | The settings used to create per-host breakers. |
| `BudgetPerHost` | `true` | Enables per-host retry budgets. An explicit `Budget` (including `RetryBudget.None`) takes precedence. `RetryBudget.Automatic` does not specify a scope, so per-host scoping applies. |
| `MaximumHosts` | `1024` | The number of hosts the per-host registry keeps. `null` is unbounded; the least-recently-seen hosts are dropped past the cap. |
| `DetectNestedRetries` | `true` | Whether the nested-retry header is added to requests and whether nesting is reported. |
| `PropagateDeadline` | `false` | Whether each attempt carries the time this side will wait for it: `min(AttemptTimeout, time left on the deadline)`, in whole milliseconds, recomputed per attempt and per hedged leg. |
| `DeadlineHeader` | `"X-Deadline-Ms"` | The header `PropagateDeadline` writes. `ResilienceDeadline.Header` is the same value, and is what the inbound middleware reads. Must not be empty. |

| Method | Description |
| :--- | :--- |
| `Validate()` | Throws `ResilienceConfigurationException` listing every problem at once. `ResilienceHandler`'s constructor calls it beside the policy's own `Validate()`, so a bad header name or bad `BreakerSettings` fails there rather than from the middle of a request. `MaximumHosts` is not checked: zero or less is documented as unbounded rather than as a mistake. |
| `Validated()` | Runs `Validate()` and returns the options, so a bad configuration throws where it is written. |

## `HttpResilience`

`HttpResilience` is a `static class` providing utility methods and constants for HTTP resilience.

| Member | Description |
| :--- | :--- |
| `CreateClient(policy = null, options = null, innerHandler = null)` | Creates an `HttpClient` with a `ResilienceHandler` in its pipeline. Disposing the client also disposes the handler chain. |
| `Repeatable` | An `HttpRequestOptionsKey<bool>` used to override the idempotency decision for a specific request. |
| `NestedRetryHeader` | The constant value for the nested-retry header: `"X-NResilience-Retrying"`. |

## `ResilienceHttpRequestExtensions`

`ResilienceHttpRequestExtensions` is a `static class` of per-request helpers over `HttpResilience`'s option keys. Both return the same request, so they compose in an initializer. See [Idempotency](../http/idempotency.md#mark-a-request-as-repeatable).

| Member | Description |
| :--- | :--- |
| `MarkRepeatable(idempotencyKey = null, headerName = "Idempotency-Key")` | Sets `HttpResilience.Repeatable` to `true` and stamps the idempotency key header when a key is supplied. An existing header of that name is left alone. |
| `MarkSingleShot()` | Sets `HttpResilience.Repeatable` to `false`, so the request is sent at most once whatever its method and whatever `RetryUnsafeMethods` says. |

### Default retryable methods
The handler retries the following methods by default: `GET`, `HEAD`, `PUT`, `DELETE`, `OPTIONS`, and `TRACE`. 

The following are not retried unless configured otherwise: `POST`, `PATCH`, and any HTTP method not recognized by the library.
