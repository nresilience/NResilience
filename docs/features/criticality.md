---
title: Criticality
description: Carry how much a call matters across a service boundary, so a backfill stops spending the retry capacity a checkout needs.
order: 13
---

# Criticality

A backfill and a checkout draw on the same retry budget and the same hedging capacity. Nothing in .NET says which is which, so the executor treats them as equal - and the service three hops down sheds the checkout and serves the backfill.

**Criticality** is a four-level label that travels with the request. The library does exactly two things with it: it refuses to spend a depleted retry budget on `Sheddable` work, and it never hedges `Sheddable` work. It does not shed, it does not reorder, and it does not decide what a level means for your service.

It is **opt-in** on both halves, for the reason [deadline propagation](deadlines.md#propagate-the-deadline-across-a-hop) is: reading the level costs something on calls that have no level, and a header is only useful when the other side reads it.

## The four levels

| Level | Meaning | What the library does |
| :--- | :--- | :--- |
| `Sheddable` | Batch, backfill, prefetch. Nobody is waiting. | Never hedged; refused a retry once the budget is more than half spent |
| `SheddablePlus` | Degrades a feature, not the request. | Nothing |
| `Critical` | The default when nothing said. | Nothing |
| `CriticalPlus` | A user is waiting and there is no fallback. | Nothing |

Two guardrails are easy to miss and both are load-bearing:

- **An unlabeled call is `Critical`, not `Sheddable`.** A default that sheds is a default that loses requests during the first incident after an upgrade.
- **`CriticalPlus` cannot arrive from the wire.** A caller that could escalate itself would, and then the level means nothing. Inbound values clamp at `Critical`; only local code reaches the top level.

## Inherit the level

Set `UseAmbientCriticality` on the policy:

<!-- snippet: criticality-inherit -->
```csharp
// The inbound half. The policy reads how much this call matters, and two things follow: a
// Sheddable call is never hedged, and it is refused a retry once the retry budget is more
// than half spent. Every other level behaves exactly as it does without this.
var api = Resilience.Http with { UseAmbientCriticality = true };

// In an ASP.NET Core app, UseResilienceDeadline() reads what the caller sent. Anywhere else -
// a queue consumer reading a level off a message, or a backfill labeling its own work -
// publish it yourself.
using var scope = AmbientCriticality.Begin(criticality: Criticality.Sheddable);
```
<!-- endsnippet -->

Nothing else in the model changes. A `Sheddable` call still retries, still honors its deadline, still reports the same events, and still fails the same way - what it loses is the ability to amplify while capacity is scarce.

**The retry gate.** A `Sheddable` retry has to leave half the [retry budget](retry-budget.md) behind. While the dependency is healthy the bucket is full and nothing is refused; once it starts draining, the backfill stops and the capacity that remains is there for work someone is waiting on. A refusal is the ordinary budget rejection - `RejectedByBudget`, `StopReason.BudgetExhausted`, the same guarded pause - because criticality changes *when* a refusal happens rather than what it is.

**The hedge gate.** A `Sheddable` call is never hedged, whatever the latency estimate says. [Hedging](hedging.md) spends capacity to buy latency, and there is no latency worth buying when nobody is waiting for the answer. No hedge is armed, so no `HedgeSuppressed` event fires either: a suppression is a judgment about whether hedging is working, and this is a bound on the call, like an open breaker.

Only the bottom level is gated. `SheddablePlus` degrades a feature rather than the request, so something is still waiting for it, and it behaves exactly as `Critical` does.

A policy with nothing for a level to gate is refused at validation rather than ignored:

<!-- snippet: criticality-validate -->
```csharp
// Refused at validation rather than ignored. The two consumers are the hedge and the retry
// budget, so a single-attempt policy with no hedge has nothing for a level to gate - and a
// policy that silently does nothing is how you end up believing your backfill is holding back.
var api = Resilience.Default with { Attempts = 1, UseAmbientCriticality = true };

var problems = Assert.Throws<ResilienceConfigurationException>(testCode: api.Validate);
```
<!-- endsnippet -->

## Send the level

Set `PropagateCriticality` on the HTTP options, and every request carries the level this call is running at:

<!-- snippet: criticality-propagate -->
```csharp
// The outbound half. Every request carries the level this call is running at, so the service
// three hops down can tell a backfill from a checkout. Off by default.
var options = new HttpResilienceOptions { PropagateCriticality = true };

using var scope = AmbientCriticality.Begin(criticality: Criticality.Sheddable);
using var client = new HttpClient(handler: new HttpResilienceHandler(innerHandler: transport, policy: Resilience.Http, options: options));
using var response = await client.GetAsync(requestUri: uri, cancellationToken: cancellationToken);

// X-Criticality: Sheddable, on every attempt of the call.
```
<!-- endsnippet -->

The value is one of the four level names, written to `CriticalityHeader`, which defaults to `X-Criticality`. It is the same on every attempt: a backfill does not become a checkout because its first attempt failed. A peer that ignores the header is unaffected.

The two halves are independent. Sending a level does not require reading one, and a service can read levels without forwarding them.

## Read the level in ASP.NET Core

Install `NResilience.AspNetCore`. The [deadline middleware](deadlines.md#inherit-the-deadline) reads both propagated values in one pass:

```csharp
app.UseResilienceDeadline();
```

`ReadCriticality` turns the second half off, and `CriticalityHeader` changes the header it reads. A value this service does not recognize leaves the request at `Critical`.

## Decide what a level means for your service

Shedding stays with you, and that is deliberate: what `Sheddable` should cost depends on what the service does, which is not something a library can know. [`Admit`](../reference/resilience.md) is where the decision goes, and what this feature adds is a value for `Admit` to read rather than a guess to make:

<!-- snippet: criticality-admit -->
```csharp
// The library never sheds. What a level means for a given service is a decision it has to
// make itself, and Admit is where that decision goes - it reads a value that traveled with
// the request rather than guessing one.
var api = TestPolicy.Instant with
{
    Admit = _ => Task.FromResult(result: AmbientCriticality.Current < Criticality.Critical && Overloaded()
        ? Verdict.Refused(retryAfter: TimeSpan.FromSeconds(value: 1))
        : Verdict.Ok),
};

using var scope = AmbientCriticality.Begin(criticality: Criticality.Sheddable);
var result = await api.TryRunAsync(work: _ => Task.FromResult(result: 1), cancellationToken: cancellationToken);
```
<!-- endsnippet -->

The same value is available to a rate limiter partition, a queue consumer choosing what to acknowledge, or a handler picking a cheaper code path.

## Publish a level outside a request

`AmbientCriticality.Begin` publishes a level for the current logical call and everything it awaits. Anything that is not an inbound HTTP request publishes it directly - a backfill job labeling its own work, a queue consumer reading a level off a message, or a test:

<!-- snippet: criticality-default -->
```csharp
// Nothing published, so nothing is held back. An unlabeled call is Critical, never Sheddable:
// a default that sheds is a default that loses requests during the first incident after an
// upgrade.
var level = AmbientCriticality.Current;
```
<!-- endsnippet -->

`AmbientCriticality.TryParse` and `AmbientCriticality.Format` are the wire format, and `TryParse` is where the escalation clamp lives - use it for anything arriving from outside the process.

## Go deeper

- [Retry budget](retry-budget.md) - the bucket whose last half a `Sheddable` retry must leave behind.
- [Hedging](hedging.md) - the gates a hedge passes, of which this is the last.
- [Deadlines and attempt timeouts](deadlines.md#propagate-the-deadline-across-a-hop) - the other value that travels with a request, and the pass that reads both.
- [Admission control](../deep-dives/admission-control.md) - where a decision about shedding belongs.
