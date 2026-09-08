---
title: HTTP
description: Use the resilience handler to manage HTTP-specific constraints like request reuse, idempotency, and per-host scoping.
order: 4
---

# HTTP

A [policy](../getting-started/key-concepts.md#what-is-a-policy) manages retries, timeouts, and circuit breaking for any call. HTTP adds constraints a general policy cannot handle alone: an `HttpRequestMessage` can only be sent once, so a retry needs a fresh request; retrying a `POST` can duplicate orders or charges; and a circuit breaker should be scoped per host so one failing server does not block calls to healthy ones.

The `HttpResilienceHandler` is a `DelegatingHandler` that manages those HTTP-specific requirements. It ships in the core package - there is no separate HTTP install.

> [!IMPORTANT]
> `HttpClient.Timeout` defaults to 100 seconds and covers the entire request sequence - including all attempts and backoff delays. This silently caps any policy with a longer deadline. By default, the resilience handler takes ownership of this timeout so that the policy's [deadline](../features/deadlines.md) is the only active bound.

## Handler capabilities

The `HttpResilienceHandler` runs a [policy](../reference/resilience.md) around the HTTP send operation and provides the following capabilities:

- **Request regeneration**: Builds a fresh `HttpRequestMessage` for every attempt.
- **Idempotency protection**: Prevents the retry of `POST` or `PATCH` methods unless explicitly configured to do so.
- **Per-host scoping**: Scopes the circuit breaker and retry budget to the target host.
- **Published quota**: Reads the rate-limit headers the dependency publishes and refuses an attempt locally once the remaining allowance is inside the reserve.
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
        Quota = Quota.Reserving(reserve: 0.1), // hold a tenth of the allowance the dependency publishes
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
| `BufferResponses` | `false` | Reads the response body inside the attempt, so a stalled or broken body is retried. | below |
| `Quota` | `Quota.Reserving(0.1)` | Holds a tenth of the allowance the dependency publishes unspent. `null` reads no rate-limit headers. | below |

Three things the handler does without being asked, because `Resilience.Http` and the per-host `BreakerSettings` carry them: each attempt is bounded by three times that host's measured p95, each host's breaker trips on an error rate five times that host's own, and each host's breaker trips on half a window of calls three times slower than that host's own normal. All three are measured per host, none is armed until it has a baseline, and each can be turned off - see [attempt timeouts](../features/deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it) and [trip conditions](../features/circuit-breaker.md#trip-conditions).

The one adaptive guard that is *not* enabled by default is the concurrency limit, because a limiter holds live permits and queues callers - not something a default should start doing. It is one option when you want it: `.AddRateLimit(o => o.Adaptive = new())` gives every host a concurrency limit discovered from its own latency. See [rate limiting](../features/rate-limiting.md#from-configuration).

## Manage the transport timeout

When `OwnTransportTimeout` is `true`, NResilience sets `HttpClient.Timeout` to `Timeout.InfiniteTimeSpan`, leaving the [deadline](../features/deadlines.md) as the only active time bound.

If you construct an `HttpClient` yourself and pass it an `HttpResilienceHandler`, `OwnTransportTimeout` has no effect - the handler cannot modify the client that contains it - so set the timeout manually:

<!-- snippet: troubleshoot-transport-timeout -->
```csharp
// HttpClient.Timeout defaults to 100 seconds and covers the whole retry sequence, so it
// silently caps any deadline longer than that. On a client you build yourself, hand the
// bound to the policy.
using var client = new HttpClient(handler: new HttpResilienceHandler(innerHandler: new HttpClientHandler()))
{
    Timeout = Timeout.InfiniteTimeSpan,
};
```
<!-- endsnippet -->

## Buffered responses

The attempt ends when the response *headers* arrive. The body is a live stream over the connection, read after the executor has classified the attempt and returned - so a body that breaks or stops arriving half-way through is not something the retry loop can see, let alone retry.

By default [`BoundProgress`](../features/deadlines.md#the-third-thing-the-attempt-timeout-bounds) makes that failure finite: a body that stops arriving for longer than `AttemptTimeout` fails your read with `AttemptStalledException` instead of hanging. `BufferResponses = true` makes it retryable instead, by reading the body to completion inside the attempt:

```csharp
services.AddHttpClient(name: "api")
    .AddResilience(configureOptions: o => o.BufferResponses = true);
```

Inside the attempt, the deadline and the attempt timeout cover the body, the breaker's slow-call detection sees the real duration of the call, and a broken body is one more transient failure. The cost is memory: the whole body is held before the call returns, so leave it off for a client that downloads large files. For a JSON API whose caller was going to buffer the body a moment later anyway, it is close to free.

## Honor the allowance the dependency publishes

Most large APIs tell you how much quota you have, how much is left, and when the window resets - on every response, not just the 429. The handler reads those numbers, keeps them per host, and refuses an attempt locally once the remaining allowance is inside the reserve. It is on by default, holding a tenth back.

<!-- snippet: http-quota -->
```csharp
// The dependency publishes how much allowance is left. Hold a fifth of it back, and refuse
// locally rather than waiting for the 429. Nothing is sent, so nothing is charged to the
// retry budget and nothing counts against the host's breaker.
using var client = HttpResilience.CreateClient(
    policy: Resilience.Http with { Attempts = 1 },
    options: new HttpResilienceOptions { Quota = Quota.Reserving(reserve: 0.2) },
    innerHandler: transport);
```
<!-- endsnippet -->

The refusal never leaves the process, so it gets the treatment every local refusal gets: the long backoff curve, the reset time honored as the pushback, no evidence against the host's [circuit breaker](../features/circuit-breaker.md), and no charge against the [retry budget](../features/retry-budget.md). A `RejectedByQuota` [event](../reference/events.md) carries the time until the window resets, and the exception the caller sees is `RateLimitedException`.

Two header shapes are read, and the standard one wins where both are present:

| Shape | Headers | Read as |
| :--- | :--- | :--- |
| Standard | `RateLimit-Policy: "burst";q=100;w=60`, `RateLimit: "burst";r=50;t=30` | `q` the quota, `w` the window, `r` what is left, `t` the seconds until reset |
| Legacy | `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` | The reset is whole seconds, either a Unix timestamp or a count from now |

A host that publishes neither has no quota, and the feature is invisible. So is a host that publishes a remaining count with no quota to take a fraction of - there is no denominator for the reserve, so it is refused only once the count reaches zero. A malformed field is ignored rather than thrown.

Set `LegacyHeaders` for a dependency that spells the triple differently - `X-Rate-Limit-*` is the common variant - naming exactly three headers, in the order limit, remaining, reset.

> [!IMPORTANT]
> A server publishing a per-account quota while you are one of fifty pods will make every pod believe it owns the whole allowance. The reserve does not fix that; only the 429 does, and the 429 still works exactly as it always has. The honest use is a single-instance client, or a generous reserve. Set `Quota = null` to read no headers at all.

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
