---
title: HTTP reference
description: Reference for HttpResilienceHandler, HttpResilienceOptions, and the HttpResilience utility class.
order: 9
---

# HTTP reference

The HTTP components live in the `NResilience` namespace in the `NResilience` package.

## `HttpResilienceHandler`

`HttpResilienceHandler` is a `sealed class` deriving from `DelegatingHandler`. It runs resilience policies around HTTP requests.

| Member | Description |
| :--- | :--- |
| `HttpResilienceHandler(Resilience? policy = null, HttpResilienceOptions? options = null)` | Creates a handler where the inner handler is assigned later (e.g., by a client factory). |
| `HttpResilienceHandler(HttpMessageHandler innerHandler, Resilience? policy = null, HttpResilienceOptions? options = null)` | Creates a handler that wraps a specific transport handler. |
| `Policy` | The policy executed by the handler, before per-host scoping is applied. Defaults to `Resilience.Http`. |
| `Options` | The `HttpResilienceOptions` used to configure the handler. |
| `BreakersByHost()` | Returns a snapshot of the circuit breakers currently managed by the handler, keyed by host. |
| `BudgetsByHost()` | Returns a snapshot of the retry budgets currently managed by the handler, keyed by host. |
| `WillRetry(HttpRequestMessage)` | Says whether a request would be retried, based on whether the policy allows multiple attempts and whether the request is repeatable. |

Both constructors validate the provided policy. The synchronous `Send` method is not supported and throws a `NotSupportedException`.

## `HttpResilienceOptions`

`HttpResilienceOptions` is a `sealed class` used to configure the `HttpResilienceHandler`. It is mutable to allow configuration via options callbacks.

| Property | Default | Description |
| :--- | :--- | :--- |
| `RetryUnsafeMethods` | `false` | Whether `POST` and `PATCH` methods are retried. |
| `OwnTransportTimeout` | `true` | Whether the client's `Timeout` is set to `Timeout.InfiniteTimeSpan`. Honored by whoever builds the client. |
| `BreakerPerHost` | `true` | Enables per-host circuit breakers. If the policy already carries an explicit `Breaker`, that breaker is used instead. |
| `BreakerSettings` | `null` | The settings used to create per-host breakers. |
| `BudgetPerHost` | `true` | Enables per-host retry budgets. An explicit `Budget` (including `RetryBudget.None`) takes precedence. `RetryBudget.Automatic` does not specify a scope, so per-host scoping applies. |
| `MaximumHosts` | `1024` | The number of hosts the per-host registry keeps. At least 1; the least-recently-seen hosts are dropped past the cap. There is no unbounded mode - `int.MaxValue` is as close as it gets. |
| `DetectNestedRetries` | `true` | Whether the nested-retry header is added to requests and whether nesting is reported. |
| `BufferResponses` | `false` | Whether the response body is read inside the attempt, so a stalled or broken body is retried. Costs the body in memory. |
| `PropagateDeadline` | `false` | Whether each attempt carries the time this side will wait for it: `min(AttemptTimeout, time left on the deadline)`, in whole milliseconds, recomputed per attempt and per hedged leg. The gRPC switch of the same name defaults to `true`, because `grpc-timeout` is a protocol field rather than a convention. |
| `DeadlineHeader` | `"X-Deadline-Ms"` | The header `PropagateDeadline` writes. `AmbientDeadline.Header` is the same value, and is what the inbound middleware reads. Must not be empty. |
| `Quota` | `Quota.Reserving(0.1)` | The reserve to keep against the allowance the dependency publishes, or `null` to read no rate-limit headers. See [`Quota`](#quota). |

| Method | Description |
| :--- | :--- |
| `Validate()` | Throws `ResilienceConfigurationException` listing every problem at once. `HttpResilienceHandler`'s constructor calls it beside the policy's own `Validate()`, so a bad header name, bad `BreakerSettings` or bad `Quota` fails there rather than from the middle of a request. `MaximumHosts` below 1 is a problem it reports. |
| `Validated()` | Runs `Validate()` and returns the options, so a bad configuration throws where it is written. |

## `Quota`

`Quota` is a `sealed record` holding the reserve to keep against the allowance the dependency publishes. It is `HttpResilienceOptions.Quota`, and it is on by default. See [Honor the allowance the dependency publishes](../http/index.md#honor-the-allowance-the-dependency-publishes).

| Member | Default | Description |
| :--- | :--- | :--- |
| `Reserve` | `0.1` | The fraction of the published allowance to leave unspent. At least 0 and less than 1. At `0`, the handler refuses only once the dependency says nothing is left. |
| `LegacyHeaders` | `null` | The header triple to read when the standard fields are absent, in the order limit, remaining, reset. `null` means `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`. Exactly three names, or none. |
| `Reserving(reserve = 0.1)` | - | Static. The way to configure a reserve. |
| `ToString()` | - | The reserve, as text. |

The handler reads two shapes off every response and keeps the numbers per host. The standard fields - `RateLimit-Policy` carrying the quota `q` and window `w`, and `RateLimit` carrying the remaining allowance `r` and the seconds until reset `t` - win where both shapes are present, and the most constraining member of `RateLimit` is the one that binds. The legacy triple's reset is whole seconds, read as a Unix timestamp when it is too large to be a count from now.

A refusal is `Verdict.Refused`, carrying the time until the window resets as the pushback: retried on the long backoff curve, never charged to the retry budget, never evidence against the host's breaker, and reported as the [`RejectedByQuota`](events.md#calleventkind) event and `RateLimitedException`. A host that publishes nothing, a window that has already reset, and a malformed field all leave the guard with no opinion.

## `HttpResilience`

`HttpResilience` is a `static class` providing utility methods and constants for HTTP resilience. The two headers the integration reads and writes are `AmbientDeadline.Header` and [`NestedRetry.Header`](resilience.md#nestedretry), each declared beside the ambient value it carries.

| Member | Description |
| :--- | :--- |
| `CreateClient(policy = null, options = null, innerHandler = null)` | Creates an `HttpClient` with an `HttpResilienceHandler` in its pipeline. Disposing the client also disposes the handler chain. |
| `Repeatable` | An `HttpRequestOptionsKey<bool>` used to override the idempotency decision for a specific request. |

## `HttpRequestExtensions`

`HttpRequestExtensions` is a `static class` of per-request helpers over `HttpResilience`'s option keys. Both return the same request, so they compose in an initializer. See [Idempotency](../http/idempotency.md#mark-a-request-as-repeatable).

| Member | Description |
| :--- | :--- |
| `MarkRepeatable(idempotencyKey = null, headerName = "Idempotency-Key")` | Sets `HttpResilience.Repeatable` to `true` and stamps the idempotency key header when a key is supplied. An existing header of that name is left alone. |
| `MarkSingleShot()` | Sets `HttpResilience.Repeatable` to `false`, so the request is sent at most once whatever its method and whatever `RetryUnsafeMethods` says. |

### Default retryable methods
The handler retries the following methods by default: `GET`, `HEAD`, `PUT`, `DELETE`, `OPTIONS`, and `TRACE`. 

The following are not retried unless configured otherwise: `POST`, `PATCH`, and any HTTP method not recognized by the library.
