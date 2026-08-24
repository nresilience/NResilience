---
title: Retry an HTTP call
description: Implement a resilient HTTP call that survives transient failures, respects deadlines, and reports outcomes without exceptions.
order: 1
---

# Retry an HTTP call

When you call an HTTP API, transient failures - such as a 503 Service Unavailable response or a dropped connection - can cause your application to fail. To make your application more resilient, you can implement a retry policy that automatically attempts the call again before giving up.

## Implementation example

The following example shows how to wrap an HTTP call in a resilience policy using `TryRunAsync`.

<!-- snippet: guide-retry-an-http-call -->
```csharp
private static async Task<Order?> ReadOrderAsync(HttpClient client, string id, CancellationToken cancellationToken)
{
    // Resilience.Http knows that a 503 is transient, a 429 is throttling and a 404 is an
    // answer. Three attempts, a 30 s deadline and a 10 s attempt ceiling are the defaults.
    var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(10) };

    var result = await api.TryRunAsync(
        attempt => client.GetFromJsonAsync<Order>(new Uri($"https://api.example.com/orders/{id}"), attempt),
        cancellationToken);

    if (result.TryGetValue(out var order))
    {
        return order;
    }

    // The failure, and everything that led to it, without an exception.
    Console.WriteLine($"{result.StopReason}: {result.Attempts}");
    return null;
}
```
<!-- endsnippet -->

### Key implementation details

- **HTTP Classification**: `Resilience.Http` uses [`Classifier.Http`](../features/classification.md) to determine if a failure is worth retrying. It treats 503 responses as transient failures and 429 responses as throttling events, while treating 404 responses as permanent failures.
- **Backoff and Jitter**: By default, the policy makes three attempts with exponential backoff and full jitter (see [`Backoff.Default`](../features/retry.md#backoff)). Backoff introduces a short delay before each retry, and jitter randomizes that delay to prevent multiple clients from retrying at the exact same time. If the server provides a `Retry-After` header, NResilience respects that value over the backoff curve.
- **Deadlines**: The `Deadline` property bounds the total time for the operation, including all retries and backoff delays. This prevents the call from hanging indefinitely. The attempt ceiling stays at its default of 10 seconds, capped by whatever is left of the deadline. See [Deadlines](../features/deadlines.md) for more details.
- **Retry Budget**: A private [retry budget](../features/retry-budget.md) is automatically enabled. This caps retries as a fraction of total traffic, preventing the client from becoming a "load generator" that overwhelms a struggling dependency during a broad outage.
- **Outcome Reporting**: `TryRunAsync` returns a `CallResult` instead of throwing exceptions for expected resilience failures. This provides a clean way to inspect the outcome and the attempt log.

## Use a handler for shared clients

If multiple parts of your application share a single `HttpClient`, it is more efficient to attach the policy to the client using a handler rather than wrapping every individual call site.

The [resilience handler](../http/index.md) clones each request to allow retries, ensures that `POST` requests are handled according to idempotency rules, and scopes circuit breakers per host.

<!-- snippet: http-create-client -->
```csharp
// One long-lived client. The per-host breakers and budgets live on the handler, and are worth
// nothing to a client that is rebuilt per call.
private static async Task<HttpStatusCode> ReadOrderAsync(CancellationToken cancellationToken)
{
    using var client = ResilienceHttp.CreateClient();

    using var response = await client.GetAsync(
        new Uri("https://api.example.com/orders/1"), cancellationToken);

    return response.StatusCode;
}
```
<!-- endsnippet -->

In applications using a dependency injection container, you can achieve this by calling [`AddResilience()`](../di/index.md).

## Handle the outcome

Use `result.TryGetValue(out var order)` to determine if the call succeeded. If the call fails, `result.StopReason` explains why the operation stopped:

- `AttemptsExhausted`: All retry attempts were used.
- `DeadlineExceeded`: The overall time limit was reached.
- `DependencyUnavailable`: The [circuit breaker](../features/circuit-breaker.md) is refusing calls.
- `BudgetExhausted`: The [retry budget](../features/retry-budget.md) was spent.
- `Permanent`: The classifier determined that the failure would not change upon retrying.

The `result.Attempts` property provides a log of every attempt, including the verdict and the delay before that attempt.

## For more information

- [Classification](../features/classification.md): Learn how to add your own status codes or exceptions to the classifier.
- [Idempotency](../http/idempotency.md): Learn when it is safe to retry `POST` requests.
- [Why one flat executor](../deep-dives/one-executor.md): Understand the performance cost of a resilience call.
