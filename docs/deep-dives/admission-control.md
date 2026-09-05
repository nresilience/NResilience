---
title: Admission control
description: Why a limiter refusal is not a new verdict kind, why the retry budget is exempt from it, and why the limiter lives in the callback.
order: 7
---

# Admission control

A limiter refusal is the only outcome in the library that the dependency had no part in. Every design decision behind [rate limiting](../features/rate-limiting.md) follows from taking that seriously.

## A refusal is not a kind of outcome

`VerdictKind` has four members, and the type's own summary says what they are: the four things an *outcome* can be. A limiter refusal is not an outcome. Nothing was attempted, so there is nothing to classify.

That is the argument against the obvious implementation, which is to add a fifth member and be done. It fails twice.

It fails on meaning. `VerdictKind` is the input to three separate decisions - the breaker's evidence rule, the choice of backoff curve, and the telemetry tag set - and a fifth member would need an answer in each. Two of those answers do not exist: no natural curve for a non-outcome, and no useful evidence in one.

It fails on blast radius. `VerdictKind` is public, and `Classifier.On<T>(Func<T, Verdict>)` exists so users can switch on it. A fifth member breaks every exhaustive switch expression in every consumer, in exchange for a value that behaves exactly like `Throttled` almost everywhere.

So the kind stays `Throttled` - the honest reading anyway: something is defending a dependency from load. That *this process* is the one defending is one bit alongside it:

```csharp
public bool SelfImposed { get; }

public static Verdict Limited(TimeSpan? retryAfter = null);
```

Everything that already handles throttling therefore handles a refusal, unchanged. The long backoff curve applies. `RetryAfter` is honored verbatim. `Breaker.RecordCore` returns early for anything that is not `Transient`, so a refusal cannot open a circuit against a dependency that was never called.

### The polarity is not arbitrary

`SelfImposed` rather than the negation-free `ReachedDependency`, because `default(Verdict)` must be safe. A default-constructed verdict reports `false`, which reads as "this reached the dependency" - the conservative answer, and the one that keeps it subject to the retry budget. Spelled the other way round, every default verdict would silently claim exemption.

## The retry budget is exempt, and nothing else is

This is the behavior the whole feature exists for.

The [retry budget](../features/retry-budget.md) bounds retries as a fraction of the traffic that reached the dependency. A retry of a call local admission control stopped costs the dependency nothing, because the call never left. Charging for it means a burst of self-throttling quietly drains the capacity that real transient failures need - and it drains it at exactly the moment a client is under load, which is when that capacity matters most.

So the spend site reads:

```csharp
if (budget is not null && !verdict.SelfImposed && !budget.TrySpend())
```

Deposits are untouched: `budget.Deposit()` fires only on `Ok`, so a refusal neither spends nor funds. It is not evidence in either direction.

The breaker needed no change, but one thing about it is worth recording, because the tempting simplification breaks it. `Breaker.Record` is still called for a self-imposed refusal. It records nothing - the early return handles that - but it also decrements the in-flight probe count. Skipping the call to save the work would leak the probe slot the refused call occupied and wedge the breaker half-open forever. A probe consumed by a refused call is wasted; a probe slot never returned is a breaker that never recovers.

## One bit, zero bytes

A verdict is live across the attempt `await`, so every byte of it is paid for in the state-machine box of every suspending call in the library - including calls in applications that never rate limit anything. A feature most users will not enable must not be charged to the users who do not enable it.

The flag was expected to pack into the padding that a single-byte `VerdictKind` leaves in front of the `RetryAfter` field. It does not: measured, the obvious `bool` field took the struct from 16 bytes to 24, because the runtime's automatic layout does not fill that padding.

So the flag shares the kind's byte instead. Four of its 256 values are enum members, and the top bit carries the origin:

```csharp
private const byte SelfImposedFlag = 0x80;

public VerdictKind Kind => (VerdictKind)(byte)(_packed & ~SelfImposedFlag);
public bool SelfImposed => (_packed & SelfImposedFlag) != 0;
```

Back to 16 bytes, gated by `The_verdict_carries_its_origin_and_its_pushback_for_free`. That gate asserts the same premise about `RetryAfter`, stored as a `long` of ticks biased by one so that `0` means "the server said nothing". A `TimeSpan?` field there cost another eight bytes for a value that is null on all but throttled verdicts, and the public property still hands back a `TimeSpan?`. `AttemptRecord` already used the same trick to carry the verdict kind in the top eight bits of a 64-bit field, and it has the same spare capacity - so the flag survives into the attempt log for nothing as well, and a reader of `CallResult<T>.Attempts` can tell a limiter this process runs from a 429 the dependency sent.

