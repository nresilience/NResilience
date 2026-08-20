---
title: Troubleshooting
description: Common symptoms, their solutions, and the technical reasoning behind them.
order: 11
---

# Troubleshooting

This guide helps you diagnose and resolve common issues when using NResilience.

## Retries not occurring

### Symptom: No retries occur, and the exception is thrown immediately.

**Solution**: Configure the classifier to treat your exception as transient.

```csharp
Classify = Classifier.Default.On<MyDbException>(Verdict.Transient)
```

**Why this happens**: `Classifier.Default` treats unrecognized exception types as `Permanent`. This prevents programming errors from being converted into slow, confusing failures. You must explicitly define which exception types are transient.

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

To identify which exception type is not being recognized, attach a telemetry listener and monitor the `NotRetried` event.

For a list of shipped rules, see [Classification](../features/classification.md). If you require the broad behavior of retrying all exceptions, use `Classifier.RetryEverything`.

### Symptom: HTTP 400 or 404 responses are not retried.

**Why this happens**: This is the intended behavior. `Classifier.Http` treats all 4xx status codes as answers rather than failures, except for 408 and 429. A 404 response is an answer, not a transient error.

If a specific status code is transient for your API, add a custom rule. For an example, see [Configure predicates](../migrating-from-polly.md#configure-predicates).

### Symptom: POST requests are not retried.

**Solution**: Mark the request as repeatable.

```csharp
request.Options.Set(ResilienceHttp.Repeatable, true);
```

**Why this happens**: `POST` and `PATCH` requests are not retried by default to prevent duplicate orders, messages, or charges.

<!-- snippet: troubleshoot-post-not-retried -->
```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/orders");
request.Options.Set(ResilienceHttp.Repeatable, true);   // this one carries an idempotency key
```
<!-- endsnippet -->

Only set this option for requests that are safe to repeat, such as those carrying an idempotency key. To enable this behavior for all requests on a client, set `HttpResilienceOptions.RetryUnsafeMethods = true`. For more details, see [Idempotency](../http/idempotency.md).

## Timeouts and deadlines

### Symptom: The call times out after approximately 100 seconds instead of at the configured deadline.

**Solution**: Set the `HttpClient.Timeout` to infinite.

```csharp
client.Timeout = Timeout.InfiniteTimeSpan;
```

**Why this happens**: `HttpClient.Timeout` defaults to 100 seconds and covers the entire operation, including all retries and backoff delays. This silently caps any deadline longer than 100 seconds. Because a `DelegatingHandler` cannot modify the client that precedes it in the pipeline, you must configure this on the client itself.

```csharp
// HttpClient.Timeout defaults to 100 seconds and covers the whole retry sequence.
// On a client you build yourself, set the bound to the policy.
using var client = new HttpClient(new ResilienceHandler(new HttpClientHandler()))
{
    Timeout = Timeout.InfiniteTimeSpan,
};
```

`ResilienceHttp.CreateClient` and the dependency injection registration handle this for you unless `OwnTransportTimeout` is set to `false`. See [The transport timeout](../http/index.md#the-transport-timeout).

### Symptom: The attempt timeout fires, but the call continues to run.

**Why this happens**: The callback is ignoring its cancellation token. A timeout cannot stop work that does not observe cancellation, and the executor must await the task. Check for calls inside the callback that do not accept a token or use `CancellationToken.None`.

When the work eventually returns, an `OrphanedWork` event fires and names the associated policy. For more information, see [The cancellation contract](../deep-dives/cancellation.md).

## Configuration and registration

### Symptom: A configuration value in `appsettings.json` has no effect.

**Solution**: Ensure you are binding to `ResilienceOptions` via `services.AddResilience(name, section)` rather than binding directly to the `Resilience` record.

**Why this happens**: Direct binding to the `Resilience` record is silently partial. For example, `Backoff:Max` is dropped, `Classify` is ignored, and `Breaker:ConsecutiveFailures` creates a breaker with default settings while ignoring your specified value.

For more information, see [Why the binding target is a DTO](../di/configuration.md#why-the-binding-target-is-a-dto). Additionally, verify that the property is bindable; classifiers, `BeforeAttempt`, and `OnEvent` are lambdas and must be configured in the `configure` callback.

### Symptom: A configuration reload does not reach the client.

**Why this happens**: A policy resolved by name on an `HttpClient` is read when the handler chain is built. `IHttpClientFactory` rebuilds this chain every two minutes by default. This lag is intentional because the handler maintains per-host breakers and budgets; rebuilding the handler per request would discard this state.

If you store a policy in a `readonly` field, configuration reloads will never reach it. Instead, resolve policies from [`IResiliencePolicies`](../reference/options.md) on a per-call basis.

### Symptom: `ResilienceConfigurationException` occurs at startup.

**Solution**: Read the `Problems` property of the exception. It lists all configuration errors at once rather than just the first one.

<!-- snippet: troubleshoot-validate -->
```csharp
var api = Resilience.Default with { Attempts = 0, Deadline = TimeSpan.FromSeconds(-1) };

var problem = Assert.Throws<ResilienceConfigurationException>(api.Validate);

Console.WriteLine(string.Join(Environment.NewLine, problem.Problems));
// Attempts must be at least 1; it is 0.
// Deadline must be positive, or Timeout.InfiniteTimeSpan for no bound; it is -00:00:01.
```
<!-- endsnippet -->

Dependency injection validates policies eagerly to ensure configuration mistakes cause startup failures rather than request failures. If you use literals, the [NRES003](../reference/analyzers.md) analyzer identifies these issues at build time.

## Performance and testing

### Symptom: Retries are refused with `BudgetExhausted`.

**Why this happens**: The [retry budget](../features/retry-budget.md) is functioning correctly. Retries are funded at 10% of successful traffic. If a dependency fails completely, it funds no retries, preventing the client from turning an outage into a load test.

If this occurs during a test that hammers a dead dependency, set `Budget = RetryBudget.None`. In production, this symptom indicates that the retry fraction has exceeded the range where retrying is effective.

### Symptom: A test is slow or flaky.

**Solution**:
- Set `Backoff = Backoff.None` for tests that only verify that a retry occurred.
- Use `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`) to advance time manually for tests that assert timing. Pass the same clock to both the policy and the scripted sequence.

For more details, see [Testing](../testing/index.md).

## Observability

### Symptom: You need to see the actual sequence of events for a call.

**Solution**: Use the `AttemptLog` to inspect the history of a call.

<!-- snippet: troubleshoot-attempt-log -->
```csharp
// Every failure carries its own history: on CallResult, on the exceptions the library
// invents, and on Exception.Data for an original exception it rethrew unchanged.
CallResult<int> result = await api.TryRunAsync(attempt => calls.NextAsync(attempt), cancellationToken);

Console.WriteLine(result.StopReason);   // AttemptsExhausted
Console.WriteLine(result.Attempts);     // 3 attempts over 0.9ms: Transient IOException (0.2ms), ...

foreach (Attempt attempt in result.Attempts)
{
    Console.WriteLine($"#{attempt.Number} {attempt.Verdict.Kind} after {attempt.DelayBefore.TotalMilliseconds}ms");
}
```
<!-- endsnippet -->

For exceptions rethrown by the library, use `AttemptLog.Of(exception)` to read the log from `Exception.Data`.
