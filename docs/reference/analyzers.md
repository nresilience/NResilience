---
title: Analyzers
description: Reference for the built-in diagnostics that help ensure correct and efficient use of NResilience.
order: 12
---

# Analyzers

Installing `NResilience` automatically includes seven diagnostics. These analyzers are shipped within the package to catch common reliability and performance issues - such as failures to propagate cancellation tokens - that might otherwise be invisible during code review.

| Rule | Description | Category | Default Severity |
| :--- | :--- | :--- | :--- |
| [NRES001](#nres001) | The attempt's cancellation token is not passed to the work. | Reliability | Warning |
| [NRES002](#nres002) | A different cancellation token is passed inside the callback. | Reliability | Warning |
| [NRES003](#nres003) | The policy will not pass validation. | Usage | Warning |
| [NRES004](#nres004) | `AttemptTimeout` is longer than `Deadline`. | Usage | Warning |
| [NRES005](#nres005) | A breaker, retry budget, policy scope, or gRPC interceptor is created per call. | Reliability | Warning |
| [NRES006](#nres006) | A resilient `HttpClient` is created per call. | Reliability | Info |
| [NRES007](#nres007) | The callback does not need to be `async`. | Performance | Info |

Rules `NRES001` and `NRES002` include automated code fixes.

## NRES001: Token not passed to work

This rule is reported when a resilience callback fails to pass its `CancellationToken` parameter to an internal call that accepts one. Without this token, the [executor](index.md) cannot stop the work when an attempt timeout occurs.

```csharp
// Reported: GetFromJsonAsync takes a token, but none is provided.
await api.RunAsync(attempt => client.GetFromJsonAsync<User>(url), cancellationToken);

// Not reported: the internal call does not accept a token.
await api.RunAsync(attempt => Task.FromResult(cached), cancellationToken);
```

The analyzer is quiet if the token is used anywhere within the callback. A body that threads it into one call and forgets a second is [CA2016](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2016)'s subject, and guessing which of the two omissions was meant is how a rule earns a `NoWarn`. For comprehensive token propagation, see CA2016.

## NRES002: Incorrect token passed

This rule is reported when a callback ignores its own token and instead passes a different token - such as the caller's token, `CancellationToken.None`, or `default` - to an internal call. This renders the attempt timeout ineffective.

```csharp
// Reported: the attempt timeout has no effect on this call.
await api.RunAsync(attempt => client.GetAsync(url, cancellationToken), cancellationToken);

// The fix: pass the 'attempt' token.
await api.RunAsync(attempt => client.GetAsync(url, attempt), cancellationToken);
```

Where the parameter was written as `_`, the fix names it first, because a token you cannot refer to cannot be passed.

Both `NRES001` and `NRES002` analyze lambdas passed to `RunAsync` or `TryRunAsync`. Method groups (e.g., `api.RunAsync(FetchAsync, cancellationToken)`) are ignored because the analyzer cannot guarantee visibility of the method body. For more information, see the [cancellation contract](../deep-dives/cancellation.md).

## NRES003: Validation failure

This rule performs a build-time check for the same logic used in [`Validate()`](resilience.md). It reports policies with `Attempts` below 1, or a `Deadline` or `AttemptTimeout` that is neither positive nor `Timeout.InfiniteTimeSpan`.

```csharp
// Reported: Attempts must be at least 1, and Deadline must be positive.
var api = Resilience.Default with { Attempts = 0, Deadline = TimeSpan.FromSeconds(-1) };
```

The analyzer folds constants like `TimeSpan.FromSeconds(2)`, `new TimeSpan(0, 0, 30)`, `TimeSpan.Zero`, and `Timeout.InfiniteTimeSpan`. Values the compiler cannot resolve are left to the runtime `Validate()` method.

## NRES004: Attempt timeout exceeds deadline

This rule is reported when `AttemptTimeout` is longer than `Deadline`. While this is technically legal and passes validation, it is misleading: the overall deadline caps every single attempt, meaning an attempt timeout larger than the deadline can never be reached.

```csharp
// Reported: the attempt is effectively capped at 5 seconds, not 10.
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(5), AttemptTimeout = TimeSpan.FromSeconds(10) };
```

This is only reported when both properties are set within the same expression.

## NRES005: Guard created per call

Circuit breakers, retry budgets, policy scopes, and the gRPC [`ResilienceInterceptor`](../grpc/per-service-scope.md) must outlive the call they protect. A breaker created inside a method body never sees a prior failure and never opens. A [`PolicyScope<TKey>`](../features/policy-scope.md) or a `ResilienceInterceptor` created inside a method body fails the same way one level up, because every call receives a fresh set of guards - `AddGrpcResilience()` registers the interceptor at channel scope for exactly this reason.

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

This rule reports when a callback is marked `async` but only contains a single `await` whose task the callback can return directly. Removing the `async` keyword avoids the allocation of an unnecessary state machine.

```csharp
// Reported: unnecessary state machine for a single await.
await api.RunAsync(async attempt => await client.GetAsync(url, attempt), cancellationToken);

// The fix: return the task directly.
await api.RunAsync(attempt => client.GetAsync(url, attempt), cancellationToken);
```

This rule also reports callbacks that await a `ValueTask`, providing additional savings. Removing `async` re-binds the call to the [`ValueTask` overloads](resilience.md#methods), eliminating both the state machine and the task allocation for synchronously completing calls:

```csharp
// Reported: the state machine, plus a task built for a buffered read.
await api.RunAsync(async attempt => await reader.ReadAsync(attempt), cancellationToken);

// The fix: the same call, on the ValueTask overload.
await api.RunAsync(attempt => reader.ReadAsync(attempt), cancellationToken);
```

Two patterns are ignored to avoid changing the program's behavior:

- A callback whose return type is **written down**, such as `async Task<int> (attempt) => await reader.ReadAsync(attempt)`. The explicit return type prevents the compiler from re-resolving the call, so a `ValueTask` body would not compile.
- A callback that **discards** a `ValueTask<T>` result, such as `async attempt => { await reader.ReadAsync(attempt); }`. While this rewrite compiles, it moves the call from the void overload to the generic one, causing the result to be passed to the [classifier](classifier.md).

For details on allocations, see [where the allocations are](../deep-dives/allocations.md).

## Manage analyzer severity

You can control analyzer severity using a `.editorconfig` file:

```ini
[*.cs]
dotnet_diagnostic.NRES006.severity = none
dotnet_diagnostic.NRES001.severity = error
```

Alternatively, you can disable a rule at a specific site using `#pragma` directives:

```csharp
#pragma warning disable NRES003 // invalid on purpose for test assertion
var api = Resilience.Default with { Attempts = 0 };
#pragma warning restore NRES003
```

The rule ids are a contract: adding, renaming, or re-severity-ing one is a reviewed diff in `AnalyzerReleases.Unshipped.md`, the way a member is in `PublicAPI.Unshipped.txt`.
