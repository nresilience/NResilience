---
title: Migrating from Polly
description: What each Polly concept becomes here, in before-and-after pairs, and the four behavior differences worth knowing before you switch.
order: 9
---

# Migrating from Polly

Polly is a good library and this is not a list of its faults. It is a translation table, plus the
handful of places where the two libraries would do different things to the same call - which are the
part worth reading before you switch anything.

Polly snippets below are illustrative v8 and are not compiled by this repository. Every NResilience
snippet is.

## The translation table

| Polly | Here |
| --- | --- |
| `ResiliencePipeline` | [`Resilience`](reference/resilience.md) - a value, not a built pipeline |
| `ResiliencePipelineBuilder` … `Build()` | `with` on a policy |
| `AddRetry` | [`Attempts`, `Backoff`](features/retry.md) |
| `AddTimeout` (per attempt) | [`AttemptTimeout`](features/deadlines.md) |
| `AddTimeout` (outer) | `Deadline` |
| `AddCircuitBreaker` | [`Breaker`](features/circuit-breaker.md) - an object you hold |
| `AddFallback` | An `if` on a [`CallResult<T>`](reference/call-result.md) |
| `AddHedging` | Not implemented. See the [FAQ](faq.md) |
| `ShouldHandle` predicates | One [`Classifier`](features/classification.md), read by everything |
| `ResilienceContext`, `ResilienceProperties` | Nothing. Use the `TState` execution overloads |
| `ResiliencePipelineProvider<string>` | [`IResiliencePolicies`](reference/options.md) |
| `AddResiliencePipeline("name", …)` | `services.AddResilience("name", …)` |
| `AddStandardResilienceHandler()` | `.AddResilience()` |
| `OnRetry`, `OnTimeout`, `OnOpened`, … | One [`OnEvent`](features/telemetry.md) listener |
| `resilience.polly.*` metrics | `nresilience.*` metrics |

## A retry, a timeout and a breaker

Before:

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

After:

<!-- snippet: migration-pipeline -->
```csharp
// One value. No pipeline, no builder, no ordering to get right - and the breaker samples
// attempts whichever way you read it.
var api = Resilience.Http with
{
    Attempts = 3,                                 // total, including the first
    AttemptTimeout = TimeSpan.FromSeconds(3),      // per attempt
    Deadline = TimeSpan.FromSeconds(10),           // the whole call
    Breaker = new Breaker { Name = "api" },
};
```
<!-- endsnippet -->

Three things changed shape. `Attempts` is the **total**, so `MaxRetryAttempts = 2` becomes
`Attempts = 3`. The status-code and exception predicates become one
[classifier](features/classification.md), which is already correct for HTTP in the preset. And the
breaker sees attempts whichever order you read the code in, because there is no order.

## A fallback

Before:

```csharp
.AddFallback(new FallbackStrategyOptions<string>
{
    FallbackAction = _ => Outcome.FromResultAsValueTask("cached"),
})
```

After:

<!-- snippet: migration-fallback -->
```csharp
CallResult<string> result = await api.TryRunAsync(attempt => calls.NextAsync(attempt), cancellationToken);
string value = result.TryGetValue(out string? fetched) ? fetched : "cached";
```
<!-- endsnippet -->

A fallback is not a strategy. It is an `if`, and putting it in the pipeline is what makes "did this
value come from the dependency or from the fallback?" a question you cannot answer at the call site.

## Registration

Before:

```csharp
services.AddHttpClient<Client>().AddStandardResilienceHandler();
```

After:

<!-- snippet: migration-registration -->
```csharp
services.AddHttpClient<Client>().AddResilience();
```
<!-- endsnippet -->

## Predicates

Before:

```csharp
ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
    .HandleResult(r => r.StatusCode == HttpStatusCode.Conflict),
```

After:

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

## Exceptions and context

Before, `ExecuteAsync` wraps some failures and passes state through a `ResilienceContext` you rent and
return. After:

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
    AttemptLog? attempts = AttemptLog.Of(e);
    Console.WriteLine(attempts);   // 3 attempts over 1.4ms: Transient HttpRequestException (0.5ms), ...
}
```
<!-- endsnippet -->

The original exception comes back unchanged, with its stack intact, so existing `catch` blocks keep
working. There is no context object: the `TState` execution overloads hand your own state to the
callback, which is also what lets the lambda be `static`.

## Four behavior differences worth knowing

**A 404 is not retried, and a POST is not either.** `Classifier.Http` treats every 4xx except 408 and
429 as an answer. The [HTTP handler](http/idempotency.md) does not retry POST or PATCH unless you say
that a particular request is repeatable. Both are changes in the safe direction, and both are visible
in the [attempt log](reference/call-result.md#attemptlog) when they surprise you.

**An unrecognized exception is not retried.** `Classifier.Default` treats an exception type it has never
heard of as `Permanent`. If you were relying on a broad `Handle<Exception>`, either name your types or
use `Classifier.RetryEverything`, which is named so that choosing it is visible.

**A retry budget is already running.** Retries are capped at 10% of successful traffic per policy, on
by default. A load test that hammers a dead dependency will see retries refused with
`StopReason.BudgetExhausted`, which is the mechanism working. [Retry budget](features/retry-budget.md)
explains the arithmetic; `RetryBudget.None` turns it off.

**A refused call pauses for 100 milliseconds before it reports.** An open breaker is not fail-fast, on
purpose. See [guarded rejection](deep-dives/guarded-rejection.md).

## Running both at once

Nothing stops you. The metric names, tag names and event names deliberately share nothing with Polly's
vocabulary, so a process running both is legible in a dashboard - which is what makes migrating one
client at a time practical.

