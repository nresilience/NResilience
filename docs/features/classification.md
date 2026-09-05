---
title: Classification
description: Define what counts as a failure to coordinate retry, circuit breaker, and retry budget behavior.
order: 3
---

# Classification

When a call fails, NResilience decides whether to retry, give up, or treat the failure as permanent. That decision is a **verdict**, and a **classifier** is the rule that turns an outcome into a verdict.

Classification is on by default: `Resilience.Default` uses `Classifier.Default`, and `Resilience.Http` uses `Classifier.Http`.

One classifier serves everything - retry logic, backoff curves, the attempt log, the circuit breaker, and the retry budget all read it, so they always agree on whether a failure happened. Unrecognized exception types count as `Permanent`, so a programming error (a null reference, a validation failure) fails fast instead of being retried into a slow, confusing failure.

## Built-in classifiers

| Classifier | Transient exceptions | Unrecognized exceptions |
| :--- | :--- | :--- |
| `Classifier.Default` | `TimeoutException`, `IOException`, `SocketException` | `Permanent` |
| `Classifier.Http` | The above, plus `HttpRequestException` and specific HTTP status codes | `Permanent` |
| `Classifier.Data` | The above, plus any `DbException` the provider calls transient | `Permanent` |
| `Classifier.RetryEverything` | Every exception | `Transient` |

`Classifier.Http` classifies HTTP status codes by their standard meanings:

<!-- snippet: classifier-http-table -->
```csharp
var http = Classifier.Http;

var throttled = http.ClassifyResult(value: new HttpResponseMessage(statusCode: HttpStatusCode.TooManyRequests)); // Throttled
var transient = http.ClassifyResult(value: new HttpResponseMessage(statusCode: HttpStatusCode.BadGateway)); // Transient
var answer = http.ClassifyResult(value: new HttpResponseMessage(statusCode: HttpStatusCode.NotFound)); // Ok - a 404 is an answer
```
<!-- endsnippet -->

| Status | Verdict |
| :--- | :--- |
| 429 | `Throttled` (includes `Retry-After` if provided by the server) |
| 503 with `Retry-After` | `Throttled` (includes `Retry-After`) |
| Other 5xx or 408 | `Transient` |
| 404 and other 4xx | `Ok` (treated as a valid answer, not a failure) |

