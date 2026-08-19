---
title: Guarded rejection
description: Why a refused call pauses before it reports, and why the pause is not configurable.
order: 6
---

# Guarded rejection

An open breaker and a depleted budget both refuse calls. The obvious implementation returns the
refusal immediately, and that is a mistake with a name.

A cheap rejection inside a caller's `while (true)` polling loop is a CPU spin. Without a forced pause,
a tripped breaker turns into errors returned at the speed of a method call, spiking client CPU and
generating more traffic than the call it refused would have. The guard designed to shed load becomes a
load generator. AWS carves out an explicit exception for exactly this on its long-polling operations.

So a refusal serves 100 milliseconds before it is reported. **Guarded rejection is not fail-fast**, and
the difference matters most in precisely the situation the guard exists for.

## The bounds on the pause

It is bounded by the time left on the [deadline](../features/deadlines.md), so a refusal can never make
a call overrun the budget its caller set. If 40 milliseconds remain, the pause is 40 milliseconds.

It observes the caller's cancellation token, so cancelling during a rejection pause aborts immediately.

It is announced before it is served: the `Rejected` [event](../reference/events.md) carries the pause in
`Delay`, so a listener reports what is about to happen rather than discovering it afterwards.

## Why it is not configurable

Because there is no value anyone needs it to be. It exists to put a floor under the rate of a rejection
loop, not to be tuned, and 100 milliseconds is short enough to be invisible to a call that was refused
and long enough to make a spin impossible. A knob here would be a knob whose only correct setting is
the default, and each of those has to be documented, defended and kept working forever.

Callers who schedule their own retries have something better than a delay to inspect:
`CallRejectedException.RetryAfter` carries the breaker's remaining break, or the time until the budget's
floor rate accrues a whole token.

