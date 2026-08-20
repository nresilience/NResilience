---
title: Classification
description: One classifier says what counts as a failure, and retry, the breaker and the budget all read it.
order: 3
---

# Classification

When a call fails, the library has to decide what to do next - retry, give up, or treat the failure
as permanent. That decision is called a **verdict**, and a **classifier** is the rule that turns an
outcome into one. It is **on by default**: `Classifier.Default` on `Resilience.Default`,
`Classifier.Http` on `Resilience.Http`.

The point of having one rule is that retry, the backoff curve, the attempt log, the circuit breaker
and the retry budget all read the same answer, so there is no way for them to disagree about what a
failure was. A classifier that does not recognize an exception type treats it as `Permanent` by
default - retrying a programming error (a null reference, a validation failure) turns a fast, clear
failure into a slow, confusing one, so the safe default is to not retry what you did not tell it
about.

## The shipped classifiers

| Classifier | Knows about | Unrecognized exception |
| --- | --- | --- |
| `Classifier.Default` | `TimeoutException`, `IOException`, `SocketException` are `Transient` | `Permanent` |
| `Classifier.Http` | The above, plus `HttpRequestException` and HTTP status codes | `Permanent` |
| `Classifier.RetryEverything` | Nothing - every exception is `Transient` | `Transient` |

`Classifier.Http` reads status codes the way they are meant:

<!-- snippet: classifier-http-table -->
```csharp
Classifier http = Classifier.Http;

Verdict throttled = http.ClassifyResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));  // Throttled
Verdict transient = http.ClassifyResult(new HttpResponseMessage(HttpStatusCode.BadGateway));       // Transient
Verdict answer = http.ClassifyResult(new HttpResponseMessage(HttpStatusCode.NotFound));            // Ok - a 404 is an answer
```
<!-- endsnippet -->

| Status | Verdict |
| --- | --- |
| 429 | `Throttled`, carrying `Retry-After` when the server sent one |
| 503 with a `Retry-After` | `Throttled`, carrying it |
| Any other 5xx, or 408 | `Transient` |
| 404, and every other 4xx | `Ok` - an answer, not a failure |

A 404 is an answer, so it is not retried. If a status really is transient for your API, add a rule
for it - [migrating a predicate](../migrating-from-polly.md#predicates) shows the shape.

## Teaching it about your exceptions

<!-- snippet: classifier-custom-exception -->
```csharp
// Classifier.Default does not retry an exception type it has never heard of - retrying a
// programming error turns a fast, clear failure into a slow, confusing one. Teaching it
// about yours is one line, and the receiver is unchanged.
var api = Resilience.Default with
{
    Classify = Classifier.Default.On<MyDbException>(Verdict.Transient),
    Backoff = Backoff.None,
};
```
<!-- endsnippet -->

Rules are evaluated most-recently-added first, so a rule you add always beats one it was derived
from. Exception types match including subclasses. Every `On` and `OnResult` returns a **new**
classifier, so the shipped statics can never be mutated by a caller deriving from them.

The predicate overload can inspect the exception - the natural home for "this SQL error number is
transient and that one is not":

<!-- snippet: key-concepts-verdicts -->
```csharp
var classify = Classifier.Http
    .On<MyTransportException>(Verdict.Transient)                  // retried, short curve
    .On<MyQuotaException>(ex => Verdict.Throttled(ex.RetryAfter)) // retried, long curve or the server's own delay
    .On<MyValidationException>(Verdict.Permanent);                // never retried

var api = Resilience.Http with { Classify = classify };
```
<!-- endsnippet -->

## Classifying what a call returned

Plenty of dependencies report failure in their own envelope rather than by throwing.

<!-- snippet: classifier-result-rule -->
```csharp
// Nothing is thrown: the dependency reports failure in its own envelope. A result rule is
// read by retry, the breaker and the budget alike, because they all read one classifier.
var api = Resilience.Default with
{
    Classify = Classifier.Default.OnResult<Reply>(reply => reply.Code switch
    {
        "OK" => Verdict.Ok,
        "BUSY" => Verdict.Throttled(TimeSpan.FromMilliseconds(50)),
        _ => Verdict.Permanent,
    }),
};
```
<!-- endsnippet -->

Result rules match the static result type of the call **exactly**, not by assignability. A type with
no rule registered is a success.

An exception cannot be turned into a value, so a classifier that calls an exception `Ok` is read as
"stop, do not retry" rather than as a success.

## Asking what it will do

<!-- snippet: classifier-print -->
```csharp
// "What will this actually retry?" without reading the library's source.
Console.WriteLine(Classifier.Http);
```
<!-- endsnippet -->

`ToString` dumps every rule in evaluation order, including what happens to an unrecognized
exception. That is the answer to "what will this actually retry?" without reading the library's
source.

