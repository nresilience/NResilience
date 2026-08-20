---
title: Key concepts
description: Learn about policies, verdicts, deadlines, attempt timeouts, and call results.
order: 2
---

# Key concepts

## What is a policy?

A network call can fail, hang, or encounter a struggling dependency. A **policy** defines how to handle these situations: whether to retry, the total time limit, the per-attempt time limit, and when to stop calling the dependency entirely.

In NResilience, a policy is a value, not a built pipeline. Store a policy in a field, compare two policies for equality, and derive variants without using a builder. Start with a preset and use the `with` expression to create variants; `with` copies all settings you don't explicitly change.

<!-- snippet: key-concepts-policy-value -->
```csharp
var api = Resilience.Http;                              // a preset
var patient = api with { Deadline = TimeSpan.FromMinutes(1) };  // a variant
var once = patient with { Attempts = 1 };               // a variant of the variant

Console.WriteLine(api == Resilience.Http);              // True - it is a value
Console.WriteLine(once.Deadline);                       // 00:01:00 - `with` copies the rest
```
<!-- endsnippet -->

A single policy works for any return type because the result type is a property of the call, not the policy.

Policies are ideal for `static readonly` fields. Name your policies where their lifetime is obvious and derive variants as needed:

<!-- snippet: quick-start-house-policy -->
```csharp
public static class Policies
{
    public static readonly Resilience Api = Resilience.Http with
    {
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(3),
    };

    public static readonly Resilience Realtime = Api with
    {
        Attempts = 1,
        AttemptTimeout = TimeSpan.FromMilliseconds(250),
    };
}
```
<!-- endsnippet -->

The `Realtime` policy keeps the deadline and classifier from the `Api` policy.

For more information, see the [`Resilience` reference](../reference/resilience.md).

## Deadline and attempt timeout

A retried call requires two distinct time bounds. Mixing these bounds is a common source of bugs. For example, a 30-second per-attempt timeout with three retries can run for 90 seconds.

- **Deadline**: The ceiling for the entire operation, including all retries and backoff time.
- **Attempt timeout**: The ceiling for a single attempt.

<!-- snippet: key-concepts-two-bounds -->
```csharp
var api = Resilience.Http with
{
    Deadline = TimeSpan.FromSeconds(10),        // the whole call, retries and backoff included
    AttemptTimeout = TimeSpan.FromSeconds(3),   // one attempt, capped by whatever is left of the deadline
};
```
<!-- endsnippet -->

The effective ceiling for an attempt is the smaller of the `AttemptTimeout` and the time remaining on the `Deadline`.

For more information, see [Deadlines and attempt timeouts](../features/deadlines.md).

## Verdicts

A call returns a value or throws an exception. The library then decides whether to retry, give up, or treat the failure as permanent. A **classifier** maps the outcome to a **verdict**.

| Verdict | Meaning | Action |
| --- | --- | --- |
| `Ok` | The call succeeded | Result is returned |
| `Transient` | The failure may not recur | Retried on the short backoff curve; counts against the circuit breaker |
| `Throttled` | The dependency is defending itself | Retried on the long curve or based on the server's `Retry-After` header; not counted against the dependency |
| `Permanent` | The failure will recur | Not retried |

<!-- snippet: key-concepts-verdicts -->
```csharp
var classify = Classifier.Http
    .On<MyTransportException>(Verdict.Transient)                  // retried, short curve
    .On<MyQuotaException>(ex => Verdict.Throttled(ex.RetryAfter)) // retried, long curve or the server's own delay
    .On<MyValidationException>(Verdict.Permanent);                // never retried

var api = Resilience.Http with { Classify = classify };
```
<!-- endsnippet -->

`Classifier.Default` treats unrecognized exception types as `Permanent`. This prevents programming errors from becoming slow, confusing failures.

For more information, see [Classification](../features/classification.md).

## Why a call stopped

A retried call stops when it succeeds, hits a non-retryable failure, runs out of attempts, or runs out of time. The `StopReason` property identifies why the call stopped. It takes one of six values:
- `Succeeded`
- `Permanent`
- `AttemptsExhausted`
- `DeadlineExceeded`
- `BudgetExhausted`
- `DependencyUnavailable`

`TryRunAsync` returns this reason within a `CallResult<T>` along with the value, the exception, and the attempt log. `RunAsync` rethrows the original exception unchanged so that existing `catch` blocks continue to work.

For more information, see [`CallResult<T>`](../reference/call-result.md) and [exceptions](../reference/exceptions.md).

## The two guards

To prevent a fleet of clients from overwhelming a struggling dependency, NResilience provides two guards:

- **Circuit breaker**: Stops calling a dependency that is failing.
- **Retry budget**: Limits retries as a fraction of total traffic.

The retry budget is enabled by default. The circuit breaker is an object that you construct and share across the scope of the dependency.

For more information, see [Circuit breaker](../features/circuit-breaker.md) and [retry budget](../features/retry-budget.md).
