---
title: Deadlines
description: How the interceptor writes a per-attempt deadline onto the wire, and which timer owns what.
order: 2
---

# Deadlines

gRPC has propagated deadlines built in, and the interceptor uses them. Each attempt's ceiling - `min(AttemptTimeout, time left on the Deadline)` - is written into `CallOptions.Deadline`, and grpc-dotnet converts that into the standard `grpc-timeout` header. The server learns how long it has, with no new header, no new format, and nothing to parse.

That is [deadline propagation](../features/deadlines.md#propagate-the-deadline-across-a-hop) for gRPC, and it costs nothing:

<!-- snippet: grpc-deadlines -->
```csharp
var options = new GrpcResilienceOptions
{
    // Each attempt's ceiling is written into CallOptions.Deadline, which grpc-dotnet sends
    // as the standard grpc-timeout header. On by default.
    PropagateAttemptDeadline = true,

    // How much longer than the attempt ceiling the wire deadline is set. Not zero: it is
    // what keeps NResilience's own timer ahead of grpc-dotnet's, so a timed-out attempt
    // still produces AttemptTimeoutException.
    DeadlineSlack = TimeSpan.FromMilliseconds(50),

    // HttpClient.Timeout stops competing with the deadline. On by default.
    OwnTransportTimeout = true,
};
```
<!-- endsnippet -->

With the shipped preset, a gRPC call gets a 10-second per-attempt ceiling that the server can see, inside a 30-second overall deadline.

## Why the slack is not zero

The HTTP integration's deadline header is advisory: the peer reads it or ignores it, and it is never a bound on this side. `CallOptions.Deadline` is not that. The local gRPC client enforces it with a timer of its own.

So writing the bare attempt ceiling into it arms **two timers for the same instant** - NResilience's and grpc-dotnet's - and whichever the runtime notices first decides what the call looks like. When grpc-dotnet's wins, you get an `RpcException(DeadlineExceeded)` instead of an `AttemptTimeoutException`, the deadline accounting is off by one attempt, and no `OrphanedWork` event is raised.

`DeadlineSlack` resolves that: the wire deadline is the ceiling **plus** the slack, so NResilience's timer fires first in the ordinary case and the wire deadline is the backstop it is meant to be. The peer still learns a number that is honest to the millisecond it matters at.

The case slack cannot cover - clock granularity, a scheduling stall - is handled inside the interceptor rather than left to chance. A `DeadlineExceeded` on a deadline the interceptor wrote is translated into the timeout shape the executor already knows how to judge, so the outcome is the same either way.

The rule, stated once: **the wire deadline is a hint to the peer; the local attempt token is authoritative.**

## What produces what

| What fired | What the caller gets |
| :--- | :--- |
| The attempt ceiling, noticed by NResilience's timer | `AttemptTimeoutException`, classified transient by the executor |
| The same ceiling, noticed first by grpc-dotnet's timer | The same `AttemptTimeoutException` - which is the point |
| Your own `CancellationToken` | `OperationCanceledException`, unchanged and never retried |
| A deadline you set on the call, or one the peer imposed | `RpcException(DeadlineExceeded)`, classified transient by the [gRPC classifier](classification.md) |

## A deadline you set yourself

A `CallOptions.Deadline` you set on a call is never overwritten. The effective deadline is whichever of the two is tighter, and when it is yours the interceptor stops treating a `DeadlineExceeded` as its own - it reaches the classifier as the transient status it is.

## Who bounds what

Three things want to bound a gRPC call, and only one of them should:

| Bound | Covers | Verdict |
| :--- | :--- | :--- |
| `Resilience.Deadline` | The whole call, retries and backoff included | Keep it. This is the honest bound, and it is visible. |
| `Resilience.AttemptTimeout` | One attempt | Keep it. This is what reaches the wire as `grpc-timeout`. |
| `HttpClient.Timeout` on the channel | The whole call, invisibly | Removed, unless you turn `OwnTransportTimeout` off. |

The transport timeout covers the entire retry sequence rather than one attempt, so it silently caps any policy with a longer deadline. `AddGrpcResilience()` sets it to `Timeout.InfiniteTimeSpan` for you. This is usually a no-op - gRPC's own client factory already does it - and the option exists for a caller who supplies a handler of their own.

An interceptor cannot reach the channel in front of it, so setting `OwnTransportTimeout` on an interceptor you construct yourself does nothing at all.

## Inherit a caller's deadline

Set `UseAmbientDeadline = true` on the policy and the effective deadline becomes the tighter of the configured one and whatever the inbound request published. In an ASP.NET Core service, [`UseResilienceDeadline()`](../features/deadlines.md#propagate-the-deadline-across-a-hop) publishes it.

The interceptor reads that clamp before it computes the wire deadline, so the number the next hop is told already reflects the time your own caller has left.
