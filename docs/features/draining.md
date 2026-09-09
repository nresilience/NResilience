---
title: Drain-aware shutdown
description: Stop retrying when the pod is going away, so a rollout is not held open by attempts nobody will read the answer to.
order: 14
---

# Drain-aware shutdown

A pod that receives `SIGTERM` gets a grace period, typically 30 seconds. A call that is mid-retry when the signal arrives keeps retrying into it: holding a connection, delaying the rollout, and sending an attempt to a dependency whose load balancer has already stopped routing to this instance. That retry cannot succeed in any way that matters, because nobody is going to read the response.

**Drain-aware shutdown** stops the next attempt. It is **on by default** for every policy registered in a container: `AddResilience` subscribes to `IHostApplicationLifetime.ApplicationStopping`, and from that moment no call starts another attempt.

Two things happen, and nothing else:

- **No new attempt starts.** The call stops with the failure it has. Hedges stop for the same reason.
- **Deadlines are clamped to what is left of the grace period.** A call with five minutes of deadline and 200 ms of grace ends in 200 ms.

**In-flight attempts are not touched.** Cancelling work that might still complete is the host's job, and the host already has the token for it.

## What a drained call looks like

The terminal event is `Draining` and the reason is `StopReason.Draining`:

| | |
| :--- | :--- |
| Event | `CallEventKind.Draining` - terminal, exactly one per call |
| `CallResult.Reason` | `StopReason.Draining` |
| Exception | **the dependency's own**, unchanged |
| Log event | `1032`, at `Debug` on the `Normal` [profile](logging.md) |

The exception is the difference worth knowing. A [retry budget](retry-budget.md) refusal or an open [breaker](circuit-breaker.md) produces a `CallRejectedException`, because a guard turned the call away. Draining is not a guard: no one refused this call, the process simply stopped asking for another attempt. So `catch (HttpRequestException)` keeps working through a rollout, and the attempt history is on `Exception.Data` as always.

## Tune it, or turn it off

<!-- snippet: draining-register -->
```csharp
// Nothing to turn on. Every registered policy stops retrying when the host starts shutting
// down, and this is where that is tuned - a shorter grace period than the host's, to leave
// room for whatever runs after the calls stop.
services.AddResilience(name: "api", policy: Resilience.Http);
services.AddResilienceDraining(o => o.Grace = TimeSpan.FromSeconds(value: 20));

// Or turned off, for a process whose outbound calls must run to their own bounds.
services.AddResilienceDraining(o => o.DrainOnShutdown = false);
```
<!-- endsnippet -->

`Grace` is 30 seconds, which is what `HostOptions.ShutdownTimeout` defaults to. **Set it to match if you changed the host's timeout**: the two numbers are not read from one place, because `HostOptions` lives in `Microsoft.Extensions.Hosting`, which `NResilience.Extensions` deliberately does not reference so that a worker or a console app can take the DI package without the whole host. `Timeout.InfiniteTimeSpan` drains without clamping anything.

Turn the feature off for a process whose outbound calls must finish during shutdown - a drain job flushing a queue. Everything else wants the default.

## Latch it without a host

The flag is in the core package and the subscription is not, because core has no dependency on `Microsoft.Extensions.Hosting` and will not acquire one. Anything that knows the process is going away can say so:

<!-- snippet: draining-manual -->
```csharp
// Core has no dependency on Microsoft.Extensions.Hosting and will not acquire one, so a
// console app or a worker without it says so directly. The grace period is optional: without
// one, draining stops retries and leaves deadlines alone.
AppDomain.CurrentDomain.ProcessExit += (_, _) => Draining.Begin(grace: TimeSpan.FromSeconds(value: 30));
```
<!-- endsnippet -->

`Draining.Begin` latches, and **the first call wins**: a second signal cannot hand a shutting-down process a longer grace period than the first one did. There is no way back, because a shutting-down process does not change its mind.

`Draining.Remaining` reports what is left of the grace period, and `Draining.IsDraining` reports the latch:

<!-- snippet: draining-readiness -->
```csharp
// The latch is readable, so a readiness probe can stop advertising this instance before the
// load balancer works it out on its own.
var ready = !Draining.IsDraining;
```
<!-- endsnippet -->

## What it costs

One volatile read of one static field on the retry path, and one more when a call resolves its deadline. Nothing is allocated, nothing is measured, and a process that never drains never branches differently.

## Go deeper

- [Retry](retry.md) - the decision draining short-circuits.
- [Deadlines and attempt timeouts](deadlines.md) - the bound the grace period clamps.
- [Hedging](hedging.md) - the second copy that is not sent while draining.
- [Events](../reference/events.md) - the terminal event and its log record.
