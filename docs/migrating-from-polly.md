---
title: Migrating from Polly
description: A translation guide and behavioral comparison for users migrating from Polly to NResilience.
order: 9
---

# Migrating from Polly

NResilience provides a different approach to resilience than Polly. This guide provides a translation table for core concepts and explains the behavioral differences you will encounter during migration.

The Polly snippets below are illustrative. All NResilience snippets are compiled and verified.

## Concept translation

| Polly | NResilience |
| :--- | :--- |
| `ResiliencePipeline` | [`Resilience`](reference/resilience.md) (a value, not a built pipeline) |
| `ResiliencePipelineBuilder` ... `Build()` | `with` expression on a policy |
| `AddRetry` | [`Attempts`, `Backoff`](features/retry.md) |
| `AddTimeout` (per attempt) | [`AttemptTimeout`](features/deadlines.md) |
| `AddTimeout` (outer) | `Deadline` |
| `AddCircuitBreaker` | [`Breaker`](features/circuit-breaker.md) (an object you maintain) |
| `AddBulkhead` | [`Limit.Concurrency`](features/rate-limiting.md) (bulkhead pattern) |
| `AddFallback` | `if` logic on a [`CallResult<T>`](reference/call-result.md) |
| `AddHedging` | Not implemented. See the [FAQ](faq.md) |
| `ShouldHandle` predicates | One [`Classifier`](features/classification.md) used by all strategies |
| `ResilienceContext`, `ResilienceProperties` | `TState` execution overloads |
| `ResiliencePipelineProvider<string>` | [`IResiliencePolicies`](reference/options.md) |
| `AddResiliencePipeline("name", ...)` | `services.AddResilience("name", ...)` |
| `AddStandardResilienceHandler()` | `.AddResilience()` |
| `OnRetry`, `OnTimeout`, `OnOpened`, etc. | One [`OnEvent`](features/telemetry.md) listener |
| `resilience.polly.*` metrics | `nresilience.*` metrics |

## Implement a retry, timeout, and breaker

### Before (Polly)

```csharp
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
    {
        MaxRetryAttempts = 2,                       // 2 retries, so 3 attempts
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .HandleResult(r => (int)r.StatusCode >= 500),
    })
    .AddTimeout(TimeSpan.FromSeconds(3))
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>())
    .Build();

var response = await pipeline.ExecuteAsync(ct => Send(ct), cancellationToken);
```

### After (NResilience)

<!-- snippet: migration-pipeline -->
```csharp
// One value. No pipeline, no builder, no ordering to get right - and the breaker samples
// attempts whichever way you read it.
var api = Resilience.Http with
{
    Attempts = 3, // total, including the first
    AttemptTimeout = TimeSpan.FromSeconds(3), // per attempt
    Deadline = TimeSpan.FromSeconds(10), // the whole call
    Breaker = new Breaker { Name = "api" },
};
```
<!-- endsnippet -->

In this migration, `Attempts` represents the total number of calls, so `MaxRetryAttempts = 2` becomes `Attempts = 3`. The status-code and exception predicates are handled by a [classifier](features/classification.md), which is pre-configured for HTTP in the `Resilience.Http` preset.

## Implement a fallback

### Before (Polly)

```csharp
.AddFallback(new FallbackStrategyOptions<string>
{
    FallbackAction = _ => Outcome.FromResultAsValueTask("cached"),
})
```

### After (NResilience)

<!-- snippet: migration-fallback -->
```csharp
var result = await api.TryRunAsync(attempt => calls.NextAsync(attempt), cancellationToken);
var value = result.TryGetValue(out var fetched) ? fetched : "cached";
```
<!-- endsnippet -->

A fallback is implemented as an `if` check on the `CallResult`. Implementing this at the call site makes it clear whether the value came from the dependency or from the fallback.

## Register the policy

### Before (Polly)

```csharp
services.AddHttpClient<Client>().AddStandardResilienceHandler();
```

### After (NResilience)

<!-- snippet: migration-registration -->
```csharp
services.AddHttpClient<Client>().AddResilience();
```
<!-- endsnippet -->

