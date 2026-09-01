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

## What this does not fix

Hedging shortens a tail caused by variance - a slow node, an unlucky GC pause, a connection that picked the wrong path. It does nothing for a dependency that is uniformly slow, and it should not: a distribution with no tail has a p95 that nothing exceeds, so nothing is hedged. If every call takes 400 ms, hedging will correctly do nothing at all, and the number to look at is the dependency's.

It also cannot make a non-idempotent operation safe. For HTTP the handler refuses to hedge what it refuses to retry; for the callback API, configuring `Hedge` is your assertion that running the callback twice at once is acceptable, and the executor takes you at your word.
