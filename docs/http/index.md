---
title: HTTP
description: Use the resilience handler to manage HTTP-specific constraints like request reuse, idempotency, and per-host scoping.
order: 4
---

# HTTP

A [policy](../getting-started/key-concepts.md#what-is-a-policy) manages retries, timeouts, and circuit breaking for any call. HTTP adds constraints a general policy cannot handle alone: an `HttpRequestMessage` can only be sent once, so a retry needs a fresh request; retrying a `POST` can duplicate orders or charges; and a circuit breaker should be scoped per host so one failing server does not block calls to healthy ones.

The `ResilienceHandler` is a `DelegatingHandler` that manages those HTTP-specific requirements. It ships in the core package - there is no separate HTTP install.

> [!IMPORTANT]
> `HttpClient.Timeout` defaults to 100 seconds and covers the entire request sequence - including all attempts and backoff delays. This silently caps any policy with a longer deadline. By default, the resilience handler takes ownership of this timeout so that the policy's [deadline](../features/deadlines.md) is the only active bound.

## Handler capabilities

The `ResilienceHandler` runs a [policy](../reference/resilience.md) around the HTTP send operation and provides the following capabilities:

- **Request regeneration**: Builds a fresh `HttpRequestMessage` for every attempt.
- **Idempotency protection**: Prevents the retry of `POST` or `PATCH` methods unless explicitly configured to do so.
- **Per-host scoping**: Scopes the circuit breaker and retry budget to the target host.
- **Nested retry detection**: Reports when retries are occurring in nested layers. Its inbound half - the middleware that reads the marker a retrying caller sent - is [`UseResilienceNestedRetry`](nested-retries.md).
- **Response management**: Disposes of responses that are superseded by a retry.
- **Transport timeout management**: Manages `HttpClient.Timeout` to ensure the policy deadline is honored.
- **Error responses**: When an NResilience exception reaches the top of an ASP.NET Core app, `NResilience.AspNetCore` maps it to the response it means. See [Error responses](error-responses.md).

## Create a resilient client

To use the handler, create a long-lived `HttpClient` using `HttpResilience.CreateClient()`.

<!-- snippet: http-create-client -->
```csharp
// One long-lived client. The per-host breakers and budgets live on the handler, and are worth
// nothing to a client that is rebuilt per call.
private static async Task<HttpStatusCode> ReadOrderAsync(CancellationToken cancellationToken)
{
    using var client = HttpResilience.CreateClient();

    using var response = await client.GetAsync(
        requestUri: new Uri(uriString: "https://api.example.com/orders/1"), cancellationToken: cancellationToken);

    return response.StatusCode;
}
```
<!-- endsnippet -->

A long-lived client matters because the per-host circuit breakers and retry budgets live in the handler; rebuilding the client for every call throws that state away. In a DI container, use [`AddResilience()`](../di/index.md) on the client builder.

## Configure the handler

Customize the handler's behavior with `HttpResilienceOptions`.

<!-- snippet: http-options -->
```csharp
using var client = HttpResilience.CreateClient(
    policy: Resilience.Http with { Attempts = 4 },
    options: new HttpResilienceOptions
    {
        RetryUnsafeMethods = false, // POST and PATCH are not retried. The default.
        OwnTransportTimeout = true, // HttpClient.Timeout stops competing with the deadline.
        BreakerPerHost = true, // a dead host does not trip calls to the healthy ones
        BudgetPerHost = true,
        MaximumHosts = 1024, // the per-host registry is bounded; int.MaxValue is as close to unbounded as it gets
        DetectNestedRetries = true,
    });
```
<!-- endsnippet -->

| Option | Default | Description | Reference |
| :--- | :--- | :--- | :--- |
| `RetryUnsafeMethods` | `false` | Whether `POST` and `PATCH` are retried. | [Idempotency](idempotency.md) |
| `OwnTransportTimeout` | `true` | Sets `HttpClient.Timeout` to infinite so it does not conflict with the deadline. | below |
| `BreakerPerHost` | `true` | Scopes the circuit breaker to the target host. | [Per-host scope](per-host-scope.md) |
| `BudgetPerHost` | `true` | Scopes the retry budget to the target host. | [Per-host scope](per-host-scope.md) |
| `MaximumHosts` | `1024` | Bounds the per-host registry. At least 1; `int.MaxValue` is effectively unbounded. | [Per-host scope](per-host-scope.md) |
| `DetectNestedRetries` | `true` | Detects nested retry loops. | [Nested retries](nested-retries.md) |

Three things the handler does without being asked, because `Resilience.Http` and the per-host `BreakerSettings` carry them: each attempt is bounded by three times that host's measured p95, each host's breaker trips on an error rate five times that host's own, and each host's breaker trips on half a window of calls three times slower than that host's own normal. All three are measured per host, none is armed until it has a baseline, and each can be turned off - see [attempt timeouts](../features/deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it) and [trip conditions](../features/circuit-breaker.md#trip-conditions).

The one adaptive guard that is *not* on by default is the concurrency limit, because a limiter holds live permits and queues callers - not something a default should start doing. It is one option when you want it: `.AddRateLimit(o => o.Adaptive = new())` gives every host a concurrency limit discovered from its own latency. See [rate limiting](../features/rate-limiting.md#from-configuration).

## Manage the transport timeout

When `OwnTransportTimeout` is `true`, NResilience sets `HttpClient.Timeout` to `Timeout.InfiniteTimeSpan`, leaving the [deadline](../features/deadlines.md) as the only active time bound.

If you construct an `HttpClient` yourself and pass it a `ResilienceHandler`, `OwnTransportTimeout` has no effect - the handler cannot modify the client that contains it - so set the timeout manually:

<!-- snippet: troubleshoot-transport-timeout -->
```csharp
// HttpClient.Timeout defaults to 100 seconds and covers the whole retry sequence, so it
// silently caps any deadline longer than that. On a client you build yourself, hand the
// bound to the policy.
using var client = new HttpClient(handler: new ResilienceHandler(innerHandler: new HttpClientHandler()))
{
    Timeout = Timeout.InfiniteTimeSpan,
};
```
<!-- endsnippet -->

## Verify retry behavior

`WillRetry` tells you whether the handler will retry a specific request, based on its method and configuration.

<!-- snippet: http-will-retry -->
```csharp
using var get = new HttpRequestMessage(method: HttpMethod.Get, requestUri: "https://api.example.com/orders/1");
using var post = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders");

Console.WriteLine(value: handler.WillRetry(request: get)); // True
Console.WriteLine(value: handler.WillRetry(request: post)); // False
```
<!-- endsnippet -->

## Limitations

The synchronous `Send` method is not supported and throws `NotSupportedException`. A synchronous retry loop blocks threads during backoff delays, which wastes threads and can starve the thread pool.