A 404 is an answer, not a failure, so it is not retried. If a status is transient for your API, add a custom rule for it - see [migrating a predicate](../migrating-from-polly.md#configure-predicates).

## Classify database failures

`Classifier.Data` adds one rule to `Classifier.Default`: a `DbException` is `Transient` when the provider says it is, and `Permanent` when it does not.

<!-- snippet: classifier-data -->
```csharp
// Classifier.Data reads DbException.IsTransient, which maintained ADO.NET providers
// implement. This avoids using a driver package or a manual table of error numbers.
// Providers that do not implement this property report false, making Classifier.Data
// equivalent to Classifier.Default.
var db = Resilience.Default with
{
    Classifier = Classifier.Data,
    Backoff = Backoff.Constant(delay: TimeSpan.FromMilliseconds(value: 50)),
};
```
<!-- endsnippet -->

The judgment comes from `DbException.IsTransient`, which is part of the base class library, not any one driver. Microsoft.Data.SqlClient, Npgsql, and MySqlConnector all implement it, so this classifier needs no driver package reference and carries no list of error numbers to go stale.

A provider that never overrides `IsTransient` reports `false` for everything, which makes `Classifier.Data` behave exactly like `Classifier.Default`. That is the property to know before you reach for it: it is never worse than the default, so you don't have to audit your driver first.

What the provider cannot tell you is that a failure was the dependency *defending itself* rather than breaking. A resource-limit error is reported as transient like any other, so it takes the short backoff curve and counts as evidence against the dependency. If your provider distinguishes them, one rule of your own does too:

<!-- snippet: classifier-data-throttled -->
```csharp
// Providers cannot distinguish between a dependency failing and one defending itself.
// For example, Azure SQL reports resource limits as 10928 and 10929. Both are
// throttling: they use a long backoff curve and do not count as evidence against the
// dependency's health.
var classifier = Classifier.Data.On<SqlLikeException>(e => e.Number is 10928 or 10929
    ? Verdict.Throttled()
    : Classifier.Data.ClassifyException(exception: e));

var db = Resilience.Default with { Classifier = classifier };
```
<!-- endsnippet -->

## Add custom exception rules

Teach a classifier about your exception types with `On`.

<!-- snippet: classifier-custom-exception -->
```csharp
// Classifier.Default does not retry an exception type it has never heard of - retrying a
// programming error turns a fast, clear failure into a slow, confusing one. Teaching it
// about yours is one line, and the receiver is unchanged.
var api = Resilience.Default with
{
    Classifier = Classifier.Default.On<MyDbException>(verdict: Verdict.Transient),
    Backoff = Backoff.None,
};
```
<!-- endsnippet -->

Rules run in reverse order of addition (most recently added first), so custom rules override derived ones. Exception type matching includes subclasses. Every call to `On` or `OnResult` returns a **new** classifier, so the built-in static classifiers stay immutable.

A predicate can inspect the exception for finer control:

<!-- snippet: key-concepts-verdicts -->
```csharp
var classifier = Classifier.Http
    .On<MyTransportException>(verdict: Verdict.Transient) // retried, short curve
    .On<MyQuotaException>(ex => Verdict.Throttled(retryAfter: ex.RetryAfter)) // retried, long curve or the server's own delay
    .On<MyValidationException>(verdict: Verdict.Permanent); // never retried

var api = Resilience.Http with { Classifier = classifier };
```
<!-- endsnippet -->

## Classify a self-imposed refusal

`Verdict.Throttled` above is for pushback the dependency itself sent - a quota response, a 429. A
different case is a refusal that never reached the dependency at all: your own admission-control
check, a distributed lock, a hand-rolled limiter. Classify that to `Verdict.Refused` instead. The
retry budget and the circuit breaker then treat it correctly - neither counts the refusal as
evidence about the dependency:

```csharp
public sealed class ConsensusRefusedException(TimeSpan? retryAfter = null) : Exception
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

var api = Resilience.Default with
{
    Classifier = Classifier.Default.On<ConsensusRefusedException>(ex => Verdict.Refused(ex.RetryAfter)),
};
```

`Verdict.Refused` is named for what happened rather than for the mechanism, so it reads correctly for
a guard that is not a rate limiter. This is the general form of what the shipped rate limiter does. See [Building a custom guard](../deep-dives/admission-control.md#building-a-custom-guard) for the full recipe, including where to throw the exception.

## Classify returned results

Some dependencies report failures in a response envelope instead of throwing. Add rules for those results.

<!-- snippet: classifier-result-rule -->
```csharp
// Nothing is thrown: the dependency reports failure in its own envelope. A result rule is
// read by retry, the breaker and the budget alike, because they all read one classifier.
var api = Resilience.Default with
{
    Classifier = Classifier.Default.OnResult<Reply>(reply => reply.Code switch
    {
        "OK" => Verdict.Ok,
        "BUSY" => Verdict.Throttled(retryAfter: TimeSpan.FromMilliseconds(value: 50)),
        _ => Verdict.Permanent,
    }),
};
```
<!-- endsnippet -->

Result rules match the static result type of the call exactly, not by assignability. Any type without a registered rule counts as a success.

An exception cannot be turned into a value: if a classifier marks an exception `Ok`, the library treats it as "stop, do not retry" rather than as a successful result.

## Inspect classifier rules

Print a classifier to see every active rule and its evaluation order.

<!-- snippet: classifier-print -->
```csharp
// "What will this actually retry?" without reading the library's source.
Console.WriteLine(value: Classifier.Http);
```
<!-- endsnippet -->

`ToString` lists every rule in evaluation order, including the default behavior for unrecognized exceptions.
