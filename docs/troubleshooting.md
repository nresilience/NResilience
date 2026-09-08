---
title: Troubleshooting
description: Common symptoms, their solutions, and the technical reasoning behind them.
order: 12
---

# Troubleshooting

Diagnose and fix common NResilience issues.

## Retries not occurring

### Symptom: No retries occur, and the exception is thrown immediately.

**Solution**: Configure the classifier to treat your exception as transient.

```csharp
Classifier = Classifier.Default.On<MyDbException>(Verdict.Transient)
```

**Why this happens**: `Classifier.Default` treats unrecognized exception types as `Permanent`, so programming errors fail fast instead of becoming slow, confusing failures. You must say which exception types are transient.

<!-- snippet: troubleshoot-not-retried -->
```csharp
// Classifier.Default treats an exception type it has never heard of as Permanent. Teach it
// about yours, and the NotRetried event names the type it did not recognize.
var api = Resilience.Default with
{
    Backoff = Backoff.None,
    Classifier = Classifier.Default.On<MyDbException>(verdict: Verdict.Transient),
};
```
<!-- endsnippet -->

To find which exception type is not recognized, attach a telemetry listener and watch the `NotRetried` event.

For a list of shipped rules, see [Classification](./features/classification.md). If you require the broad behavior of retrying all exceptions, use `Classifier.RetryEverything`.

### Symptom: HTTP 400 or 404 responses are not retried.

**Why this happens**: Intended behavior. `Classifier.Http` treats all 4xx statuses as answers rather than failures, except 408 and 429. A 404 is an answer, not a transient error.

