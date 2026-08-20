---
title: Retry an HTTP call
description: One API call that survives a 503, with the outcome handled rather than caught.
order: 1
---

# Retry an HTTP call

## Scenario

You call an HTTP API that occasionally returns a 503 or drops a connection. You want the call to
survive that, to give up rather than hang, and to know what happened when it does give up.

## Complete example

`using` statements are omitted.

<!-- snippet: guide-retry-an-http-call -->
```csharp
private static async Task<Order?> ReadOrderAsync(HttpClient client, string id, CancellationToken cancellationToken)
{
    // Resilience.Http knows that a 503 is transient, a 429 is throttling and a 404 is an
    // answer. Three attempts, a 30 s deadline and a 10 s attempt ceiling are the defaults.
    var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(10) };

    CallResult<Order?> result = await api.TryRunAsync(
        attempt => client.GetFromJsonAsync<Order>(new Uri($"https://api.example.com/orders/{id}"), attempt),
        cancellationToken);

    if (result.TryGetValue(out Order? order))
    {
        return order;
    }

    // The failure, and everything that led to it, without an exception.
    Console.WriteLine($"{result.StopReason}: {result.Attempts}");
    return null;
}
```
<!-- endsnippet -->

## What's happening

- **`Resilience.Http`** brings [`Classifier.Http`](../features/classification.md), which reads a 503
  as transient, a 429 as throttling, and a 404 as an answer rather than a failure.
- **Three attempts** with exponential backoff and full jitter, from
  [`Backoff.Default`](../features/retry.md#backoff). A `Retry-After` header, if the server sends one,
  wins over the curve.
- **The deadline** bounds the whole thing, retries and backoff included. The attempt ceiling stays at
  its default of 10 seconds, capped by whatever is left of the deadline. See
  [deadlines](../features/deadlines.md).
- **A retry budget** is already running, private to this policy, so a broad outage cannot turn this
  client into a load generator. See [retry budget](../features/retry-budget.md).
- **`TryRunAsync`** reports the outcome instead of throwing, and always materializes the attempt log.

## Use the handler instead when the client is shared

If several call sites share one `HttpClient`, put the policy on the client rather than at each call
site: the [handler](../http/index.md) clones each request, keeps POST out of the retry path, and
scopes a breaker per host.

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

In an application with a container, that whole line is
[`AddResilience()`](../di/index.md).

## Handle the outcome

`result.TryGetValue(out var order)` is the success test. On failure, `result.StopReason` says why the
call stopped - `AttemptsExhausted`, `DeadlineExceeded`, `DependencyUnavailable`, `BudgetExhausted` or
`Permanent` - and `result.Attempts` prints every attempt with its verdict and the delay before it.

## When to go deeper

- [Classification](../features/classification.md) - to add a status code or an exception of your own.
- [Idempotency](../http/idempotency.md) - before you let a POST be retried.
- [Why one flat executor](../deep-dives/one-executor.md) - what this call costs.

