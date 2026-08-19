---
title: Key concepts
description: Policy, verdict, deadline, attempt timeout and call result - the five words the rest of the docs use.
order: 2
---

# Key concepts

## A policy is a value

<!-- snippet: key-concepts-policy-value -->
```csharp
var api = Resilience.Http;                              // a preset
var patient = api with { Deadline = TimeSpan.FromMinutes(1) };  // a variant
var once = patient with { Attempts = 1 };               // a variant of the variant

Console.WriteLine(api == Resilience.Http);              // True - it is a value
Console.WriteLine(once.Deadline);                       // 00:01:00 - `with` copies the rest
```
<!-- endsnippet -->

One policy covers every return type: the result type is a property of the call, not of the policy.
`Resilience` is not generic and there is no generic variant.

Go deeper: [`Resilience` reference](../reference/resilience.md).

## Deadline and attempt timeout are different things

<!-- snippet: key-concepts-two-bounds -->
```csharp
var api = Resilience.Http with
{
    Deadline = TimeSpan.FromSeconds(10),        // the whole call, retries and backoff included
    AttemptTimeout = TimeSpan.FromSeconds(3),   // one attempt, capped by whatever is left of the deadline
};
```
<!-- endsnippet -->

The effective ceiling for an attempt is the smaller of `AttemptTimeout` and the time left on the
`Deadline`, so "is that per attempt or total?" has no answer to get wrong. The docs never write bare
"timeout": ambiguity there is what this library exists to remove.

Go deeper: [Deadlines and attempt timeouts](../features/deadlines.md).

## Every outcome gets one of four verdicts

A **classifier** turns an outcome - a returned value or a thrown exception - into a **verdict**, and
everything downstream reads that one answer.

| Verdict | What it means | What happens |
| --- | --- | --- |
| `Ok` | The call worked | Returned |
| `Transient` | May not recur | Retried on the short backoff curve; counts against the breaker |
| `Throttled` | The dependency is defending itself | Retried on the long curve, or on the server's own `Retry-After`; never counted against the dependency |
| `Permanent` | Will recur | Never retried |

<!-- snippet: key-concepts-verdicts -->
```csharp
var classify = Classifier.Http
    .On<MyTransportException>(Verdict.Transient)                  // retried, short curve
    .On<MyQuotaException>(ex => Verdict.Throttled(ex.RetryAfter)) // retried, long curve or the server's own delay
    .On<MyValidationException>(Verdict.Permanent);                // never retried

var api = Resilience.Http with { Classify = classify };
```
<!-- endsnippet -->

`Classifier.Default` treats an exception type it does not recognize as `Permanent`. Retrying a
programming error turns a fast, clear failure into a slow, confusing one.

Go deeper: [Classification](../features/classification.md).

## A call ends in one of six ways

`StopReason` says which: `Succeeded`, `Permanent`, `AttemptsExhausted`, `DeadlineExceeded`,
`BudgetExhausted` or `DependencyUnavailable`. `TryRunAsync` hands it back on a `CallResult<T>`
alongside the value, the exception and the attempt log; `RunAsync` throws instead, rethrowing the
original exception unchanged so existing `catch` blocks keep working.

Go deeper: [`CallResult<T>`](../reference/call-result.md) and
[exceptions](../reference/exceptions.md).

## The two guards

A **circuit breaker** stops calling a dependency that is failing. A **retry budget** bounds retries
as a fraction of traffic, so a failing dependency cannot turn a fleet of clients into a load
generator. The budget is on by default; the breaker is an object you construct and share exactly as
widely as you intend.

Go deeper: [Circuit breaker](../features/circuit-breaker.md) and
[retry budget](../features/retry-budget.md).

