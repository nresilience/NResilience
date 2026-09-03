---
title: Hedging internals
description: Why an adaptive threshold is safe where a constant one is not, how the quantile is estimated, and what a third execution loop costs.
order: 9
---

# Hedging internals

Hedging is easy to refuse, and the reason to refuse it is correct. The objection deserves stating at full strength before the answer, because the answer is narrow and the API's shape is what carries it.

## The objection, and the half of it that survives

Hedging multiplies load on a dependency exactly when the dependency is slow. That is not a risk to be managed; against a **fixed** threshold it is a certainty. If the threshold is 50 ms and a brownout makes every call take 200 ms, then every call hedges and the client doubles traffic to a service already failing. A tail-latency tool has become a load generator - the failure mode [guarded rejection](guarded-rejection.md) prevents elsewhere in this library.

The premise is true. The conclusion it supports is that a **constant threshold** must not ship; it does not follow that hedging must not.

Hedge against a live quantile of recent latency instead and the failure mode is gone by construction. If the threshold is the observed p95, about 5% of calls sit above it - and that stays about 5% during a brownout, because the brownout carries the p95 up with it. The load multiplier is bounded at roughly `1 + (1 - q)` by the definition of a quantile, not by an operator's guess about conditions nobody has seen yet.

So the API is `Hedge.At(0.95)`, and there is no `Hedge.After(TimeSpan)`. The omission is not an oversight to be filled in later; it is the feature. `Quantile` is spelled as the load an operator is willing to add, because that is the number they want to bound.

There is a test for exactly this claim. `A_brownout_stops_the_hedging_it_would_otherwise_cause` hedges a 500 ms call while 500 ms is the tail of the distribution, then makes 500 ms *be* the distribution, and asserts that the identical call is no longer hedged. Nothing was reconfigured in between.

## Estimating the quantile

The estimate lives in `LatencyWindow`, an internal type with no public surface. Two decisions shape it, and both are about what it is allowed to cost.

**Storage is an HdrHistogram-lite.** Durations land in a log-linear bucket array: a linear region below 8 µs, then eight buckets per octave up to about 134 seconds. 208 buckets, four of them in rotation, about 3.3 KB per scope - allocated only when a policy actually hedges, and only once per policy instance. Recording a sample is one clock read, one division, one bucket index, and one interlocked increment.