The same accounting is why [the `Admit` hook](#the-admit-hook) does not live in the shared loop. A
hook returning a new awaitable type would add a hoisted awaiter field to the state-machine box of
every suspending call, configured or not - the same 16 bytes per call that `BeforeAttempt` returns
`Task` to avoid. `Admit` avoids this by living in a second, separate `async` method, selected only
when it is configured, so the field is charged to the callers who select it and to no one else.

## Building a custom guard

Nothing about the mechanism is specific to the shipped rate limiter. Any local admission decision -
a distributed lock, a hand-rolled token bucket, a load shedder driven by your own telemetry - gets
the same treatment: the long backoff curve, the limiter's own hint honored verbatim, no evidence
against the breaker, no charge against the retry budget. None of it requires a new type in the
library.

The recipe is the one [classification](../features/classification.md) already documents, aimed at
`Verdict.Refused` instead of `Verdict.Throttled`:

```csharp
public sealed class ConsensusRefusedException(TimeSpan? retryAfter = null) : Exception
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

var policy = Resilience.Default with
{
    Classifier = Classifier.Default.On<ConsensusRefusedException>(ex => Verdict.Refused(ex.RetryAfter)),
};

var result = await policy.RunAsync(async ct =>
{
    if (!await consensusStore.TryAcquireAsync(ct))
        throw new ConsensusRefusedException(retryAfter: TimeSpan.FromMilliseconds(200));

    return await CallDependencyAsync(ct);
}, cancellationToken);
```

This works because `Verdict.Refused` (an alias of `Verdict.Limited`, named for a guard that is not a
rate limiter) is not specific to rate limiting - its summary says it is for "a rate limiter, a
concurrency limit, or anything else in this process that said no before the call left it" - and
because the budget and the breaker key off `SelfImposed`, not the exception type.
`RateLimitedException` gets special-cased in the executor only because the shipped limiters throw it
directly; a classifier rule reaches the identical code path through the ordinary
exception-classification `catch`, with the identical result.

The three properties [the callback is the seam](#the-callback-is-the-seam) requires of any guard -
per attempt, bounded by the attempt's own token, and running where a thrown exception is classified -
apply here exactly as they do to a rate limiter. `BeforeAttempt` does not have this property: it runs
outside the classified region, so an exception thrown there is never turned into a verdict at all.
That is why a guard belongs in the callback, not as a matter of taste.

## The Admit hook

`Resilience.Admit` is `Func<NextAttempt, Task<Verdict>>?`, checked once per attempt, in the same
classified region the attempt itself runs in. Return `Verdict.Ok` to admit the attempt; return
anything else - typically `Verdict.Refused` or `Verdict.Limited` - to refuse it. The attempt is
skipped and processed exactly as if the callback had produced that verdict: the same log entry, the
same telemetry, the same retry-budget exemption for `SelfImposed`, and the same breaker treatment.

```csharp
var policy = Resilience.Default with
{
    Admit = async next =>
        await consensusStore.TryAcquireAsync(next.CancellationToken)
            ? Verdict.Ok
            : Verdict.Refused(TimeSpan.FromMilliseconds(200)),
};

var result = await policy.RunAsync(ct => CallDependencyAsync(ct), cancellationToken);
```

This is the same guard as [Building a custom guard](#building-a-custom-guard), expressed as a value
instead of a thrown exception. Prefer `Admit` when the guard's outcome is naturally a decision rather
than a failure - there is no exception to throw, so there is nothing to invent one for. Prefer the
classified-exception recipe when the guard already fails by throwing, because a domain type you
already have composes with the classifier for free. Both reach the identical code path: `Admit`
returning a non-`Ok` verdict is processed exactly where a classified `RateLimitedException` is.

An exception `Admit` throws is not special-cased. It falls into the same exception handling the
attempt's own exceptions do, and is classified like any other.

**A guard that answers from memory should not allocate to say so.** The hook returns `Task<Verdict>`
rather than `ValueTask<Verdict>` for the reason below, which leaves a synchronous guard - a
semaphore, a load shedder, a cached lease - wrapping its answer in a task on every attempt. Hand back
`Verdict.OkTask` instead:

```csharp
var policy = Resilience.Default with
{
    Admit = _ => gate.Wait(0)
        ? Verdict.OkTask
        : Task.FromResult(Verdict.Limited(TimeSpan.FromMilliseconds(50))),
};
```

`Verdict.TransientTask` and `Verdict.PermanentTask` are the same thing for the other two constant
verdicts. There is no cached task for `Throttled`, `Limited` or `Refused`: each takes a pushback, so
there is no single value to cache - and a guard that refuses is on the slow path anyway, where the
retry it causes costs far more than the task wrapping its answer.

`Admit` is one slot, and setting it replaces whatever was there. That is deliberate: combining two
guards needs a rule for which refusal wins, and that is a decision about your system rather than one
the library should make for you. Two guards are one hook that checks both.

**The cost is opt-in, and only the callers who opt in pay it.** Configuring `Admit` selects a second,
separate execution path - a second `async` method with the loop's shell repeated and one extra
`await Admit(...)` added - rather than adding the await to the one shared loop. This is the only
technique that works for the reason [a refusal is not a kind of outcome](#a-refusal-is-not-a-kind-of-outcome):
a hoisted awaiter field is a property of the generated state-machine type, not of any particular
call, so an `await` written once in the executor's source would cost every caller that field whether
or not the hook is set. A policy that never configures `Admit` selects the original loop, and its
state-machine box is unchanged; `NResilience.Gates` gates this directly, in the same sweep as every
other budget in the library. Configuring `Admit` costs one hoisted `TaskAwaiter<Verdict>` field -
measured at roughly 30 B per suspending call on top of the same policy without it.

## The callback is the seam

Which leaves the question of where a limiter runs, and the answer is that it needs no new place. Inside the executed callback, every property it needs is already true:

- **Per attempt.** Retry re-invokes the callback, so a permit taken inside it is taken once per attempt. A guard a retry bypasses is not a guard.
- **Bounded.** The callback receives the attempt's token, which is `min(AttemptTimeout, remaining deadline)` linked with the caller's. A waiting acquire is bounded by the policy's own time budget with nothing to configure.
- **Classified.** The callback runs inside the executor's `try`, so a `RateLimitedException` reaches the exception handling. An acquire anywhere else does not - `BeforeAttempt` is awaited outside it, and an exception thrown there escapes the executor entirely.

For HTTP this falls out of the handler chain. `ResilienceHandler` executes one attempt by sending through the handler inner to it, so a limiting handler installed there is asked for a permit on every attempt, inside the `try`, with the attempt's token. With `IHttpClientFactory` the first handler registered is the outermost, which makes the correct order also the natural reading order:

```csharp
services.AddHttpClient("api")
        .AddResilience()
        .AddRateLimit(o => o.PermitsPerSecond = 100);
```

Registered the other way round the limiter is asked once per operation and every retry bypasses the quota. Nothing about the resulting behavior looks wrong until a dependency starts returning 429s under load, which is why the registration refuses it rather than accepting it.

## The exception belongs to the core

`RateLimitedException` is in `NResilience`, not beside any limiter. It needs no reference to `System.Threading.RateLimiting`, so the core package keeps its no-package-dependencies claim, and any limiter at all - the platform's, a distributed one, a hand-rolled semaphore - composes with the executor by throwing it.

The executor recognizes it directly rather than asking a `Classifier`, for the reason it recognizes its own `AttemptTimeoutException`: a user predicate that turned a refusal into `Transient` would feed the breaker evidence about a dependency that was never contacted, and open a circuit against a healthy service because this process throttled itself. That is not a decision a classifier should be able to get wrong.

One consequence is worth stating plainly: `Classifier.ClassifyException(new RateLimitedException())` returns `Permanent`, because the classifier genuinely does not recognize the type. Nothing reaches it by that path, and the same is already true of the attempt timeout and of caller cancellation.

## Queueing is off by default

A refusal becomes a retry on the throttled curve, honoring the limiter's hint, capped by `Backoff.MaximumDelay` and the deadline, and visible in telemetry as a retry.

Queue time is none of those. It counts against `AttemptTimeout`, where it is indistinguishable from a slow dependency, and a `SlowCallThreshold` breaker would count it against a service answering perfectly well. The library has one mechanism for waiting between attempts, already tuned; a second one hidden inside the limiter would compete with it.

Queueing is there for the cases that want it. It is not what you get by not thinking about it.
