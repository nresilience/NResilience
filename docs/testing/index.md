---
title: Testing
description: Learn how to write fast, deterministic tests for resilience policies using scripted callbacks, a recording listener, and fake time.
order: 6
---

# Testing

Testing resilience logic - such as retries and timeouts - can be slow and flaky if you rely on real-time delays. A test that waits 30 seconds for a timeout takes 30 seconds to run, and timing variations across different machines can cause intermittent failures.

The `NResilience.Testing` package addresses these issues by providing tools to make your tests deterministic and fast. It allows you to script dependency behavior, capture policy events for assertion, and manipulate time to run long-duration tests in microseconds.

```bash
dotnet add package NResilience.Testing
```

The testing package is a separate dependency and does not impact the performance of the core library in production.

## Script the callback

Use the `Sequence<T>` class to create a script of outcomes (returns, throws, or delays) that are served one by one as the policy makes attempts.

<!-- snippet: testing-sequence -->
```csharp
var calls = Sequence.For<HttpResponseMessage>()
    .Returns(result: new HttpResponseMessage(statusCode: HttpStatusCode.ServiceUnavailable), count: 2)
    .Returns(result: new HttpResponseMessage(statusCode: HttpStatusCode.OK));

var policy = Resilience.Http with { Backoff = Backoff.None };

var result = await policy.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt));

Assert.True(condition: result.IsSuccess);
Assert.Equal(expected: 3, actual: calls.CallCount);
Assert.Equal(expected: 3, actual: result.Attempts.Count);
```
<!-- endsnippet -->

`Sequence.For<T>()` allows you to chain `Returns`, `Throws`, and `Delays` steps. For void execution overloads, use `Sequence.ForVoid()`. 

### Sequence behavior
- **Deterministic Outcomes**: Every call to `NextAsync` serves the next step in the script.
- **Synchronous Completion**: A step with no delay completes synchronously, allowing you to test synchronous paths.
- **Async Delays**: A step with a delay suspends execution and observes the provided cancellation token, making it possible to test attempt timeouts and deadlines.
- **Bounds**: If the script is exhausted, the sequence throws an `InvalidOperationException` specifying the script length and the call number.

## Control the clock

To test timeouts or deadlines without actually waiting for the clock, provide a `FakeTimeProvider` to both the policy and the sequence. This allows you to "advance" time manually.

<!-- snippet: testing-fake-time -->
```csharp
// Pass the same clock to the policy and to the script, or a scripted delay is a real
// sleep - and a real sleep is what makes timing tests slow and flaky.
var time = new FakeTimeProvider();

var calls = Sequence.For<int>(time: time)
    .Delays(delay: TimeSpan.FromSeconds(value: 30)) // longer than the attempt timeout
    .Returns(result: 1);

var policy = Resilience.Default with
{
    Time = time,
    Attempts = 1,
    AttemptTimeout = TimeSpan.FromSeconds(value: 3),
};

var pending = policy.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt)).AsTask();
time.Advance(delta: TimeSpan.FromSeconds(value: 4));

var result = await pending;

Assert.IsType<AttemptTimeoutException>(@object: result.Exception);
```
<!-- endsnippet -->

> [!IMPORTANT]
> You must pass the same `TimeProvider` instance to both the policy and the sequence. If the sequence uses the system clock while the policy uses a fake clock, the scripted delay becomes a real sleep, making your tests slow and flaky.

## Verify policy behavior

You can verify that a policy is emitting the correct events in the correct order by using an `EventRecorder`. This is more reliable than asserting on elapsed time.

<!-- snippet: testing-event-recorder -->
```csharp
var events = new EventRecorder();
var calls = Sequence.For<int>().Throws(exception: new IOException()).Returns(result: 42);

var policy = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

await policy.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt));

// Assert on the order, not just the membership: if a telemetry surface raises the right
// events in the wrong order, the log it produces is misleading even though every event
// is present.
Assert.Equal(
    expected: [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
    actual: events.Kinds);

Assert.Equal(expected: VerdictKind.Transient, actual: events.OfKind(kind: CallEventKind.Attempt)[index: 0].Verdict.Kind);
Assert.Equal(expected: 42, actual: events.Single(kind: CallEventKind.Succeeded).Result);
```
<!-- endsnippet -->

The `EventRecorder` captures every [`CallEvent`](../reference/events.md) in order. While you can use methods like `CountOf(kind)` or `Contains(kind)` for simple checks, asserting on the entire `Kinds` sequence is recommended to ensure that telemetry is reported in the correct order.

## Test an HTTP client

You can test resilient `HttpClient` configurations by providing a scripted `HttpMessageHandler` as the inner handler.

<!-- snippet: testing-http-handler -->
```csharp
var transport = new ScriptedTransport(
    () => new HttpResponseMessage(statusCode: HttpStatusCode.ServiceUnavailable),
    () => new HttpResponseMessage(statusCode: HttpStatusCode.OK));

using var client = ResilienceHttp.CreateClient(
    policy: Resilience.Http with { Backoff = Backoff.None },
    innerHandler: transport);

using var response = await client.GetAsync(requestUri: new Uri(uriString: "https://api.example.com/orders/1"));

Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
Assert.Equal(expected: 2, actual: transport.Requests.Count);
```
<!-- endsnippet -->

## Testing best practices

To keep your tests fast and deterministic, follow these practices:

- **Disable backoff or fake the clock**. Use `Backoff = Backoff.None` to make retry tests instantaneous. If your test specifically asserts on timing or delays, use `FakeTimeProvider`.
- **Assert on the attempt log**. Instead of using a stopwatch to verify retries, inspect `result.Attempts`. This log provides a deterministic record of how many attempts ran, their classifications, and the delays that preceded them.
