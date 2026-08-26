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
check, a distributed lock, a hand-rolled limiter. Classify that to `Verdict.Limited` instead. The
retry budget and the circuit breaker then treat it correctly - neither is evidence about the
dependency, so neither charges the refusal against it:

```csharp
public sealed class ConsensusRefusedException(TimeSpan? retryAfter = null) : Exception
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

var api = Resilience.Default with
{
    Classify = Classifier.Default.On<ConsensusRefusedException>(ex => Verdict.Limited(ex.RetryAfter)),
};
```

This is the general form of what the shipped rate limiter does. See [Building a custom guard](../deep-dives/admission-control.md#building-a-custom-guard) for the full recipe, including where to throw the exception from.

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