**The rings carry their own epoch stamp** rather than being cleared on a timer - the [circuit breaker's](breaker-internals.md) clear-on-write idea one level up. Idle behavior falls out for free: after a quiet period every ring's stamp is stale, the sum is zero, and the window reports nothing rather than leftovers from a previous revolution. No timer ever runs on behalf of an idle policy.

The answer is memoized per slice - a quarter of the window, so 7.5 s at the default - which makes the read path in the hot loop a volatile read and a comparison rather than a scan of four rings. That staleness is deliberate: the question is about a distribution, not the last call, and rescanning a few thousand counters per attempt to answer it a few seconds sooner would charge every call for it. The one exception is a window that has not yet reached `MinimumSamples`; a stale "not enough yet" would keep a cold process from ever starting, so that case always recomputes.

`Threshold` reports the containing bucket's **upper** bound rather than interpolating. The answer is an overestimate of at most 12.5% and never an underestimate, and the direction is the point: a threshold 12% high hedges slightly less often than asked, and one 12% low hedges more. Only one of those errs toward the dependency.

## What is not evidence

The loser of a race was cancelled by this library. It is not a failure, and reporting it as one would corrupt every downstream signal at once: the breaker would trip on our own cancellations, the budget would be charged for a call whose outcome was thrown away, and `nresilience.attempts ÷ nresilience.calls` would count work nobody waited for.

So a discarded leg is not classified, records no outcome against the breaker, returns the probe slot it took, and makes no deposit to the budget. It **is** written to the attempt log, flagged, because a hedge you cannot see is a hedge you cannot tune. Its log entry is written at the moment of cancellation rather than when the leg finally returns: waiting would hand the caller's success back only once every loser had stopped, and a callback that ignores its cancellation token could hold up the very call hedging exists to make faster.

Its value is disposed. That is the one runtime type test in the executor:

```csharp
if (loser is IAsyncDisposable a)
    await a.DisposeAsync();
else if (loser is IDisposable d)
    d.Dispose();
```

`HttpResponseMessage` is `IDisposable`, so a hedged HTTP call leaks no sockets without a line of HTTP-specific code in the core. The alternative - a `Func<T, ValueTask>` disposal hook - cannot live on a non-generic `Resilience`, and making the policy generic for one feature would trade away the library's central design claim. The same rule covers values a later round supersedes: hedging asked for answers nobody requested, so hedging disposes the ones it throws away.

## The third loop

The executor is [one fused `async` frame](one-executor.md), written twice already: `ExecuteAsync` and `ExecuteWithAdmitAsync` differ in a single `await`, because a hoisted awaiter field is a property of the generated state-machine **type** and an `await Admit(...)` written once would charge every caller for it. Hedging adds a third optional shape. Following the rule mechanically gives four methods, then eight.

Two decisions cap it at three.

**The post-attempt decision is shared, and it is not `async`.** Everything between "the attempt returned" and "await the backoff" is pure synchronous logic - record, notify, sample the breaker, then the six questions in the order they have to be asked. It lives in `RecordAttempt` and `Decide`, two non-`async` helpers all three loops call. Nothing reaches the state-machine box, because nothing there awaits: the parameters travel in registers and on the stack. This keeps the three loops synchronized, so there is no drift to manage between them.

**The hedged loop is allowed to allocate.** It runs a task per in-flight leg, races them with `Task.WhenAny`, and holds a list. No version of hedging avoids that, and pretending otherwise would be a worse design, not a cheaper one. What matters is quarantine: the loop is selected only when `Hedge` is set, so the [allocation budgets](allocations.md) for every other caller do not move by a byte. That is a gate, not an intention.

One thing came out cheaper than expected. Because each leg runs in its own `async` local function, the `Admit` hook is awaited inside the leg rather than in the shared loop - so its hoisted field is charged per leg of a hedged call instead of to every caller of a merged loop.

## Rounds

The loop runs in rounds. A round starts a leg, waits `min(threshold, remaining deadline)`, and starts another if the gates pass and `Attempts` is not spent. The first `Ok` wins and the rest are cancelled. If every leg in a round fails, the round's last verdict drives the backoff and the next round begins - the same `Decide` makes the call.

`Attempts` counts wire calls, not rounds, and that is why it stays the one number. Polly gives hedging its own `MaxHedgedAttempts` alongside retry's `MaxRetryAttempts`, and the product is the real ceiling on load. Here `Hedge.MaxConcurrent` bounds concurrency and `Attempts` bounds the total, so the number an operator reasons about is the number they already know.

The budget is charged when a hedge actually starts, not when its timer is armed - so a call that came back on its own is never charged for a hedge it did not need. Hedges and retries draw on one bucket because both are amplification, and the aggregate is what the budget exists to bound. The arithmetic is comfortable rather than tight: hedging at the p95 spends about 5% of traffic, leaving about 5% of the default budget for retries, and a policy already retrying hard stops hedging on its own.

## Closed is not healthy

Gate 3 of the hedged loop is "the breaker is closed", and for a long time that was the whole health check. It is a weak one. A breaker's default trip is five *consecutive* failures, and a dependency erroring on 40% of its calls almost never produces five in a row - so it sits closed indefinitely while this process hedges every slow call against it and adds 5% load to a service that fails 40% of its calls. Envoy suppresses hedging under elevated errors for exactly this reason.

The gap needed a number, and the library already had one. The breaker measures its own error rate to decide when to trip, and `Failures` turns that into a trip point relative to the dependency's own baseline. `SuppressAt` is a fraction of *that* point, not a rate of its own - so the second gate inherits the first's guardrails whole: the measured baseline, the absolute floor that keeps a near-zero baseline from suppressing on one unlucky call, and `FailureRatio` as the ceiling when it is set. Operators don't need to estimate or guess new values.

Two rules keep it from firing on noise. The trip window has to hold `MinimumCalls` outcomes, which is the same evidence the breaker demands before it will judge a rate at all; and at least two of them have to be failures, because one failure is an event rather than a rate - the same rule the relative trip applies, for the same reason.

The second rule reaches slightly further here than it does in the breaker, and deliberately. The breaker exempts its absolute `FailureRatio` from the two-failure floor, because a caller who wrote `FailureRatio = 0.05` against `MinimumCalls = 20` asked for exactly that reading and the library does not get to second-guess a number they named. Suppression is not a number they named - it is a fraction the library derives - so it holds to the floor whatever the trip point was derived from. The only effect is that a single failure never suppresses hedging; the breaker still opens on it if that is what the caller configured.

The failure mode is real and stated rather than engineered around. A dependency with one bad shard both fails often and is precisely the case hedging routes around, and this gate turns hedging off for it. That is the deliberate trade: the cost of hedging a dependency that cannot use it is borne by the dependency, and the cost of not hedging one that could is borne by this process's tail. `SuppressAt = 1` puts the decision back where it was, suppressing only at the rate that opens the breaker anyway.

## When the extra load buys nothing

`SuppressAt` asks whether the dependency is well enough to be sent extra load. It does not ask whether the extra load is doing anything, and those are different questions with different answers.

Hedging works on an assumption no configuration can state: that latency is independent enough between two attempts that the second one sometimes wins. Against a dependency with one slow shard it is true, the second draw lands somewhere else, and hedging is the best feature in the library. Against a dependency that is uniformly slow because it is saturated it is false - the second leg is as slow as the first, every hedge loses its race, and the 5% is pure added load on a service that is already short of capacity. The two look identical from the outside. What tells them apart is the outcome, and the library has been counting it all along: `nresilience.hedges{outcome=won}` over `{outcome=started}` is the win rate, and until now nothing acted on it.

`WinRate` closes that loop. Hedges won over hedges started, over a window; a window below `Floor` halves the fraction of would-be hedges that start, a window above it adds a quarter back. AIMD, and the asymmetry is the one [ramped recovery](breaker-internals.md#the-recovery-cliff) and the adaptive limiter both use, for the same reason: hedging too much costs the dependency and hedging too little costs this process's tail, and only one of those two is somebody else's capacity.

**Why the loop moves the load and not the threshold.** The obvious retreat is to raise the effective quantile, and it is wrong twice. A `LatencyWindow` answers one quantile, fixed at construction so the answer can be memoized per slice, so a moving quantile costs a second window per policy. Worse, the map from a quantile to a hedge *rate* is a property of the dependency's distribution rather than of the configuration - raising 0.95 to 0.98 might halve the hedges or barely touch them. Admitting a fraction of the hedges that fire keeps the arithmetic exact instead: the load is `(1 - Quantile) x allowance`, whatever the distribution is doing, which is the same guarantee `Quantile` itself carries. The admission is deficit-accounted, not sampled - `allowance` accumulates as credit and a hedge starts when it reaches 1 - so the admitted hedges come out evenly spaced and a simulation of the loop runs the same way twice.

**Why the return is on the clock and not on the evidence.** A retreat starves the loop of exactly the evidence it would need to come back: fewer hedges start, so fewer wins are observable, so a purely evidence-driven return would ratchet the allowance to `MinimumAllowance` and hold it there for the life of the process - and the dependency that recovers would never be hedged again. So a window holding fewer than `MinimumSamples` hedges *relaxes* the allowance rather than holding it, which doubles as the cold-start rule. Against a dependency hedging cannot help, the loop therefore settles into a shallow cycle rather than a floor, and the amplitude of that cycle is the feature's steady-state cost. At real traffic it is small: a policy seeing a hundred hedges a slice reaches `MinimumAllowance` in five decisions and stays there, because a twentieth of a hundred is still evidence.

The failure mode is stated rather than engineered around, exactly as it is for `SuppressAt`. A dependency whose tail is caused by something no second attempt can route around - a saturated fleet, a slow downstream every replica shares - is precisely what this loop retreats from, and the tail is real while it does. Hedging is not what fixes that, which is the argument; but a climbing `suppressed` count means "hedging has stopped helping", not "the dependency is healthy again", and it is the dependency's own numbers to look at next. The loop is opt-in for this reason, and because it is a control loop over a control loop on the one path in the library that already allocates.

## What this does not fix

Hedging shortens a tail caused by variance - a slow node, an unlucky GC pause, a connection that picked the wrong path. It does far less for a dependency that is uniformly slow, and the quantile is most of why: a distribution with no tail has a p95 that almost nothing exceeds, so almost nothing is hedged. If every call takes 400 ms, hedging costs about `1 - Quantile` of the traffic and buys nothing, `WinRate` is what notices, and the number to look at is the dependency's.

It also cannot make a non-idempotent operation safe. For HTTP the handler refuses to hedge what it refuses to retry; for the callback API, configuring `Hedge` is your assertion that running the callback twice at once is acceptable, and the executor takes you at your word.
