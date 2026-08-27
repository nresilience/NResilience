---
title: Classification
description: Define what counts as a failure to coordinate retry, circuit breaker, and retry budget behavior.
order: 3
---

# Classification

When a call fails, NResilience must decide whether to retry, give up, or treat the failure as permanent. This decision is called a **verdict**, and a **classifier** is the rule that turns an outcome into a verdict.

Classification is enabled by default. `Resilience.Default` uses `Classifier.Default`, and `Resilience.Http` uses `Classifier.Http`.

By using a single classifier, NResilience ensures that retry logic, backoff curves, the attempt log, the circuit breaker, and the retry budget all agree on whether a failure occurred. If a classifier does not recognize an exception type, it treats it as `Permanent` by default. This prevents the library from retrying programming errors (such as null references or validation failures), which would turn a fast failure into a slow, confusing one.

## Built-in classifiers

| Classifier | Transient exceptions | Unrecognized exceptions |
| :--- | :--- | :--- |
| `Classifier.Default` | `TimeoutException`, `IOException`, `SocketException` | `Permanent` |
| `Classifier.Http` | The above, plus `HttpRequestException` and specific HTTP status codes | `Permanent` |
| `Classifier.Data` | The above, plus any `DbException` the provider calls transient | `Permanent` |
| `Classifier.RetryEverything` | Every exception | `Transient` |

`Classifier.Http` classifies HTTP status codes according to standard semantics:

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

A 404 is considered an answer and is not retried. If a specific status is transient for your API, you can add a custom rule for it. For more information, see [migrating a predicate](../migrating-from-polly.md#configure-predicates).

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
    Classify = Classifier.Data,
    Backoff = Backoff.Constant(delay: TimeSpan.FromMilliseconds(value: 50)),
};
```
<!-- endsnippet -->

The judgment comes from `DbException.IsTransient`, which is part of the base class library rather than any particular driver. Microsoft.Data.SqlClient, Npgsql, and MySqlConnector all implement it, so this classifier needs no package reference and carries no list of error numbers to go stale.

A provider that never overrode `IsTransient` reports `false` for everything, which makes `Classifier.Data` behave exactly like `Classifier.Default`. That is the property worth knowing before you reach for it: it is never worse than the default, so you do not have to audit your driver first.

What the provider cannot tell you is that a failure was the dependency *defending itself* rather than breaking. A resource-limit error is reported as transient like any other, so it takes the short backoff curve and counts as evidence against the dependency. If your provider distinguishes them, one rule of your own does too:

<!-- snippet: classifier-data-throttled -->
```csharp
// Providers cannot distinguish between a dependency failing and one defending itself.
// For example, Azure SQL reports resource limits as 10928 and 10929. Both are
// throttling: they use a long backoff curve and do not count as evidence against the
// dependency's health.
var classify = Classifier.Data.On<SqlLikeException>(e => e.Number is 10928 or 10929
    ? Verdict.Throttled()
    : Classifier.Data.ClassifyException(exception: e));

var db = Resilience.Default with { Classify = classify };
```
<!-- endsnippet -->

## Add custom exception rules

You can teach a classifier about your specific exception types using the `On` method.

<!-- snippet: classifier-custom-exception -->
```csharp
// Classifier.Default does not retry an exception type it has never heard of - retrying a
// programming error turns a fast, clear failure into a slow, confusing one. Teaching it
// about yours is one line, and the receiver is unchanged.
var api = Resilience.Default with
{
    Classify = Classifier.Default.On<MyDbException>(verdict: Verdict.Transient),
    Backoff = Backoff.None,
};
```
<!-- endsnippet -->

Rules are evaluated in reverse order of addition (most recently added first), so your custom rules override derived ones. Exception type matching includes subclasses. Every call to `On` or `OnResult` returns a **new** classifier, ensuring that the built-in static classifiers remain immutable.

You can also use a predicate to inspect the exception for more granular control:

<!-- snippet: key-concepts-verdicts -->
```csharp
var classify = Classifier.Http
    .On<MyTransportException>(verdict: Verdict.Transient) // retried, short curve
    .On<MyQuotaException>(ex => Verdict.Throttled(retryAfter: ex.RetryAfter)) // retried, long curve or the server's own delay
    .On<MyValidationException>(verdict: Verdict.Permanent); // never retried

var api = Resilience.Http with { Classify = classify };
```
<!-- endsnippet -->

## Classify a self-imposed refusal

`Verdict.Throttled` above is for pushback the dependency itself sent - a quota response, a 429. A
different case is a refusal that never reached the dependency at all: your own admission-control
check, a distributed lock, a hand-rolled limiter. Classify that to `Verdict.Refused` instead. The
retry budget and the circuit breaker then treat it correctly - neither is evidence about the
dependency, so neither charges the refusal against it:

```csharp
public sealed class ConsensusRefusedException(TimeSpan? retryAfter = null) : Exception
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

var api = Resilience.Default with
{
    Classify = Classifier.Default.On<ConsensusRefusedException>(ex => Verdict.Refused(ex.RetryAfter)),
};
```

`Verdict.Refused` is an alias of `Verdict.Limited` - the same verdict, named for a guard that is not
a rate limiter. This is the general form of what the shipped rate limiter does. See [Building a custom guard](../deep-dives/admission-control.md#building-a-custom-guard) for the full recipe, including where to throw the exception from.

## Classify returned results

Some dependencies report failures in a response envelope rather than by throwing exceptions. You can create rules to classify these results.

<!-- snippet: classifier-result-rule -->
```csharp
// Nothing is thrown: the dependency reports failure in its own envelope. A result rule is
// read by retry, the breaker and the budget alike, because they all read one classifier.
var api = Resilience.Default with
{
    Classify = Classifier.Default.OnResult<Reply>(reply => reply.Code switch
    {
        "OK" => Verdict.Ok,
        "BUSY" => Verdict.Throttled(retryAfter: TimeSpan.FromMilliseconds(value: 50)),
        _ => Verdict.Permanent,
    }),
};
```
<!-- endsnippet -->

Result rules match the static result type of the call exactly, not by assignability. Any type without a registered rule is treated as a success.

Note that an exception cannot be converted into a value. If a classifier marks an exception as `Ok`, the library treats it as "stop, do not retry" rather than a successful result.

## Inspect classifier rules

You can print a classifier to see all active rules and their evaluation order.

<!-- snippet: classifier-print -->
```csharp
// "What will this actually retry?" without reading the library's source.
Console.WriteLine(value: Classifier.Http);
```
<!-- endsnippet -->

The `ToString` method lists every rule in evaluation order, including the default behavior for unrecognized exceptions.
