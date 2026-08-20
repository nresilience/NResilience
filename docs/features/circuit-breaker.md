---
title: Circuit breaker
description: An object you construct and share exactly as widely as you intend, tripping on failures, rates or brownouts.
order: 4
---

# Circuit breaker

A breaker stops calling a dependency that is failing. It is **opt-in**, and it is an object rather
than a setting, because its scope is a decision only you can make.

## Turning it on

<!-- snippet: breaker-construct -->
```csharp
// Breaker scope is a variable with a name and a lifetime. `with` copies the reference,
// so every policy derived from `payments` shares this breaker.
var breaker = new Breaker { Name = "payments" };

var payments = Resilience.Http with { Breaker = breaker };
var paymentsWrites = payments with { Attempts = 1 };
```
<!-- endsnippet -->

The breaker is an object you hold, so its scope is wherever you hold it. `with` copies the
*reference*, never the state, so two policies derived from a common ancestor share whatever breaker
that ancestor held.

For HTTP, the handler scopes a breaker per host for you. See
[per-host scope](../http/per-host-scope.md).

## What trips it

| Setting | Default | What it does |
| --- | --- | --- |
| `ConsecutiveFailures` | 5 | Consecutive failures before opening |
| `FailureRatio` | null | Optional rate-based trip, evaluated alongside the counter |
| `MinimumCalls` | 20 | Sampled calls a rate needs before it means anything |
| `Window` | 30 s | The sliding window rates are measured over |
| `SlowCallThreshold` | null | An attempt slower than this counts against `SlowCallRatio`, even when it succeeded |
| `SlowCallRatio` | 0.5 | The proportion of slow calls that opens it |
| `BreakDuration` | 15 s | How long the first break lasts |
| `MaxBreakDuration` | 2 min | The break doubles on each consecutive open, up to this |
| `HalfOpenProbes` | 1 | Concurrent trial calls while recovering |
| `ProbeSuccesses` | 2 | Successful probes required to close |

<!-- snippet: breaker-slow-calls -->
```csharp
// The most common real degradation is not errors, it is a dependency answering 200s at
// 30x normal latency. An error-rate breaker sits closed through the whole incident.
var breaker = new Breaker(new BreakerSettings
{
    ConsecutiveFailures = 5,                             // the default trip condition
    SlowCallThreshold = TimeSpan.FromSeconds(2),         // anything slower counts against
    SlowCallRatio = 0.5,                                 // half the window being slow trips it
    MinimumCalls = 20,                                   // below this, a ratio means nothing
    Window = TimeSpan.FromSeconds(30),
    BreakDuration = TimeSpan.FromSeconds(15),            // doubles per consecutive open
    MaxBreakDuration = TimeSpan.FromMinutes(2),
    ProbeSuccesses = 2,                                  // two good probes to close, not one
})
{
    Name = "search",
};
```
<!-- endsnippet -->

Consecutive failures is the default trip condition because it is the reading most people have of
"circuit breaker", and because a rate-based trip needs traffic a median .NET service does not have.

The breaker samples individual **attempts**, always - so "does it see attempts or whole operations?"
has one answer rather than depending on composition order. Only `Transient` outcomes count as
evidence: a `Throttled` response means the dependency is working correctly and defending itself, and
a `Permanent` one is overwhelmingly a client-side fact.

Slow calls matter because the most common real degradation is not a dependency returning errors, it
is a dependency returning 200s at 30 times normal latency while your thread pool and connection pool
fill up.

## What a refused call looks like

<!-- snippet: breaker-rejection -->
```csharp
// A refused call reports itself rather than the dependency's last exception, and it says
// which guard refused it. RetryAfter is there so a caller that schedules its own polling
// does not have to guess.
if (result.Exception is CallRejectedException rejection)
{
    Console.WriteLine(rejection.Reason);      // DependencyUnavailable, or BudgetExhausted
    Console.WriteLine(rejection.RetryAfter);  // when to come back, when there is an answer
}
```
<!-- endsnippet -->

A refusal is not fail-fast: it serves a short pause first, because a cheap rejection inside a
caller's polling loop is a CPU spin. `StopReason.DependencyUnavailable` is the breaker's refusal;
`BudgetExhausted` is the [budget's](retry-budget.md).

Go deeper: [Guarded rejection](../deep-dives/guarded-rejection.md).

## Reading and driving it

<!-- snippet: breaker-admin -->
```csharp
BreakerState state = breaker.State;         // Closed, Open, HalfOpen or Isolated
DateTimeOffset? since = breaker.OpenedAt;   // null while it is closed

breaker.Isolate();                          // force it open and keep it there
breaker.Reset();                            // close it and forget the history
```
<!-- endsnippet -->

`State` reports `HalfOpen` for an open breaker whose break has already elapsed, because that is what
the next call will find - and reading it never consumes the probe slot a real call needs. `Isolate`
forces it open until someone calls `Reset`; neither raises an event, because there is no call to
attribute them to.

Transitions arrive as `BreakerOpened`, `BreakerClosed` and `BreakerHalfOpened`
[events](telemetry.md) on the call that caused them.

Go deeper: [Breaker internals](../deep-dives/breaker-internals.md).

