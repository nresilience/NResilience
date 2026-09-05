---
title: Key concepts
description: Learn about policies, verdicts, deadlines, attempt timeouts, and call results.
order: 2
---

# Key concepts

## What is a policy?

Network calls can fail, hang, or hit a struggling dependency. A **policy** defines how to handle these failures: whether to retry, the total time limit, the per-attempt time limit, and when to stop calling the dependency entirely.

A policy is a value, not a built pipeline. Store one in a field, compare two with `==`, and derive variants with the `with` expression - no builder needed. Start from a preset and change only the settings you need; `with` copies all others.

<!-- snippet: key-concepts-policy-value -->
```csharp
var api = Resilience.Http; // a preset
var patient = api with { Deadline = TimeSpan.FromMinutes(value: 1) }; // a variant
var once = patient with { Attempts = 1 }; // a variant of the variant

Console.WriteLine(value: api == Resilience.Http); // True - it is a value
Console.WriteLine(value: once.Deadline); // 00:01:00 - `with` copies the rest
```
<!-- endsnippet -->

A single policy works for any return type because the result type is a property of the call, not the policy.

Use `static readonly` fields for policies. Name them where their lifetime is obvious and derive variants as needed:

<!-- snippet: quick-start-house-policy -->
```csharp
public static class Policies
{
    public static readonly Resilience Api = Resilience.Http with
    {
        Deadline = TimeSpan.FromSeconds(value: 10),
        AttemptTimeout = TimeSpan.FromSeconds(value: 3),
    };

    public static readonly Resilience Realtime = Api with
    {
        Attempts = 1,
        AttemptTimeout = TimeSpan.FromMilliseconds(value: 250),
    };
}
```
<!-- endsnippet -->

The `Realtime` policy inherits the deadline and classifier from the `Api` policy.

For more information, see the [`Resilience` reference](../reference/resilience.md).

## Deadline and attempt timeout

Retried calls need two separate time bounds. Confusing them often leads to bugs: a 30-second attempt timeout with three retries can run for 90 seconds.

- **Deadline**: The ceiling for the entire call, including retries and backoff.
- **Attempt timeout**: The ceiling for a single attempt.

<!-- snippet: key-concepts-two-bounds -->
```csharp
var api = Resilience.Http with
{
    Deadline = TimeSpan.FromSeconds(value: 10), // the whole call, retries and backoff included
    AttemptTimeout = TimeSpan.FromSeconds(value: 3), // one attempt, capped by whatever is left of the deadline
};
```
<!-- endsnippet -->

The effective ceiling for an attempt is the smallest of the `AttemptTimeout`, the time remaining on the `Deadline`, and three times what a call recently took - the policy measures that last one for you, and it can only lower the ceiling.

For more information, see [Deadlines and attempt timeouts](../features/deadlines.md).

## Constants and measurements compose

That last bound is an instance of one rule, and it holds everywhere in the library:

> A bound can be stated as a constant, measured from the dependency, or both. When both, the tighter one wins - the measured term never loosens what you wrote.

| Constant | Measured | Effect together |
| --- | --- | --- |
| `AttemptTimeout` | `AttemptCeiling` | The attempt is cut at whichever is shorter. |
| `Breaker.SlowCallThreshold` | `Breaker.SlowCalls` | A call is slow when it is above either. |
| `Breaker.FailureRatio` | `Breaker.Failures` | The breaker trips at whichever ratio is lower. |

The measured half of each pair is on by default and stays invisible until it has a baseline, so a cold process behaves exactly as one with only the constants would.

To opt out of all adaptive measurement, set `Adaptive = false` on the policy and on the breaker's settings if it has one. The policy's switch deliberately stops at the breaker, because a breaker is a live object two policies may share. In configuration, a single `"Adaptive": false` setting covers both.

## Verdicts

Calls return a value or throw. The library then decides whether to retry, give up, or treat the failure as permanent. A **classifier** maps each outcome to a **verdict**.

| Verdict | Meaning | Action |
| --- | --- | --- |
| `Ok` | The call succeeded | Result is returned |
| `Transient` | The failure may not recur | Retried on the short backoff curve; counts against the circuit breaker |
| `Throttled` | The dependency is defending itself | Retried on the long curve or based on the server's `Retry-After` header; not counted against the dependency |
| `Permanent` | The failure will recur | Not retried |

<!-- snippet: key-concepts-verdicts -->
```csharp
var classifier = Classifier.Http
    .On<MyTransportException>(verdict: Verdict.Transient) // retried, short curve
    .On<MyQuotaException>(ex => Verdict.Throttled(retryAfter: ex.RetryAfter)) // retried, long curve or the server's own delay
    .On<MyValidationException>(verdict: Verdict.Permanent); // never retried

var api = Resilience.Http with { Classifier = classifier };
```
<!-- endsnippet -->

`Classifier.Default` treats unrecognized exception types as `Permanent` so programming errors fail fast.

For more information, see [Classification](../features/classification.md).

## Why a call stopped

Retried calls stop when they succeed, hit a non-retryable failure, exhaust attempts, or exceed the deadline. The `Reason` property identifies why the call stopped.

`TryRunAsync` returns this reason in a `CallResult<T>` along with the value, the exception, and the attempt log. `RunAsync` rethrows the original exception so existing `catch` blocks continue to work.

For more information, see [`CallResult<T>`](../reference/call-result.md) and [exceptions](../reference/exceptions.md).

## The three guards

Prevent a fleet of clients from overwhelming a struggling dependency using these three guards:

- **Circuit breaker**: Stops calling a dependency that is failing.
- **Retry budget**: Limits retries as a fraction of total traffic.
- **Limiter**: Limits the absolute rate, or the concurrency, of what leaves this process.

The retry budget is on by default. Construct the circuit breaker and share it across a dependency's scope - it trips on slowness and on error rates measured against the dependency's own, without being told what either normally is. The limiter is opt-in.

For more information, see [Circuit breaker](../features/circuit-breaker.md), [retry budget](../features/retry-budget.md) and [rate limiting](../features/rate-limiting.md).
