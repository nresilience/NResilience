---
title: Analyzers
description: The seven diagnostics that ship inside the package, what each one reports, and how to turn one off.
order: 12
---

# Analyzers

Installing `NResilience` installs seven diagnostics. They ship inside the package rather than beside
it, in `analyzers/dotnet/cs`, because the first two catch a failure that has no symptom: work that
never sees the attempt's cancellation token cannot be stopped by the attempt timeout, and the call
looks correct in review either way.

Nothing to configure. A project that does not reference the library resolves no symbols and
registers no callbacks.

| Rule | Reports | Category | Default |
| --- | --- | --- | --- |
| [NRES001](#nres001) | The attempt's cancellation token is not passed to the work | Reliability | Warning |
| [NRES002](#nres002) | A different cancellation token is passed inside the callback | Reliability | Warning |
| [NRES003](#nres003) | The policy will not pass `Validate()` | Usage | Warning |
| [NRES004](#nres004) | `AttemptTimeout` is longer than `Deadline` | Usage | Warning |
| [NRES005](#nres005) | A breaker or retry budget is created per call | Reliability | Warning |
| [NRES006](#nres006) | A resilient `HttpClient` is created per call | Reliability | Info |
| [NRES007](#nres007) | The callback does not need to be `async` | Performance | Info |

NRES001 and NRES002 come with a code fix.

## NRES001

**The attempt's cancellation token is not passed to the work.**

Reported when a callback never mentions its `CancellationToken` parameter and something inside it
takes one and was not given it.

```csharp
// Reported: GetFromJsonAsync takes a token, and did not get one.
await api.RunAsync(attempt => client.GetFromJsonAsync<User>(url), cancellationToken);

// Not reported: there is nothing to pass it to.
await api.RunAsync(attempt => Task.FromResult(cached), cancellationToken);
```

The rule is deliberately quiet once the token is used anywhere in the callback: a body that threads
it into one call and forgets a second is [CA2016](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2016)'s
subject, and guessing which of the two omissions was meant is how a rule earns a `NoWarn`.

## NRES002

**A different cancellation token is passed inside the callback.**

Reported when a callback never mentions its own token and hands some other one - the caller's,
`CancellationToken.None`, `default` - to a call inside it.

```csharp
// Reported: the attempt timeout has no effect on this call.
await api.RunAsync(attempt => client.GetAsync(url, cancellationToken), cancellationToken);

// The fix.
await api.RunAsync(attempt => client.GetAsync(url, attempt), cancellationToken);
```

Where the parameter was written as `_`, the fix names it first, because a token you cannot refer to
cannot be passed.

Both rules read the lambda handed to `RunAsync` or `TryRunAsync`. A method group -
`api.RunAsync(FetchAsync, cancellationToken)` - is left alone: the body may be in another assembly,
and a diagnostic that appears only when the source happens to be visible is worse than one that
stays quiet. See [the cancellation contract](../deep-dives/cancellation.md) for what the two tokens
are.

## NRES003

**The policy will not pass validation.**

The literal half of [`Validate()`](resilience.md), at build time: `Attempts` below 1, and a
`Deadline` or `AttemptTimeout` that is neither positive nor `Timeout.InfiniteTimeSpan`.

```csharp
// Reported twice, and it throws ResilienceConfigurationException on first execution.
var api = Resilience.Default with { Attempts = 0, Deadline = TimeSpan.FromSeconds(-1) };
```

`TimeSpan.FromSeconds(2)`, `new TimeSpan(0, 0, 30)`, `TimeSpan.Zero` and `Timeout.InfiniteTimeSpan`
are folded; a value the compiler cannot see is left to `Validate()`.

## NRES004

**`AttemptTimeout` is longer than `Deadline`.**

Legal, validates, and cannot do what it looks like it does: the deadline covers the whole call and
caps every attempt inside it, so an attempt timeout above it is never reached.

```csharp
// Reported: the attempt is capped at 5 seconds, not 10.
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(5), AttemptTimeout = TimeSpan.FromSeconds(10) };
```

Only reported when both are set in the same expression. Reaching back through a preset for the
inherited deadline would mean guessing which one it came from. See
[deadlines](../features/deadlines.md).

## NRES005

**A breaker or retry budget created per call keeps no state.**

A breaker counts consecutive failures; a budget counts deposits over a window. Both exist to outlive
the call, so one built inside a method has never seen a failure and never opens.

```csharp
// Reported: a new breaker per call, which is a breaker that never opens.
static Resilience Payments() => Resilience.Http with { Breaker = new Breaker() };

// Not reported: one breaker, held.
static readonly Resilience Payments = Resilience.Http with { Breaker = new Breaker() };
```

Reported only for a guard written directly into a policy's initializer inside a method body. A guard
that is a local or a parameter first may be on its way to something that keeps it, and startup code
- the entry point, a static initializer, a field initializer - is doing once what a called method
would do per call. `RetryBudget.Shared(name)` is looked up by name, so asking for it per call is
correct and is not reported.

## NRES006

**A resilient `HttpClient` created per call discards its per-host state.**

```csharp
// Reported: the handler's per-host breakers and budgets die with the client.
using HttpClient client = ResilienceHttp.CreateClient();
```

Reported only for the `using` form, where the client provably does not outlive the method, and never
in the entry point. A client the method returns is not per call. See
[the HTTP handler](../http/index.md) and [`AddResilience()`](../di/index.md).

## NRES007

**The callback does not need to be `async`.**

```csharp
// Reported: a state machine per attempt, for one await.
await api.RunAsync(async attempt => await client.GetAsync(url, attempt), cancellationToken);

// The same call, without it.
await api.RunAsync(attempt => client.GetAsync(url, attempt), cancellationToken);
```

The execution overloads already take a `Task`-returning delegate, and the [executor](index.md) invokes the
callback inside the same `try` that classifies its outcome - so a callback that throws
synchronously is classified exactly as a faulted task is, and dropping `async` changes nothing but
the allocation. Reported only when the whole body is one `await` whose task is already the
delegate's return type; a `ValueTask`, or a configured awaiter, is a different type and keeps its
state machine. Go deeper: [where the allocations are](../deep-dives/allocations.md).

## Turning one off

Standard Roslyn severity control - per rule, per project, in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.NRES006.severity = none
dotnet_diagnostic.NRES001.severity = error
```

Or at a single site, where the code is deliberate:

```csharp
#pragma warning disable NRES003 // invalid on purpose: this test asserts the message
var api = Resilience.Default with { Attempts = 0 };
#pragma warning restore NRES003
```

The rule ids are a contract: adding, renaming or re-severity-ing one is a reviewed diff in
`AnalyzerReleases.Unshipped.md`, the way a member is in `PublicAPI.Unshipped.txt`.
