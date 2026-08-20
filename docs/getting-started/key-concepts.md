---
title: Key concepts
description: Policy, verdict, deadline, attempt timeout, and call result - the vocabulary the rest of the docs use.
order: 2
---

# Key concepts

## What is a policy?

A network call can fail, hang, or come back from a dependency that is struggling. A **policy** is
the object that says what to do when that happens: whether to retry, how long to wait in total, how
long to wait per attempt, and when to stop calling the dependency at all.

In NResilience a policy is a value, not a built pipeline. You hold one in a field, compare two for
equality, and derive a variant without a builder. You start from a preset and derive variants with
`with`, which copies everything you did not mention.

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

A policy is a good fit for a `static readonly` field, because it is a value rather than a built
pipeline. Name your policies once, where their lifetime is obvious, and derive variants from them:

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

`with` copies everything you did not mention, so `Realtime` keeps `Api`'s deadline and classifier.

Go deeper: [`Resilience` reference](../reference/resilience.md).

## Deadline and attempt timeout

A retried call needs two time bounds, and mixing them up is a common bug: a 30-second per-attempt
timeout with three retries can run for 90 seconds, which is probably not what you meant. The
**deadline** is the ceiling on the whole call, retries and backoff included. The **attempt
timeout** is the ceiling on a single attempt.

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
`Deadline`, so "is that per attempt or total?" has no answer to get wrong. The two bounds have
different names everywhere in this library, and the docs keep them apart the same way.

Go deeper: [Deadlines and attempt timeouts](../features/deadlines.md).

## Every outcome gets one of four verdicts

A call can come back as a value or as a thrown exception, and the library has to decide what to do
next - retry, give up, or treat the failure as permanent. A **classifier** turns that outcome into a
**verdict**, and everything downstream reads that one answer.

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

## Why a call stopped

A retried call can stop for several reasons - it worked, it hit a failure it won't retry, it ran
out of attempts, it ran out of time. `StopReason` names which one applied, and it takes one of six
values: `Succeeded`, `Permanent`, `AttemptsExhausted`, `DeadlineExceeded`, `BudgetExhausted` or
`DependencyUnavailable`. `TryRunAsync` hands it back on a `CallResult<T>` alongside the value, the
exception and the attempt log; `RunAsync` throws instead, rethrowing the original exception unchanged
so existing `catch` blocks keep working.

Go deeper: [`CallResult<T>`](../reference/call-result.md) and
[exceptions](../reference/exceptions.md).

## The two guards

A struggling dependency can take down not just your calls but the fleet of clients calling it, if
every client retries at once. Two guards stop that: a **circuit breaker** stops calling a dependency
that is failing, and a **retry budget** bounds retries as a fraction of traffic so a failing
dependency cannot turn a fleet of clients into a load generator. The budget is on by default; the
breaker is an object you construct and share exactly as widely as you intend.

Go deeper: [Circuit breaker](../features/circuit-breaker.md) and
[retry budget](../features/retry-budget.md).