If a specific status code is transient for your API, add a custom rule. For an example, see [Configure predicates](./migrating-from-polly.md#configure-predicates).

### Symptom: POST requests are not retried.

**Solution**: Mark the request as repeatable.

```csharp
request.MarkRepeatable(idempotencyKey);
```

**Why this happens**: `POST` and `PATCH` are not retried by default, to prevent duplicate orders, messages, or charges.

<!-- snippet: troubleshoot-post-not-retried -->
```csharp
using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders");
request.MarkRepeatable(idempotencyKey: Guid.NewGuid().ToString()); // the option this client retries on, plus the key the service deduplicates on
```
<!-- endsnippet -->

Set this only for requests that are safe to repeat, such as those carrying an idempotency key. To turn it on for all requests on a client, set `HttpResilienceOptions.RetryUnsafeMethods = true`. For more information, see [Idempotency](./http/idempotency.md).

## Timeouts and deadlines

### Symptom: Reading the response body never returns, and the deadline does nothing.

**Solution**: Leave `BoundProgress` enabled, which is the default. If you disabled it, enable it again.

**Why this happens**: The attempt ends when the response *headers* arrive. The body is a live stream read after the policy has classified the attempt and returned, so the deadline, the attempt timeout and the retry all stop covering it - and because the handler sets `HttpClient.Timeout` to infinite, nothing else covers it either. A dependency that sends headers and then stops writing produced a call that never completed.

With `BoundProgress` on, a body that stops arriving for longer than `AttemptTimeout` fails your read with `AttemptStalledException`. To have the stall *retried* rather than merely bounded, read the body inside the attempt:

```csharp
services.AddHttpClient(name: "api")
    .AddResilience(configureOptions: o => o.BufferResponses = true);
```

See [progress bounds](./features/deadlines.md#the-third-thing-the-attempt-timeout-bounds) for the mechanism and [buffered responses](./http/index.md#buffered-responses) for the trade.

### Symptom: The call times out after approximately 100 seconds instead of at the configured deadline.

**Solution**: Set the `HttpClient.Timeout` to infinite.

```csharp
client.Timeout = Timeout.InfiniteTimeSpan;
```

**Why this happens**: `HttpClient.Timeout` defaults to 100 seconds and covers the whole operation, including all retries and backoff. It silently caps any deadline longer than 100 seconds. A `DelegatingHandler` cannot modify the client that precedes it in the pipeline, so configure this on the client itself.

```csharp
// HttpClient.Timeout defaults to 100 seconds and covers the whole retry sequence.
// On a client you build yourself, set the bound to the policy.
using var client = new HttpClient(new HttpResilienceHandler(new HttpClientHandler()))
{
    Timeout = Timeout.InfiniteTimeSpan,
};
```

`HttpResilience.CreateClient` and the dependency injection registration handle this for you unless `OwnTransportTimeout` is set to `false`. See [The transport timeout](./http/index.md#the-transport-timeout).

### Symptom: The attempt timeout fires, but the call continues to run.

**Why this happens**: The callback is ignoring its cancellation token. A timeout cannot stop work that does not observe cancellation, and the executor must await the task. Check for calls inside the callback that accept no token or use `CancellationToken.None`.

When the work eventually returns, an `OrphanedWork` event fires and names the policy. For more information, see [The cancellation contract](./deep-dives/cancellation.md).

## Configuration and registration

### Symptom: A configuration value in `appsettings.json` has no effect.

**Solution**: Ensure you are binding to `ResilienceOptions` via `services.AddResilience(name, section)` rather than binding directly to the `Resilience` record.

**Why this happens**: Binding directly to the `Resilience` record is silently partial. Scalars bind, but `Classifier` is ignored and `Breaker:ConsecutiveFailures` creates a breaker with default settings while ignoring your value.

For more information, see [Projection via ResilienceOptions](./di/configuration.md#projection-via-resilienceoptions). Also check that the property is bindable: classifiers, `BeforeAttempt`, and `OnEvent` are lambdas and must be set in the `configure` callback.

### Symptom: Registration fails saying a property "was not found on the instance of ResilienceOptions".

> [!CAUTION] Quick fix
> The key named in the message does not exist. Check the spelling, then check whether it was renamed.

**Why this happens**: policy sections are bound with `ErrorOnUnknownConfiguration`, so a key the DTO does not have fails rather than binding nothing. Several keys have different names: `AttemptCeiling` (for `Timeouts`), `Breaker:TripWindow` (for `Breaker:Window`), and the `Backoff` and `Budget` sections (for flat keys).

The check is deliberate: a key that binds nothing leaves the policy quietly on its defaults, which is indistinguishable from a policy nobody configured. See [An unrecognized key is an error](./di/configuration.md#an-unrecognized-key-is-an-error).

### Symptom: A configuration reload does not reach the client.

**Why this happens**: A policy resolved by name on an `HttpClient` is read when the handler chain is built, and `IHttpClientFactory` rebuilds that chain every two minutes by default. The lag is intentional: the handler holds per-host breakers and budgets, and rebuilding it per request would throw that state away.

If you store a policy in a `readonly` field, reloads never reach it. Resolve policies from [`IResiliencePolicies`](./reference/options.md) per call instead.

### Symptom: `ResilienceConfigurationException` occurs at startup.

**Solution**: Read the exception's `Problems` property. It lists every configuration error at once, not just the first.

<!-- snippet: troubleshoot-validate -->
```csharp
var api = Resilience.Default with { Attempts = 0, Deadline = TimeSpan.FromSeconds(value: -1) };

var problem = Assert.Throws<ResilienceConfigurationException>(testCode: api.Validate);

Console.WriteLine(value: string.Join(separator: Environment.NewLine, values: problem.Problems));

// Attempts must be at least 1; it is 0.
// Deadline must be positive, or Timeout.InfiniteTimeSpan for no bound; it is -00:00:01.
```
<!-- endsnippet -->

DI validates policies eagerly so configuration mistakes fail at startup, not per request. With literals, the [NRES003](./reference/analyzers.md) analyzer catches them at build time.

## Performance and testing

### Symptom: A dependency is down, and my service returns 500 for every request.

> [!CAUTION] Quick fix
> Register the exception handler: `builder.Services.AddResilienceExceptionHandler()`, with `AddProblemDetails()` and `UseExceptionHandler()` alongside it.

**Why this happens**: An unhandled `DeadlineExceededException` or `CallRejectedException` becomes the framework's 500 - the response that means "this service is broken", for a failure that means "this service's dependency is broken". The handler maps them to 504 and 503, the statuses that let a caller or a gateway shed load instead of panicking.

See [Error responses](./http/error-responses.md) for the full mapping.

### Symptom: Retries are refused with `BudgetExhausted`.

**Why this happens**: The [retry budget](./features/retry-budget.md) is working as designed. Retries are funded at 10% of successful traffic, so a completely failed dependency funds no retries - which stops the client from turning an outage into a load test.

In a test hammering a dead dependency, set `Budget = RetryBudget.None`. In production, this symptom means the retry fraction has left the range where retrying helps.

### Symptom: `RateLimitedException` is thrown and nothing was sent, while the dependency looks healthy.

> [!CAUTION] Quick fix
> Lower the reserve, or turn the guard off for that client: `configureOptions: o => o.Quota = Quota.Reserving(reserve: 0)`, or `o.Quota = null`.

**Why this happens**: The dependency publishes its rate limit in response headers, and the handler is honoring it. Once the remaining allowance is inside the reserve - a tenth of the published quota by default - the next attempt is refused locally rather than sent. The `RejectedByQuota` event and log record 1030 carry the time until the published window resets.

The usual cause of an unwanted refusal is a quota published per account while several instances of your service share it: each one reads the whole allowance as its own, and each holds back a tenth of it. `Quota.Reserving(0)` refuses only once the dependency says nothing is left; `null` stops reading the headers at all.

See [the published quota](./http/index.md#honor-the-allowance-the-dependency-publishes).

### Symptom: Every measured bound loosened at once during an incident, and the dependency was fine.

> [!CAUTION] Quick fix
> Turn on saturation awareness: `Saturation = Saturation.Above(multiple: 5)`, alongside whatever measured terms the policy already has.

**Why this happens**: The library times the callback with a wall clock and attributes all of it to the dependency. When the local thread pool is the bottleneck, a work item waiting 400 ms for a thread is indistinguishable - from inside the executor - from a dependency that got 400 ms slower. So the attempt ceiling rises, the measured backoff base lengthens, and the hedge threshold climbs out of reach, all at the moment the process could least afford it. Neither the breaker nor the retry budget can see it, because the dependency really is fine.

With `Saturation` set, the policy stops feeding those estimates while the pool queues, and `Measured.QueueDelay` plus the `nresilience.pool.delay` histogram name the cause. It only declines to record - nothing is refused and no bound moves - so it does not fix the pool. It stops the pool's problem being recorded as the dependency's.

See [Local saturation](./features/saturation.md) for the whole mechanism.

### Symptom: A test is slow or flaky.

**Solution**:
- Set `Backoff = Backoff.None` for tests that only verify a retry occurred.
- Use `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`) to advance time manually for tests that assert timing. Pass the same clock to both the policy and the scripted sequence.

For more details, see [Testing](./testing/index.md).

## Observability

### Symptom: You need to see the actual sequence of events for a call.

**Solution**: Use the `AttemptLog` to inspect a call's history.

<!-- snippet: troubleshoot-attempt-log -->
```csharp
// Every failure carries its own history: on CallResult, on the exceptions the library
// invents, and on Exception.Data for an original exception it rethrew unchanged.
var result = await api.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

Console.WriteLine(value: result.Reason); // AttemptsExhausted
Console.WriteLine(value: result.Attempts); // 3 attempts over 0.9ms: Transient IOException (0.2ms), ...

foreach (var attempt in result.Attempts)
{
    Console.WriteLine(value: $"#{attempt.Number} {attempt.Verdict.Kind} after {attempt.DelayBefore.TotalMilliseconds}ms");
}
```
<!-- endsnippet -->

For exceptions the library rethrew, read the log from `Exception.Data` with `AttemptLog.Of(exception)`.

### Symptom: You cannot tell what a policy will actually do.

> [!CAUTION] Quick fix
> `Console.WriteLine(api.Explain());`

`Explain()` names the bound that binds first, lays out the worst case attempt by attempt, and reports which adaptive terms are warm. It answers the two questions a policy's properties cannot: how long can this call take, and which of these numbers is in effect right now.

It is also safe on a policy that will not validate - the timeline is usually what makes the problem obvious. See [`Explain()`](reference/resilience.md#explaining-a-policy).

### Symptom: A call is not being retried and you cannot see why.

> [!CAUTION] Quick fix
> Increase the resilience category log level: `"Logging": { "LogLevel": { "NResilience": "Debug" } }`.

Event 1007 warns the first time a policy declines to retry an exception type and names the type. If your sink does not support `Debug`, set `"Logging": "Verbose"` in the policy section to raise traffic records to `Information`.

See [Logging in DI](di/logging.md) and [the event IDs](reference/events.md#log-event-ids).

### Symptom: Your configuration section does not seem to apply.

> [!CAUTION] Quick fix
> Read event 1020 at the `Debug` level. This event names the effective policy for every registration once per resolution.

Binding a section is silently partial. A reload produces a new log entry, showing what changed.

See [Configuration](di/configuration.md) for the bindable shape and [Logging in DI](di/logging.md#provenance) for the record.

### Symptom: Your logs are full of resilience records.

> [!CAUTION] Quick fix
> Decrease the log level for the noisy policy: `"Logging": { "LogLevel": { "NResilience.reports": "Warning" } }`.

Each policy logs under `NResilience.<name>`, so you can silence one client without touching the others. To turn the listener off for a policy, set `"Logging": "Off"` in its section.

To keep the detail without the volume, sample the steady state instead: `services.AddResilienceLogging(o => o.Sampling = LogSampling.OneIn(20))` writes one traffic record in twenty while a policy is healthy and every record for a minute after its breaker opens.

See [Filter per policy](di/logging.md#filter-per-policy) and [Sample the steady state](features/logging.md#sample-the-steady-state).

### Symptom: A retry sequence is missing records in the middle.

> [!CAUTION] Quick fix
> If `Sampling` is set, set `KeepOneIn` to `1` for the run: `services.AddResilienceLogging(o => o.Sampling = LogSampling.OneIn(1))`.

Sampling drops the traffic-proportional records - the per-attempt records, the per-call records and the hedge records - while the policy is healthy, and nothing counts what it dropped. Breaker transitions, rejections and first sightings are never sampled, so a gap that stops at the attempt records is this and not a lost event.

See [Sample the steady state](features/logging.md#sample-the-steady-state).
