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

### Guards the library builds for you
 
The policy's `Time` also drives the breakers and retry budgets the library constructs, including [per-host](../http/per-host-scope.md) guards and those defined in a [configuration section](../di/configuration.md). A single `FakeTimeProvider` on the policy manages a per-host breaker's break duration and a configured budget's refill.
 
<!-- snippet: testing-library-clock -->
```csharp
// The per-host breaker is built by the handler, so it runs on the policy's clock rather
// than on wall time - which is the only reason a break duration can be waited out in a
// test without actually waiting.
var time = new FakeTimeProvider();

using var handler = new ResilienceHandler(
    innerHandler: transport,
    policy: Resilience.Http with
    {
        Time = time,
        Attempts = 1,
        Backoff = Backoff.None,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Deadline = Timeout.InfiniteTimeSpan,
    },
    options: new HttpResilienceOptions
    {
        BreakerSettings = new BreakerSettings { ConsecutiveFailures = 2, BreakDuration = TimeSpan.FromSeconds(value: 15) },
    });

using var client = new HttpClient(handler: handler);

for (var i = 0; i < 2; i++)
{
    (await client.GetAsync(requestUri: "https://api.example.com/orders")).Dispose();
}

Assert.Equal(expected: BreakerState.Open, actual: handler.BreakersByHost()[key: "api.example.com"].State);

down = false;
time.Advance(delta: TimeSpan.FromSeconds(value: 16)); // the break expires on the fake clock

using var response = await client.GetAsync(requestUri: "https://api.example.com/orders");
Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
```
<!-- endsnippet -->
 
A `Breaker` you construct yourself is the exception; it uses the clock specified in its settings. To align it with a policy, provide the same `TimeProvider` instance. See [the breaker's clock](../features/circuit-breaker.md#the-breakers-clock).
 
> [!CAUTION]
> A guard that refuses a call pauses briefly on the policy's clock. Under a fake clock, this pause never ends unless the test advances time. Tests expecting a rejection must advance the clock or disable the guard (e.g., set `BreakerPerHost = false`).

## Reach for a ready-made policy

`TestPolicy.Instant` is a `Resilience` value shaped for tests: three attempts, no backoff, and both the deadline and the attempt timeout set to infinite, so a test pays for neither a sleep nor a wall-clock bound it does not care about. It retries on whatever the policy's classifier decides, and its breaker and retry budget are both off.

```csharp
using NResilience.Testing;

var api = TestPolicy.Instant;
```

`TestPolicy.InstantHttp` is the same shape with `Classify = Classifier.Http`, for a test that scripts HTTP status codes rather than a custom classifier.

To run `Instant` on a `FakeTimeProvider`, call `TestPolicy.On(time)`. It rebuilds any breaker the policy carries on that same clock, so the policy, its breaker and its budget all advance together - the same pairing the [Control the clock](#control-the-clock) section makes by hand with `Time = time`:

```csharp
var time = new FakeTimeProvider();
var api = TestPolicy.On(time);
```

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

## Test a custom listener
 
An `EventRecorder` proves the policy raised the right events. To prove a listener behaves correctly, you can use `CallEvent.Create` to build events without the executor. Since most parameters are defaulted, you only need to specify the fields your listener asserts on.
 
<!-- snippet: testing-call-event-create -->
```csharp
// The listener under test counts the two refusal kinds separately, as "the dependency
// is down" and "we are retrying too hard" require opposite responses.
var unavailable = 0;
var overRetried = 0;

void Listener(CallEvent e)
{
    if (e.Kind == CallEventKind.RejectedByBreaker)
    {
        unavailable++;
    }
    else if (e.Kind == CallEventKind.RejectedByBudget)
    {
        overRetried++;
    }
}

// CallEvent.Create builds the event the executor would raise.
Listener(CallEvent.Create(kind: CallEventKind.RejectedByBreaker, policyName: "orders", reason: StopReason.DependencyUnavailable));
Listener(CallEvent.Create(kind: CallEventKind.RejectedByBudget, policyName: "orders", reason: StopReason.BudgetExhausted));
Listener(CallEvent.Create(kind: CallEventKind.Succeeded, policyName: "orders", reason: StopReason.Succeeded));

Assert.Equal(expected: 1, actual: unavailable);
Assert.Equal(expected: 1, actual: overRetried);
```
<!-- endsnippet -->
 
This allows you to cover kinds that are difficult to provoke in a test - such as `RejectedByBudget`, `OrphanedWork`, and `NestedRetry` - without constructing a complex scenario for each.

## Test an HTTP client

You can test resilient `HttpClient` configurations by providing a scripted `HttpMessageHandler` as the inner handler.

<!-- snippet: testing-http-handler -->
```csharp
var transport = new ScriptedHttpHandler()
    .Respond(HttpStatusCode.ServiceUnavailable)
    .Respond(HttpStatusCode.OK);

using var client = ResilienceHttp.CreateClient(
    policy: Resilience.Http with { Backoff = Backoff.None },
    innerHandler: transport);

using var response = await client.GetAsync(requestUri: new Uri(uriString: "https://api.example.com/orders/1"));

Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
Assert.Equal(expected: 2, actual: transport.CallCount);
```
<!-- endsnippet -->

`ScriptedHttpHandler` serves the script you give it, then repeats the last step for every attempt after that, so it does not need to know in advance how many attempts the policy will make. `Respond` and `Throw` both return the handler, so a multi-step script reads as one chain:

- `Respond(status)` and `Respond(status, times)` serve a fixed status code, once or for a run of attempts.
- `Respond(response)` and `Respond(response, times)` build a fresh `HttpResponseMessage` from the given function on every attempt that consumes the step - use this over the status overload when a response carries content that a test reads.
- `Throw(exception)` and `Throw(exception, times)` throw instead, for the transport failures a classifier has to see.

`CallCount` is how many attempts reached the handler. `Requests` is a snapshot of what each attempt sent, in order: the method, the URI, the headers, and - only when `CaptureBodies` is `true` - the body. `CaptureBodies` defaults to `false` because reading a body buffers it; turn it on only when a test asserts on what was sent.

## Testing best practices

To keep your tests fast and deterministic, follow these practices:

- **Disable backoff or fake the clock**. Use `Backoff = Backoff.None` to make retry tests instantaneous. If your test specifically asserts on timing or delays, use `FakeTimeProvider`.
- **Assert on the attempt log**. Instead of using a stopwatch to verify retries, inspect `result.Attempts`. This log provides a deterministic record of how many attempts ran, their classifications, and the delays that preceded them.

## Inject faults on purpose

The tools above script a dependency's behavior exactly. When what you want instead is a *rate* - one call in ten fails, one in five is slow - see [Fault injection](fault-injection.md). It wraps the callback rather than the policy, so an injected failure is classified, retried, and logged exactly like a real one.
