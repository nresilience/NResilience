---
title: Test a policy
description: Learn how to write fast, deterministic tests for resilience policies using sequences and fake time.
order: 4
---

# Test a policy

Testing resilience logic - retries, timeouts - is slow and flaky if you use real time. A test that waits 30 seconds for a timeout takes 30 seconds to run, and timing differences between machines cause intermittent failures.

NResilience's testing package lets you script dependency behavior and control the clock, so the same tests run in milliseconds and pass deterministically.

## Verify retry behavior

`Sequence<T>` is a test double that serves outcomes in a scripted order - for example, a dependency that fails twice and then succeeds.

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

- **Deterministic doubles**: Instead of setting up expectations in a mock framework, `Sequence.For<T>()` is a simple script of outcomes.
- **No delays**: `Backoff = Backoff.None` removes the wait between retries, so the test runs almost instantly.
- **Attempt logs**: Asserting on `result.Attempts.Count` is a deterministic way to verify that the policy retried exactly as many times as you expected.

## Test timeouts without waiting

To test timeouts or deadlines without waiting for the real clock, give a `FakeTimeProvider` to both the policy and the sequence. You then advance time by hand.

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

Passing the same `TimeProvider` to the policy and the sequence means scripted delays run on the fake clock, not the system clock.

## Assert on policy events

Verify that a policy raises the right events in the right order with an `EventRecorder`. This is useful for testing telemetry or logging.

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

```bash
dotnet test
```

## For more information

- [Testing](../testing/index.md): The full testing package, including the HTTP double.
- [Telemetry](../features/telemetry.md): What each `CallEventKind` means.
