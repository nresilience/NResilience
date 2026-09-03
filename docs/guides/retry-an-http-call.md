---
title: Retry an HTTP call
description: Implement a resilient HTTP call that survives transient failures, respects deadlines, and reports outcomes without exceptions.
order: 1
---

# Retry an HTTP call

When you call an HTTP API, transient failures - a 503 response, a dropped connection - can break your application. A retry policy handles them automatically: it attempts the call again before giving up.

## Implementation example

This example wraps an HTTP call in a resilience policy using `TryRunAsync`.

<!-- snippet: guide-retry-an-http-call -->
```csharp
private static async Task<Order?> ReadOrderAsync(HttpClient client, string id, CancellationToken cancellationToken)
{
    // Resilience.Http knows that a 503 is transient, a 429 is throttling and a 404 is an
    // answer. Three attempts, a 30 s deadline and a 10 s attempt ceiling are the defaults.
    var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10) };

    var result = await api.TryRunAsync(
        attempt => client.GetFromJsonAsync<Order>(requestUri: new Uri(uriString: $"https://api.example.com/orders/{id}"), cancellationToken: attempt),
        cancellationToken: cancellationToken);

    if (result.TryGetValue(value: out var order))
        return order;

    // The failure, and everything that led to it, without an exception.
    Console.WriteLine(value: $"{result.StopReason}: {result.Attempts}");
    return null;
}
```
<!-- endsnippet -->

### Key implementation details

- **HTTP classification**: `Resilience.Http` uses [`Classifier.Http`](../features/classification.md) to decide which failures are worth retrying: 503 responses are transient, 429 responses are throttling, and 404 responses are permanent.
- **Backoff and jitter**: By default, the policy makes three attempts with exponential backoff and full jitter (see [`Backoff.Default`](../features/retry.md#backoff)). Backoff delays each retry; jitter randomizes that delay so many clients do not all retry at the same instant. If the server sends a `Retry-After` header, NResilience uses that value instead of the backoff curve.
- **Deadlines**: The `Deadline` property bounds the total time for the operation, including all retries and backoff delays, so the call cannot hang indefinitely. The attempt ceiling stays at its default of 10 seconds, capped by whatever is left of the deadline. See [Deadlines](../features/deadlines.md).
- **Retry budget**: A private [retry budget](../features/retry-budget.md) is on automatically. It caps retries as a fraction of total traffic so the client does not become a load generator that pours fuel on a broad outage.
- **Outcome reporting**: `TryRunAsync` returns a `CallResult` instead of throwing for expected resilience failures, which makes the outcome and the attempt log easy to inspect.

## Use a handler for shared clients

If multiple parts of your application share one `HttpClient`, attach the policy to the client with a handler instead of wrapping every call site.

The [resilience handler](../http/index.md) clones each request so it can be retried, keeps `POST` requests out of the retry path unless you say otherwise, and scopes circuit breakers per host.

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

In an application with a DI container, call [`AddResilience()`](../di/index.md) instead.

## Handle the outcome

Use `result.TryGetValue(out var order)` to check whether the call succeeded. If it failed, `result.StopReason` says why:

- `AttemptsExhausted`: All retry attempts were used.
- `DeadlineExceeded`: The overall time limit was reached.
- `DependencyUnavailable`: The [circuit breaker](../features/circuit-breaker.md) is refusing calls.
- `BudgetExhausted`: The [retry budget](../features/retry-budget.md) was spent.
- `Permanent`: The classifier decided retrying would not change the outcome.

The `result.Attempts` property provides a log of every attempt, including the verdict and the delay before that attempt.

## For more information

- [Classification](../features/classification.md): Add your own status codes or exceptions to the classifier.
- [Idempotency](../http/idempotency.md): When it is safe to retry `POST` requests.
- [Why one flat executor](../deep-dives/one-executor.md): The performance cost of a resilience call.
