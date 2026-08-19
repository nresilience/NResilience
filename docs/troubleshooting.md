---
title: Troubleshooting
description: Symptoms, the literal fix, and then why it happens.
order: 11
---

# Troubleshooting

### Nothing was retried, and the exception came straight out

> [!CAUTION] Quick fix
> ```csharp
> Classify = Classifier.Default.On<MyDbException>(Verdict.Transient)
> ```

`Classifier.Default` treats an exception type it does not recognize as `Permanent`. That is deliberate:
retrying a programming error converts a fast, clear failure into a slow, confusing one and hides the
bug. The cost is one line per exception type you know to be transient.

<!-- snippet: troubleshoot-not-retried -->
```csharp
// Classifier.Default treats an exception type it has never heard of as Permanent. Teach it
// about yours, and the NotRetried event names the type it did not recognise.
var api = Resilience.Default with
{
    Backoff = Backoff.None,
    Classify = Classifier.Default.On<MyDbException>(Verdict.Transient),
};
```
<!-- endsnippet -->

Attach a listener and the `NotRetried` event names the type that was not recognized.

See [classification](features/classification.md) for the shipped rules, and
`Classifier.RetryEverything` if you genuinely want the old broad behavior.

### A 404 or a 400 is not being retried

That is correct. `Classifier.Http` treats every 4xx except 408 and 429 as an answer rather than a
failure - retrying a 404 is in the most-copied retry snippet in .NET, and it is wrong there too. If a
particular status really is transient for your API, add a rule:
[migrating a predicate](migrating-from-polly.md#predicates) shows the shape.

### My POST is not being retried

> [!CAUTION] Quick fix
> ```csharp
> request.Options.Set(ResilienceHttp.Repeatable, true);
> ```

POST and PATCH are not retried by default, because a retried POST is a duplicate order, message or
charge.

<!-- snippet: troubleshoot-post-not-retried -->
```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/orders");
request.Options.Set(ResilienceHttp.Repeatable, true);   // this one carries an idempotency key
```
<!-- endsnippet -->

Set it only on a request that is safe to repeat, which in practice means one carrying an idempotency
key. `HttpResilienceOptions.RetryUnsafeMethods = true` is the per-client version, and it is a much
broader statement. See [idempotency](http/idempotency.md).

### The call gave up after about 100 seconds, not at my deadline

> [!CAUTION] Quick fix
> ```csharp
> client.Timeout = Timeout.InfiniteTimeSpan;
> ```

`HttpClient.Timeout` defaults to 100 seconds and covers the **entire** send, retries and backoff
included, so it silently caps any longer deadline. A `DelegatingHandler` cannot reach the client in
front of it.

<!-- snippet: troubleshoot-transport-timeout -->
```csharp
// HttpClient.Timeout defaults to 100 seconds and covers the whole retry sequence, so it
// silently caps any deadline longer than that. On a client you build yourself, hand the
// bound to the policy.
using var client = new HttpClient(new ResilienceHandler(new HttpClientHandler()))
{
    Timeout = Timeout.InfiniteTimeSpan,
};
```
<!-- endsnippet -->

`ResilienceHttp.CreateClient` and the DI registration do this for you unless
`OwnTransportTimeout` is false. See [HTTP](http/index.md#the-transport-timeout).

### Retries are being refused with `BudgetExhausted`

The [retry budget](features/retry-budget.md) is working. Retries are funded at 10% of successful
traffic, and a dependency that is failing outright funds nothing - which is the whole point, because
the alternative is a client that turns an outage into a load test.

If the refusals are in a test that hammers a dead dependency, set `Budget = RetryBudget.None` for that
test. If they are in production, the budget is telling you the retry fraction has left the range where
retrying helps.

### A configuration value in `appsettings.json` had no effect

Check that you are binding to `ResilienceOptions` - through `services.AddResilience(name, section)` -
and not onto `Resilience` yourself. Direct binding onto the record is **silently partial**:
`Backoff:Max` is dropped, `Classify` is ignored, and `Breaker:ConsecutiveFailures` constructs a
default-settings breaker while ignoring your value. See
[why the binding target is a DTO](di/configuration.md#why-the-binding-target-is-a-dto).

Also check the property is bindable at all: a classifier, `BeforeAttempt` and `OnEvent` are lambdas and
belong in the `configure` callback.

### A configuration reload did not reach my client

A policy resolved by name on an `HttpClient` is read when the handler chain is built, which
`IHttpClientFactory` does every two minutes by default. The lag is deliberate: the handler holds the
per-host breakers and budgets, and rebuilding it per request would discard that state on every call.

If you are holding a policy in a `readonly` field, the reload will never reach it at all. Resolve from
[`IResiliencePolicies`](reference/options.md) per call.

### The attempt timeout fired but the call kept running

The callback is ignoring its cancellation token. A timeout cannot kill work that does not observe
cancellation, and the executor is awaiting that very task. Look for a call inside the callback that
takes no token, or is passed `CancellationToken.None`.

An `OrphanedWork` event fires when the work eventually returns, naming the policy. See
[the cancellation contract](deep-dives/cancellation.md).

### `ResilienceConfigurationException` at startup

> [!CAUTION] Quick fix
> Read `Problems` - it lists every problem at once, not just the first.

<!-- snippet: troubleshoot-validate -->
```csharp
var api = Resilience.Default with { Attempts = 0, Deadline = TimeSpan.FromSeconds(-1) };

var problem = Assert.Throws<ResilienceConfigurationException>(api.Validate);

Console.WriteLine(string.Join(Environment.NewLine, problem.Problems));
// Attempts must be at least 1; it is 0.
// Deadline must be positive, or Timeout.InfiniteTimeSpan for no bound; it is -00:00:01.
```
<!-- endsnippet -->

Registration validates eagerly, which is what turns a configuration mistake into a startup failure
rather than a first-request failure.

### A test is slow, or flaky

Set `Backoff = Backoff.None` for tests that only assert that a retry happened, and use
`FakeTimeProvider` for tests that assert on timing - passing the same clock to the policy **and** to
the scripted sequence. See [testing](testing/index.md).

### I need to see what actually happened

<!-- snippet: troubleshoot-attempt-log -->
```csharp
// Every failure carries its own history: on CallResult, on the exceptions the library
// invents, and on Exception.Data for an original exception it rethrew unchanged.
CallResult<int> result = await api.TryRunAsync(ct => calls.NextAsync(ct), cancellationToken);

Console.WriteLine(result.StopReason);   // AttemptsExhausted
Console.WriteLine(result.Attempts);     // 3 attempts over 0.9ms: Transient IOException (0.2ms), ...

foreach (Attempt attempt in result.Attempts)
{
    Console.WriteLine($"#{attempt.Number} {attempt.Verdict.Kind} after {attempt.DelayBefore.TotalMilliseconds}ms");
}
```
<!-- endsnippet -->

For an exception the library rethrew unchanged, `AttemptLog.Of(exception)` reads the log off
`Exception.Data`.

