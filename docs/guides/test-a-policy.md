---
title: Test a policy
description: A test that proves the retry happened, asserts on the events, and runs in milliseconds.
order: 4
---

# Test a policy

## Scenario

Testing retry and backoff code the naive way means using real delays - a 30-second timeout test
takes 30 seconds, and a backoff test can pass on one machine and fail on another when timing
varies. You want a test that proves the call is retried, that the deadline bites, and that neither
claim depends on a real sleep.

## Complete example

<!-- snippet: testing-sequence -->
```csharp
Sequence<HttpResponseMessage> calls = Sequence.For<HttpResponseMessage>()
    .Returns(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), count: 2)
    .Returns(new HttpResponseMessage(HttpStatusCode.OK));

var policy = Resilience.Http with { Backoff = Backoff.None };

CallResult<HttpResponseMessage> result = await policy.TryRunAsync(attempt => calls.NextAsync(attempt));

Assert.True(result.IsSuccess);
Assert.Equal(3, calls.CallCount);
Assert.Equal(3, result.Attempts.Count);
```
<!-- endsnippet -->

## What's happening

- **The script is the double.** A test double (a stand-in for the real dependency) is what
  `Sequence.For<T>()` provides: it serves outcomes one per call in order, so "fails twice then
  succeeds" is the test's first three lines rather than a mock framework (a library for setting up
  call expectations on fake objects).
- **`Backoff.None`** removes the only thing that would have made this test slow.
- **The attempt log is the assertion.** `result.Attempts.Count` is deterministic where a stopwatch is
  not.

## Assert on timing without waiting for it

<!-- snippet: testing-fake-time -->
```csharp
// Pass the same clock to the policy and to the script, or a scripted delay is a real
// sleep - and a real sleep is what makes timing tests slow and flaky.
var time = new FakeTimeProvider();

Sequence<int> calls = Sequence.For<int>(time)
    .Delays(TimeSpan.FromSeconds(30))   // longer than the attempt timeout
    .Returns(1);

var policy = Resilience.Default with
{
    Time = time,
    Attempts = 1,
    AttemptTimeout = TimeSpan.FromSeconds(3),
};

Task<CallResult<int>> pending = policy.TryRunAsync(attempt => calls.NextAsync(attempt)).AsTask();
time.Advance(TimeSpan.FromSeconds(4));

CallResult<int> result = await pending;

Assert.IsType<AttemptTimeoutException>(result.Exception);
```
<!-- endsnippet -->

Pass the same `TimeProvider` to the policy and to the sequence, or the scripted delay becomes a real
one.

## Assert on what the policy did

<!-- snippet: testing-event-recorder -->
```csharp
var events = new EventRecorder();
Sequence<int> calls = Sequence.For<int>().Throws(new IOException()).Returns(42);

var policy = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

await policy.RunAsync(attempt => calls.NextAsync(attempt));

// Assert on the order, not just the membership: if a telemetry surface raises the right
// events in the wrong order, the log it produces is misleading even though every event
// is present.
Assert.Equal(
    [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
    events.Kinds);

Assert.Equal(VerdictKind.Transient, events.OfKind(CallEventKind.Attempt)[0].Verdict.Kind);
Assert.Equal(42, events.Single(CallEventKind.Succeeded).Result);
```
<!-- endsnippet -->

## Run it

```bash
dotnet test
```

## When to go deeper

- [Testing](../testing/index.md) - the whole package, including the HTTP double.
- [Telemetry](../features/telemetry.md) - what each event means.

