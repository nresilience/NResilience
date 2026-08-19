---
title: Testing
description: Scripted callbacks, a recording listener, and a clock you control - so a policy test is neither slow nor flaky.
order: 6
---

# Testing

```bash
dotnet add package NResilience.Testing
```

Two types, and the platform's own `FakeTimeProvider`. The package adds nothing to the execution path.

## Script the callback

<!-- snippet: testing-sequence -->
```csharp
Sequence<HttpResponseMessage> calls = Sequence.For<HttpResponseMessage>()
    .Returns(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), count: 2)
    .Returns(new HttpResponseMessage(HttpStatusCode.OK));

var policy = Resilience.Http with { Backoff = Backoff.None };

CallResult<HttpResponseMessage> result = await policy.TryRunAsync(ct => calls.NextAsync(ct));

Assert.True(result.IsSuccess);
Assert.Equal(3, calls.CallCount);
Assert.Equal(3, result.Attempts.Count);
```
<!-- endsnippet -->

`Sequence.For<T>()` builds a script of `Returns`, `Throws` and `Delays` steps served one per call, in
order. `Sequence.ForVoid()` scripts the void execution overloads. `CallCount` counts every call
including the one that ran off the end of the script, and running off the end throws an
`InvalidOperationException` that says so.

A step with no delay completes **synchronously**, which is what makes the synchronous-completion path
scriptable at all. A step with a delay suspends and honors the cancellation token, which is what makes
attempt timeouts and deadlines testable.

## Control the clock

<!-- snippet: testing-fake-time -->
```csharp
// Pass the same clock to the policy and to the script, or a scripted delay is a real
// sleep - which is the flakiness this package exists to remove.
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

Task<CallResult<int>> pending = policy.TryRunAsync(ct => calls.NextAsync(ct)).AsTask();
time.Advance(TimeSpan.FromSeconds(4));

CallResult<int> result = await pending;

Assert.IsType<AttemptTimeoutException>(result.Exception);
```
<!-- endsnippet -->

> [!IMPORTANT]
> Pass the same `TimeProvider` to the policy and to the sequence. A scripted delay served against the
> system clock is a real sleep, which is the flakiness this package exists to remove.

A test with a fake clock and no real delays runs the whole 30-second deadline in microseconds.

## Assert on what the policy did

<!-- snippet: testing-event-recorder -->
```csharp
var events = new EventRecorder();
Sequence<int> calls = Sequence.For<int>().Throws(new IOException()).Returns(42);

var policy = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

await policy.RunAsync(ct => calls.NextAsync(ct));

// Assert on the order, not just the membership: a telemetry surface that raises the right
// events in the wrong order still produces a log people believe.
Assert.Equal(
    [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
    events.Kinds);

Assert.Equal(VerdictKind.Transient, events.OfKind(CallEventKind.Attempt)[0].Verdict.Kind);
Assert.Equal(42, events.Single(CallEventKind.Succeeded).Result);
```
<!-- endsnippet -->

`EventRecorder` records every [`CallEvent`](../reference/events.md) in order. `Kinds` is the usual
assertion surface, and asserting on the whole sequence is worth the extra characters: a telemetry
surface that raises the right events in the wrong order still produces a log people believe.
`Single(kind)`, `OfKind(kind)`, `CountOf(kind)`, `Contains(kind)` and `Clear()` are there for the
narrower assertions.

## Testing an HTTP client

<!-- snippet: testing-http-handler -->
```csharp
var transport = new ScriptedTransport(
    () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
    () => new HttpResponseMessage(HttpStatusCode.OK));

using HttpClient client = ResilienceHttp.CreateClient(
    Resilience.Http with { Backoff = Backoff.None },
    innerHandler: transport);

using HttpResponseMessage response = await client.GetAsync(new Uri("https://api.example.com/orders/1"));

Assert.Equal(HttpStatusCode.OK, response.StatusCode);
Assert.Equal(2, transport.Requests.Count);
```
<!-- endsnippet -->

The handler needs no container: `ResilienceHttp.CreateClient` takes an inner handler, so a scripted
`HttpMessageHandler` is the whole test double.

## Two habits worth having

**Turn backoff off, or fake the clock.** `Backoff = Backoff.None` makes a retry test instant without
a clock. Anything that asserts on timing wants `FakeTimeProvider` instead.

**Assert on the attempt log, not on the elapsed time.** `result.Attempts` says how many attempts ran,
how each was classified and what delay preceded it. It is deterministic; a stopwatch is not.

