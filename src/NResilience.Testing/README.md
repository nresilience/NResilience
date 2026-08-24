# NResilience.Testing

Test helpers for [NResilience](https://github.com/nresilience/NResilience): scripted callbacks, a recording telemetry listener, and fake-time support for
deterministic, fast tests.

## Install

Install the package using the .NET CLI:

```bash
dotnet add package NResilience.Testing
```

## Why a separate package

Testing retries and timeouts against the real clock is slow and flaky - a 30-second timeout takes 30 seconds to test, and timing varies across machines.
`NResilience.Testing` lets you script dependency behavior, capture policy events for assertion, and advance time manually, so a timeout test runs in
microseconds.

It is a test-time dependency only and does not affect the core library in production.

## Script the callback

`Sequence<T>` serves a script of returns, throws, and delays one by one as the policy makes attempts:

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

## Control the clock

Pass the same `FakeTimeProvider` to both the policy and the sequence to test timeouts without waiting:

```csharp
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

> [!IMPORTANT]
> Pass the same `TimeProvider` to both the policy and the sequence. If the sequence uses the system clock while the policy uses a fake clock, the scripted delay
becomes a real sleep.

## Verify policy behavior

`EventRecorder` captures every `CallEvent` in order, so you can assert on the sequence rather than elapsed time:

```csharp
var events = new EventRecorder();
Sequence<int> calls = Sequence.For<int>().Throws(new IOException()).Returns(42);

var policy = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

await policy.RunAsync(attempt => calls.NextAsync(attempt));

Assert.Equal(
    [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
    events.Kinds);
```

## Test an HTTP client

Provide a scripted `HttpMessageHandler` as the inner handler to test a resilient `HttpClient` end to end:

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

## Documentation

For more information, see the following resources:

- [Testing guide](https://github.com/nresilience/NResilience/blob/main/docs/testing/index.md) - the full walkthrough, including best practices for keeping tests
  fast and deterministic.

## Feedback

Provide feedback using these channels:

- [Usage questions](https://github.com/nresilience/NResilience/discussions)
- [Bug reports and feature requests](https://github.com/nresilience/NResilience/issues/new/choose)
- [Security vulnerabilities](https://github.com/nresilience/NResilience/security/advisories/new) - private advisory, not a public issue.
