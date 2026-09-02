---
title: Analyzers
description: Reference for the built-in diagnostics that help ensure correct and efficient use of NResilience.
order: 12
---

# Analyzers

Installing `NResilience` gives you seven diagnostics automatically. They ship inside the package to catch common reliability and performance issues - failures to propagate cancellation tokens, for example - that are invisible in code review.

| Rule | Description | Category | Default Severity |
| :--- | :--- | :--- | :--- |
| [NRES001](#nres001) | The attempt's cancellation token is not passed to the work. | Reliability | Warning |
| [NRES002](#nres002) | A different cancellation token is passed inside the callback. | Reliability | Warning |
| [NRES003](#nres003) | The policy will not pass validation. | Usage | Warning |
| [NRES004](#nres004) | `AttemptTimeout` is longer than `Deadline`. | Usage | Warning |
| [NRES005](#nres005) | A breaker, retry budget, policy scope, or gRPC interceptor is created per call. | Reliability | Warning |
| [NRES006](#nres006) | A resilient `HttpClient` is created per call. | Reliability | Info |
| [NRES007](#nres007) | The callback does not need to be `async`. | Performance | Info |
| [NRES008](#nres008) | A policy configuring `Hedge` or `Timeouts` is created per call. | Reliability | Info |

Rules `NRES001` and `NRES002` include automated code fixes.

## NRES001: Token not passed to work

Reported when a resilience callback fails to pass its `CancellationToken` parameter to an internal call that accepts one. Without that token, the [executor](index.md) cannot stop the work when an attempt timeout hits.

```csharp
// Reported: GetFromJsonAsync takes a token, but none is provided.
await api.RunAsync(attempt => client.GetFromJsonAsync<User>(url), cancellationToken);

// Not reported: the internal call does not accept a token.
await api.RunAsync(attempt => Task.FromResult(cached), cancellationToken);
```

The analyzer stays quiet if the token is used anywhere in the callback. A body that threads it into one call and forgets a second is [CA2016](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2016)'s subject - guessing which of two omissions was meant is how a rule earns a `NoWarn`. See CA2016 for comprehensive token propagation.

## NRES002: Incorrect token passed

Reported when a callback ignores its own token and passes a different one - the caller's token, `CancellationToken.None`, or `default` - to an internal call. That makes the attempt timeout ineffective.

```csharp
// Reported: the attempt timeout has no effect on this call.
await api.RunAsync(attempt => client.GetAsync(url, cancellationToken), cancellationToken);

// The fix: pass the 'attempt' token.
await api.RunAsync(attempt => client.GetAsync(url, attempt), cancellationToken);
```

Where the parameter was written as `_`, the fix names it first - a token you cannot refer to cannot be passed.

Both `NRES001` and `NRES002` analyze lambdas passed to `RunAsync` or `TryRunAsync`. Method groups (`api.RunAsync(FetchAsync, cancellationToken)`) are ignored, because the analyzer cannot guarantee visibility of the method body. See the [cancellation contract](../deep-dives/cancellation.md).

## NRES003: Validation failure

A build-time check running the same logic as [`Validate()`](resilience.md). It reports policies with `Attempts` below 1, or a `Deadline` or `AttemptTimeout` that is neither positive nor `Timeout.InfiniteTimeSpan`.

```csharp
// Reported: Attempts must be at least 1, and Deadline must be positive.
var api = Resilience.Default with { Attempts = 0, Deadline = TimeSpan.FromSeconds(-1) };
```

The analyzer folds constants like `TimeSpan.FromSeconds(2)`, `new TimeSpan(0, 0, 30)`, `TimeSpan.Zero`, and `Timeout.InfiniteTimeSpan`. Values the compiler cannot resolve are left to the runtime `Validate()`.

## NRES004: Attempt timeout exceeds deadline

Reported when `AttemptTimeout` is longer than `Deadline`. Legal, and it passes validation, but misleading: the overall deadline caps every attempt, so an attempt timeout larger than the deadline can never be reached.

```csharp
// Reported: the attempt is effectively capped at 5 seconds, not 10.
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(5), AttemptTimeout = TimeSpan.FromSeconds(10) };
```

This is only reported when both properties are set within the same expression.

## NRES005: Guard created per call

Circuit breakers, retry budgets, policy scopes, and the gRPC [`ResilienceInterceptor`](../grpc/per-service-scope.md) must outlive the call they protect. A breaker created inside a method body never sees a prior failure and never opens. A [`PolicyScope<TKey>`](../features/policy-scope.md) or a `ResilienceInterceptor` created inside a method body fails the same way, one level up: every call receives a fresh set of guards. `AddGrpcResilience()` registers the interceptor at channel scope for exactly this reason.

```csharp
// Reported: a new breaker is created every time the method is called.
static Resilience Payments() => Resilience.Http with { Breaker = new Breaker() };

// Not reported: the breaker is held in a static field.
static readonly Resilience Payments = Resilience.Http with { Breaker = new Breaker() };
```

```csharp
// Reported: the scope, and everything it keys, dies with the call.
static Resilience For(string tenant) => new PolicyScope<string>(Template).For(tenant);

// Not reported: the scope is held in a static field.
static readonly PolicyScope<string> Tenants = new(Template);
```

This rule reports guards written directly into a policy's initializer inside a method. It ignores locals or parameters that may be stored elsewhere. A policy scope is reported if it provably dies with the call, such as when used immediately or held in a local that does not leave the method. `RetryBudget.Shared(name)` is ignored because it is looked up by name.

## NRES006: HttpClient created per call

Creating a resilient `HttpClient` within a `using` block inside a method causes the handler's per-host breakers and budgets to be discarded immediately after the call.

```csharp
// Reported: the handler's per-host state dies with the client.
using HttpClient client = ResilienceHttp.CreateClient();
```

This is reported only for `using` forms where the client provably does not outlive the method. For the correct approach, see [the HTTP handler](../http/index.md) and [`AddResilience()`](../di/index.md).

## NRES007: Redundant async callback

Reported when a callback is marked `async` but contains only a single `await` whose task the callback could return directly. Dropping `async` avoids the state-machine allocation.

```csharp
// Reported: unnecessary state machine for a single await.
await api.RunAsync(async attempt => await client.GetAsync(url, attempt), cancellationToken);

// The fix: return the task directly.
await api.RunAsync(attempt => client.GetAsync(url, attempt), cancellationToken);
```

This rule also reports callbacks that await a `ValueTask`, which saves more: dropping `async` re-binds the call to the [`ValueTask` overloads](resilience.md#methods), eliminating both the state machine and the task allocation for synchronously completing calls:

```csharp
// Reported: the state machine, plus a task built for a buffered read.
await api.RunAsync(async attempt => await reader.ReadAsync(attempt), cancellationToken);

// The fix: the same call, on the ValueTask overload.
await api.RunAsync(attempt => reader.ReadAsync(attempt), cancellationToken);
```

Two patterns are ignored to avoid changing behavior:

- A callback whose return type is **written down**, such as `async Task<int> (attempt) => await reader.ReadAsync(attempt)`. The explicit return type stops the compiler from re-resolving the call, so a `ValueTask` body would not compile.
- A callback that **discards** a `ValueTask<T>` result, such as `async attempt => { await reader.ReadAsync(attempt); }`. The rewrite compiles, but it moves the call from the void overload to the generic one, which sends the result to the [classifier](classifier.md).

For the allocation details, see [where the allocations are](../deep-dives/allocations.md).

## NRES008: Policy with a latency estimate created per call

[`Hedge`](../features/hedging.md) and [`Timeouts`](../features/deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it) both measure a quantile of recent latency, and that estimate is held per policy **instance** - which is the scope the feature wants, because one host's p95 is not another's. A policy rebuilt on every call therefore starts cold every time, never reaches `MinimumSamples`, and the feature silently does nothing.

```csharp
// Reported: a new policy, and so a new latency estimate, on every call.
static Resilience Search() => Resilience.Http with { Hedge = Hedge.At(0.95) };

// Not reported: one instance for the lifetime of the process.
static readonly Resilience Search = Resilience.Http with { Hedge = Hedge.At(0.95) };
```

`Info` rather than a warning, for the reason `NRES006` is: a policy written inline in a method is a common and often deliberate shape, and nothing here is less safe than the same policy without the feature. The hedge simply does not fire, and the attempt ceiling stays at `AttemptTimeout`. It is dead configuration rather than a hazard - but it is dead configuration you paid for and cannot see.

Two limits are deliberate:

- The rule reports a policy that **sets** `Hedge` or `Timeouts` in the expression the compiler can see. `Api with { Deadline = budget }`, where the estimator was configured on `Api`, is not reported - establishing that would mean following the referenced symbol, and a rule that is merely usually right about a shape this common is a rule people turn off.
- Setting either property to `null` is not reported. That removes the feature rather than configuring one, and it is how the HTTP handler builds its own single-shot policy.

If you need a per-request bound on a policy that carries an estimator, prefer `ResilienceDeadline.Begin` with `UseAmbientDeadline` over deriving a policy per request. See [deadline propagation](../features/deadlines.md#propagate-the-deadline-across-a-hop).

## Manage analyzer severity

Control severity with a `.editorconfig` file:

```ini
[*.cs]
dotnet_diagnostic.NRES006.severity = none
dotnet_diagnostic.NRES001.severity = error
```

Alternatively, disable a rule at a specific site with `#pragma` directives:

```csharp
#pragma warning disable NRES003 // invalid on purpose for test assertion
var api = Resilience.Default with { Attempts = 0 };
#pragma warning restore NRES003
```

The rule ids are a contract: adding, renaming, or re-severity-ing one is a reviewed diff in `AnalyzerReleases.Unshipped.md`, the way a member is in `PublicAPI.Unshipped.txt`.
