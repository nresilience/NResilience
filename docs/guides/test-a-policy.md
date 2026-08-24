---
title: Test a policy
description: Learn how to write fast, deterministic tests for resilience policies using sequences and fake time.
order: 4
---

# Test a policy

Testing resilience logic - such as retries and timeouts - can be slow and flaky if you rely on real-time delays. A test that waits 30 seconds for a timeout takes 30 seconds to run, and timing variations across different machines can cause intermittent failures.

To avoid this, NResilience provides tools to simulate dependency behavior and manipulate time, allowing you to prove your policies work correctly in milliseconds.

## Verify retry behavior

The `Sequence<T>` class acts as a test double that serves pre-defined outcomes in a specific order. This allows you to simulate complex scenarios, such as a dependency that fails twice before succeeding.

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

### Key testing concepts

- **Deterministic Doubles**: Instead of using a mock framework to set up expectations, `Sequence.For<T>()` provides a simple script of outcomes.
- **Removing Delays**: Setting `Backoff = Backoff.None` removes the real-world wait time between retries, making the test execution nearly instantaneous.
- **Attempt Logs**: Asserting on `result.Attempts.Count` provides a deterministic way to verify that the policy retried the expected number of times.

## Test timeouts without waiting

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

Passing the same `TimeProvider` to the policy and the sequence ensures that scripted delays are processed by the fake clock rather than the system clock.

## Assert on policy events

You can verify that a policy is emitting the correct events in the correct order by using an `EventRecorder`. This is particularly useful for testing telemetry or logging.

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

## Run tests

Run your tests using the standard .NET CLI:

```bash
dotnet test
```

## For more information

- [Testing](../testing/index.md): Learn about the full testing package, including the HTTP double.
- [Telemetry](../features/telemetry.md): Understand the meaning of each `CallEventKind`.