## Configure predicates

### Before (Polly)

```csharp
ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
    .HandleResult(r => r.StatusCode == HttpStatusCode.Conflict),
```

### After (NResilience)

<!-- snippet: migration-predicate -->
```csharp
// Classifier.Http already knows that a 429 is throttling, a 5xx or 408 is transient and a
// 404 is an answer. Adding a status of your own is one rule, and retry, the breaker and
// the budget all read it.
var api = Resilience.Http with
{
    Backoff = Backoff.None,
    Classify = Classifier.Http.OnResult<HttpResponseMessage>(r =>
        r.StatusCode == HttpStatusCode.Conflict ? Verdict.Transient : Classifier.Http.ClassifyResult(r)),
};
```
<!-- endsnippet -->

## Implement bulkhead isolation

### Before (Polly)

```csharp
var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
    .AddBulkhead(handledByEntityKey => 10)  // max 10 concurrent calls
    .Build();
```

### After (NResilience)

<!-- snippet: migration-bulkhead -->
```csharp
// For HTTP clients via dependency injection
services.AddHttpClient<PaymentClient>()
    .AddResilience()
    .AddRateLimit(options => options.Concurrency = 10);

// For any other callback
using var limiter = Limit.Concurrency(10);

var result = await policy.RunAsync(async ct =>
{
    using var lease = await limiter.AcquireOrThrowAsync(ct);
    return await dependency.CallAsync(ct);
}, cancellationToken);
```
<!-- endsnippet -->

The bulkhead pattern prevents one slow dependency from monopolizing your thread pool. In NResilience, `Limit.Concurrency` achieves this more efficiently than Polly's thread pool partitioning:

- Zero allocation when unused
- Each attempt acquires its own permit (retries don't reuse slots)
- Refusals are classified as `Verdict.Throttled(SelfImposed: true)`, which are retried on the long backoff curve and never open the breaker
- For HTTP, scoped per host by default (like circuit breakers)

For a complete guide with real-world examples, see [Resource isolation with bulkheads](guides/resource-isolation.md).

## Handle exceptions and state

### Before (Polly)
`ExecuteAsync` wraps failures and passes state through a rented `ResilienceContext`.

### After (NResilience)

<!-- snippet: migration-exceptions -->
```csharp
// The original exception is rethrown unchanged, with its stack intact, so existing catch
// blocks keep working. The history rides along on Exception.Data.
try
{
    await api.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken);
}
catch (HttpRequestException e)
{
    var attempts = AttemptLog.Of(e);
    Console.WriteLine(attempts); // 3 attempts over 1.4ms: Transient HttpRequestException (0.5ms), ...
}
```
<!-- endsnippet -->

The original exception is returned unchanged, so existing `catch` blocks continue to work. Instead of a context object, use the `TState` execution overloads to pass your own state to the callback, which also allows the lambda to be `static`.

## Behavioral differences

When migrating, be aware of these four behavioral differences:

- **Limited HTTP retries**: `Classifier.Http` treats all 4xx status codes as answers, except 408 and 429. The [HTTP handler](http/idempotency.md) does not retry `POST` or `PATCH` requests unless you explicitly mark the request as repeatable.
- **Unrecognized exceptions**: `Classifier.Default` treats unknown exception types as `Permanent`. If you require a broad handler, use `Classifier.RetryEverything`.
- **Active retry budget**: By default, retries are capped at 10% of successful traffic per policy. A load test against a dead dependency will return `StopReason.BudgetExhausted`. Use `RetryBudget.None` to disable this. See the [Retry budget](features/retry-budget.md) guide for details.
- **Refusal pause**: An open circuit breaker pauses for 100 milliseconds before reporting a failure. This prevents the breaker from becoming a load generator. See [Guarded rejection](deep-dives/guarded-rejection.md).

## Run NResilience and Polly together

You can run both libraries in the same process. Because the metric names, tag names, and event names do not overlap with Polly's vocabulary, you can distinguish between them in your dashboards. This allows you to migrate clients one at a time.
