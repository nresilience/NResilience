# NResilience

Add retry, timeouts, and circuit breaking to your .NET calls - with defaults that work out of the box.

## What it gives you

- Automatic retries with backoff and jitter when a call fails transiently
- Timeouts so a slow dependency can't hang your application
- A circuit breaker that stops calling a failing service
- A retry budget that prevents retries from overwhelming a struggling dependency
- HTTP-aware out of the box (knows a 503 is retryable, a 404 is not)
- Works with zero configuration - sensible defaults are already on

## Quick start

```bash
dotnet add package NResilience
```

<!-- snippet: quick-start-http-client -->
```csharp
// One client for the application's lifetime, with the policy already inside it.
private static readonly HttpClient Client = ResilienceHttp.CreateClient();

private static async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken) =>
    await Client.GetFromJsonAsync<User>(new Uri($"https://api.example.com/users/{id}"), cancellationToken);
```
<!-- endsnippet -->

Every call that client makes now retries up to three times with exponential backoff, gives up
entirely after 30 seconds, and knows that a 503 is worth retrying but a 404 is not. You didn't
configure any of that, and there is one cancellation token to pass - your own.

## Tuning a policy

Change one setting and keep the rest:

```csharp
var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };
```

Everything you didn't mention carries over from the preset.

## Any call, not just HTTP

HTTP gets a client because it is the common case. Everything else - a queue read, a database call, a
third-party SDK - goes through `RunAsync`, which takes the work as a callback:

<!-- snippet: quick-start-run-any-call -->
```csharp
var api = Resilience.Default;

string name = await api.RunAsync(attempt => db.ReadNameAsync(id, attempt), cancellationToken);
```
<!-- endsnippet -->

Two cancellation tokens appear there, and they are different things. `attempt` is cancelled when that
attempt hits its `AttemptTimeout`; `cancellationToken` is yours, and cancels the whole call. Passing
`attempt` into your work is what lets a timed-out attempt actually stop, so every overload requires a
callback that takes it - there is no zero-argument form to forget.

## Handling failure

When you'd rather check the result than catch an exception, use `TryRunAsync`:

```csharp
CallResult<User> result = await api.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);
User best = result.TryGetValue(out User? fetched) ? fetched : cache.LastKnownGood;
```

You get the value if it succeeded, or the reason and the attempt history if it didn't - and you
decide what to do with an `if`.

## The whole API at a glance

Here are all four patterns you just saw, together:

<!-- snippet: whole-api -->
```csharp
// 1. A policy is a value. Presets are the entry point.
var api = Resilience.Http;

// 2. Derive with `with`. No builder, no Build(), no ordering to get right.
var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };

// 3. Run anything. One method, any return type, nothing to declare.
User? user = await api.RunAsync(attempt => client.GetFromJsonAsync<User>(url, attempt), cancellationToken);
HttpResponseMessage response = await api.RunAsync(attempt => client.GetAsync(url, attempt), cancellationToken);
await slow.RunAsync(attempt => queue.FlushAsync(attempt), cancellationToken);

// 4. Fallback is not a strategy. It is an `if`.
CallResult<User> result = await api.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);
User best = result.TryGetValue(out User? fetched) ? fetched : cache.LastKnownGood;
```
<!-- endsnippet -->

The whole thing is one value and one method - no fluent builder to learn, no strategy ordering to
get right.

## Why NResilience

- **Configure with `with` expressions, not a fluent builder.** Change one setting, keep the rest.
  No `Build()` call, no ordering to get right.
- **One method for any return type.** `RunAsync` works whether you're calling an HTTP API, reading a
  queue, or returning `void`. The policy doesn't change.
- **Sensible defaults, on by default.** Three retries, jittered backoff, a 30-second deadline, HTTP
  status classification, and a retry budget. Nothing to configure for a working retried HTTP call.
- **Handle failure with an `if`, not a strategy.** `TryRunAsync` gives you the outcome, the reason,
  and the attempt history. Decide what to do at the call site.
- **Low overhead.** One flat execution path means cost doesn't grow with how much policy you
  configure.
- **AOT and trimming safe.** No reflection, zero external dependencies, CI-enforced on `net8.0` and
  `net10.0`.

## What it costs

Bytes above an identical un-wrapped callback, measured in one process on .NET 8 and .NET 10. Every
figure is a test that fails the build.

| Scenario | Overhead |
|---|---:|
| No policy, any call | **0** |
| A call that retries, suspends | **384 B** |
| Polly retry + timeout, same call | 1,291 B |

Go deeper: [where the allocations are](docs/deep-dives/allocations.md).

## Packages

| Package | When you need it |
|---|---|
| `NResilience` | The core library and HTTP handler. Start here. |
| `NResilience.Extensions` | Dependency injection, configuration binding, metrics. Add this in a hosted app. |
| `NResilience.Testing` | Helpers for testing your policies. |

## Documentation

- [Quick start](docs/getting-started/quick-start.md) - a retried HTTP call in two minutes.
- [Key concepts](docs/getting-started/key-concepts.md) - the five words the rest of the docs use.
- [Guides](docs/guides/index.md) - worked scenarios.
- [Features](docs/features/index.md) - one page per knob.
- [Reference](docs/reference/index.md) - every member, in order.
- [Deep dives](docs/deep-dives/index.md) - why the library is built this way.
- [Migrating from Polly](docs/migrating-from-polly.md) - a translation table and the behavior
  differences worth knowing.
- [Troubleshooting](docs/troubleshooting.md) and the [FAQ](docs/faq.md).
- [Samples](docs/samples.md) - three runnable console applications.

## Contributing

Project layout, build and test commands, and the design documents live in
[CONTRIBUTING.md](CONTRIBUTING.md).

## Status

Stable. Targets `net8.0` and `net10.0`; both run in CI; Native AOT publishes clean on both.