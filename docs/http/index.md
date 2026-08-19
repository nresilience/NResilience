---
title: HTTP
description: One DelegatingHandler that does the five HTTP-specific things a policy on its own cannot.
order: 4
---

# HTTP

```bash
dotnet add package NResilience.Http
```

`ResilienceHandler` runs a [policy](../reference/resilience.md) around the send and does the five
things a policy on its own cannot:

1. Builds a **fresh request** for every attempt, because an `HttpRequestMessage` may be sent once.
2. Does not retry **POST or PATCH** unless told to, per client or per request.
3. Scopes the **breaker and the budget to the host**.
4. Reports **nested retries**.
5. **Disposes the responses** a retry supersedes.

Taking ownership of the transport timeout is the sixth, and it belongs to whoever builds the
`HttpClient`.

## A client

<!-- snippet: http-create-client -->
```csharp
// One long-lived client. The per-host breakers and budgets live on the handler, and are worth
// nothing to a client that is rebuilt per call.
private static async Task<HttpStatusCode> ReadOrderAsync(CancellationToken cancellationToken)
{
    using HttpClient client = ResilienceHttp.CreateClient();

    using HttpResponseMessage response = await client.GetAsync(
        new Uri("https://api.example.com/orders/1"), cancellationToken);

    return response.StatusCode;
}
```
<!-- endsnippet -->

One long-lived client is the right shape: the per-host breakers and budgets live on the handler and
are worth nothing to a client that is rebuilt per call. In an application with a container, use
[`AddResilience()`](../di/index.md) on the client builder instead.

## The switches

<!-- snippet: http-options -->
```csharp
using HttpClient client = ResilienceHttp.CreateClient(
    Resilience.Http with { Attempts = 4 },
    new HttpResilienceOptions
    {
        RetryUnsafeMethods = false,   // POST and PATCH are not retried. The default.
        OwnTransportTimeout = true,   // HttpClient.Timeout stops competing with the deadline.
        BreakerPerHost = true,        // a dead host does not trip calls to the healthy ones
        BudgetPerHost = true,
        DetectNestedRetries = true,
    });
```
<!-- endsnippet -->

| Option | Default | Page |
| --- | --- | --- |
| `RetryUnsafeMethods` | `false` | [Idempotency](idempotency.md) |
| `OwnTransportTimeout` | `true` | below |
| `BreakerPerHost` | `true` | [Per-host scope](per-host-scope.md) |
| `BudgetPerHost` | `true` | [Per-host scope](per-host-scope.md) |
| `DetectNestedRetries` | `true` | [Nested retries](nested-retries.md) |

## The transport timeout

> [!IMPORTANT]
> `HttpClient.Timeout` defaults to 100 seconds and covers the **entire** send - every attempt, every
> backoff delay - rather than one attempt. It silently caps any policy whose deadline is longer, and
> nothing in the policy can see it.

`OwnTransportTimeout` sets it to `Timeout.InfiniteTimeSpan` so the [deadline](../features/deadlines.md)
is the only bound, and it is honored by whoever builds the client: `ResilienceHttp.CreateClient` or
the DI registration. A `DelegatingHandler` cannot reach the client in front of it, so setting the
option `false` on a handler you hand to your own `HttpClient` does nothing at all - set the timeout
yourself:

<!-- snippet: troubleshoot-transport-timeout -->
```csharp
// HttpClient.Timeout defaults to 100 seconds and covers the whole retry sequence, so it
// silently caps any deadline longer than that. On a client you build yourself, hand the
// bound to the policy.
using var client = new HttpClient(new ResilienceHandler(new HttpClientHandler()))
{
    Timeout = Timeout.InfiniteTimeSpan,
};
```
<!-- endsnippet -->

## Asking what it will do

<!-- snippet: http-will-retry -->
```csharp
using var get = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/orders/1");
using var post = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/orders");

Console.WriteLine(handler.WillRetry(get));    // True
Console.WriteLine(handler.WillRetry(post));   // False
```
<!-- endsnippet -->

## Not supported

The synchronous `Send` throws `NotSupportedException`. A retry loop that blocks holds a thread
through every backoff delay.

