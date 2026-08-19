---
title: HTTP reference
description: ResilienceHandler, HttpResilienceOptions and ResilienceHttp.
order: 9
---

# HTTP reference

Namespace `NResilience.Http`, package `NResilience.Http`.

## `ResilienceHandler`

`sealed class ResilienceHandler : DelegatingHandler`.

| Member | Meaning |
| --- | --- |
| `ResilienceHandler(Resilience? policy = null, HttpResilienceOptions? options = null)` | A handler whose inner handler is assigned later, as a client factory does. |
| `ResilienceHandler(HttpMessageHandler innerHandler, Resilience? policy = null, HttpResilienceOptions? options = null)` | A handler in front of a transport. |
| `Policy` | The policy it runs, before per-host scoping. Defaults to `Resilience.Http`. |
| `Options` | The switches it was built with. |
| `BreakersByHost()` | A snapshot of the breakers, by host, for the hosts it has seen. |
| `BudgetsByHost()` | The same for the retry budgets. |
| `WillRetry(HttpRequestMessage)` | Whether that request would be retried: more than one attempt, and a repeatable request. |

Both constructors validate the policy. The synchronous `Send` throws `NotSupportedException`.

## `HttpResilienceOptions`

`sealed class`. Mutable, because it is what an options callback configures.

| Property | Default | Meaning |
| --- | --- | --- |
| `RetryUnsafeMethods` | `false` | Whether POST and PATCH are retried. |
| `OwnTransportTimeout` | `true` | Whether the client's `Timeout` is set to `Timeout.InfiniteTimeSpan`. Honored by whoever builds the client. |
| `BreakerPerHost` | `true` | One breaker per host. A policy carrying an explicit `Breaker` keeps it. |
| `BreakerSettings` | null | The settings per-host breakers are created with. |
| `BudgetPerHost` | `true` | One retry budget per host. An explicit `Budget` wins, including `None`. |
| `DetectNestedRetries` | `true` | Whether the nested-retry header is stamped and nesting reported. |

## `ResilienceHttp`

`static class`.

| Member | Meaning |
| --- | --- |
| `CreateClient(policy = null, options = null, innerHandler = null)` | An `HttpClient` with the handler in front of it, built the way the DI registration builds one. Disposing it disposes the chain. |
| `Repeatable` | `HttpRequestOptionsKey<bool>`. Per-request override of the idempotency decision; wins in both directions. |
| `NestedRetryHeader` | `"X-NResilience-Retrying"`. |

Retried methods: GET, HEAD, PUT, DELETE, OPTIONS, TRACE. Not retried: POST, PATCH, and any method the
library does not recognize.

