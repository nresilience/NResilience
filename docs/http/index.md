---
title: HTTP
description: Use the resilience handler to manage HTTP-specific constraints like request reuse, idempotency, and per-host scoping.
order: 4
---

# HTTP

A [policy](../getting-started/key-concepts.md#what-is-a-policy) manages retries, timeouts, and circuit breaking for any call. However, HTTP introduces specific constraints that a general policy cannot address on its own.

For example, an `HttpRequestMessage` can only be sent once, so a retry requires a fresh request. Additionally, retrying a `POST` request can lead to duplicate orders or charges, and a circuit breaker should be scoped per host to prevent a single failing server from blocking calls to healthy ones.

The `ResilienceHandler` is a `DelegatingHandler` that manages these HTTP-specific requirements. The handler ships in the core package - there is no separate HTTP install.

> [!IMPORTANT]
> `HttpClient.Timeout` defaults to 100 seconds and covers the entire request sequence - including all attempts and backoff delays. This silently caps any policy with a longer deadline. By default, the resilience handler takes ownership of this timeout so that the policy's [deadline](../features/deadlines.md) is the only active bound.

## Handler capabilities

The `ResilienceHandler` runs a [policy](../reference/resilience.md) around the HTTP send operation and provides the following capabilities:

- **Request regeneration**: Builds a fresh `HttpRequestMessage` for every attempt.
- **Idempotency protection**: Prevents the retry of `POST` or `PATCH` methods unless explicitly configured to do so.
- **Per-host scoping**: Scopes the circuit breaker and retry budget to the target host.
- **Nested retry detection**: Reports when retries are occurring in nested layers.
- **Response management**: Disposes of responses that are superseded by a retry.
- **Transport timeout management**: Manages `HttpClient.Timeout` to ensure the policy deadline is honored.

## Create a resilient client

To use the handler, create a long-lived `HttpClient` using `ResilienceHttp.CreateClient()`.

<!-- snippet: http-create-client -->
```csharp
// One long-lived client. The per-host breakers and budgets live on the handler, and are worth
// nothing to a client that is rebuilt per call.
private static async Task<HttpStatusCode> ReadOrderAsync(CancellationToken cancellationToken)
{
    using var client = ResilienceHttp.CreateClient();

    using var response = await client.GetAsync(
        requestUri: new Uri(uriString: "https://api.example.com/orders/1"), cancellationToken: cancellationToken);

    return response.StatusCode;
}
```
<!-- endsnippet -->

Using a long-lived client is essential because the per-host circuit breakers and retry budgets reside within the handler. Rebuilding the client for every call discards this state. In applications using a dependency injection container, use [`AddResilience()`](../di/index.md) on the client builder.

## Configure the handler

You can customize the handler's behavior using `HttpResilienceOptions`.

<!-- snippet: http-options -->
```csharp
using var client = ResilienceHttp.CreateClient(
    policy: Resilience.Http with { Attempts = 4 },
    options: new HttpResilienceOptions
    {
        RetryUnsafeMethods = false, // POST and PATCH are not retried. The default.
        OwnTransportTimeout = true, // HttpClient.Timeout stops competing with the deadline.
        BreakerPerHost = true, // a dead host does not trip calls to the healthy ones
        BudgetPerHost = true,
        DetectNestedRetries = true,
    });
```
<!-- endsnippet -->

| Option | Default | Description | Reference |
| :--- | :--- | :--- | :--- |
| `RetryUnsafeMethods` | `false` | Determines if `POST` and `PATCH` are retried. | [Idempotency](idempotency.md) |
| `OwnTransportTimeout` | `true` | Sets `HttpClient.Timeout` to infinite to avoid conflicting with the deadline. | below |
| `BreakerPerHost` | `true` | Scopes the circuit breaker to the target host. | [Per-host scope](per-host-scope.md) |
| `BudgetPerHost` | `true` | Scopes the retry budget to the target host. | [Per-host scope](per-host-scope.md) |
| `DetectNestedRetries` | `true` | Enables detection of nested retry loops. | [Nested retries](nested-retries.md) |

## Manage the transport timeout

When `OwnTransportTimeout` is set to `true`, NResilience sets `HttpClient.Timeout` to `Timeout.InfiniteTimeSpan`. This ensures the [deadline](../features/deadlines.md) is the only active time bound.

If you manually instantiate an `HttpClient` and pass it a `ResilienceHandler`, the `OwnTransportTimeout` option has no effect because the handler cannot modify the client that contains it. In this case, you must set the timeout manually:

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

You can use the `WillRetry` method to determine if the handler will retry a specific request based on its method and configuration.

<!-- snippet: http-will-retry -->
```csharp
using var get = new HttpRequestMessage(method: HttpMethod.Get, requestUri: "https://api.example.com/orders/1");
using var post = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders");

Console.WriteLine(value: handler.WillRetry(request: get)); // True
Console.WriteLine(value: handler.WillRetry(request: post)); // False
```
<!-- endsnippet -->

## Limitations

The synchronous `Send` method is not supported and throws a `NotSupportedException`. Synchronous retry loops that block threads during backoff delays are inefficient and can lead to thread pool starvation.
