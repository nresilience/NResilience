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

It fails on meaning. `VerdictKind` is the input to three separate decisions - the breaker's evidence rule, the choice of backoff curve, and the telemetry tag set - and a fifth member would need an answer in each. Two of those answers do not exist: there is no natural curve for a non-outcome, and no useful evidence in one.

It fails on blast radius. `VerdictKind` is public, and `Classifier.On<T>(Func<T, Verdict>)` exists so that users can switch on it. A fifth member makes every exhaustive switch expression in every consumer stop compiling, in exchange for a value that behaves exactly like `Throttled` almost everywhere.

So the kind stays `Throttled`, which is the honest reading anyway - something is defending a dependency from load - and the fact that *this process* is the thing doing the defending is one bit alongside it:

```csharp
public bool SelfImposed { get; }

public static Verdict Limited(TimeSpan? retryAfter = null);
```

Everything that already handles throttling therefore handles a refusal, unchanged. The long backoff curve applies. `RetryAfter` is honored verbatim. `Breaker.RecordCore` returns early for anything that is not `Transient`, so a refusal cannot open a circuit against a dependency that was never called.

### The polarity is not arbitrary

`SelfImposed` rather than the negation-free `ReachedDependency`, because `default(Verdict)` has to be safe. A default-constructed verdict reports `false`, which reads as "this reached the dependency" - the conservative answer, and the one that keeps it subject to the retry budget. Spelled the other way round, every default verdict would silently claim exemption.

## The retry budget is exempt, and nothing else is

This is the behavior the whole feature exists for.

The [retry budget](../features/retry-budget.md) bounds retries as a fraction of the traffic that reached the dependency. A retry of a call local admission control stopped costs the dependency nothing, because the call never left. Charging for it means a burst of self-throttling quietly drains the capacity that real transient failures need - and it drains it at exactly the moment a client is under load, which is when that capacity matters most.

So the spend site reads:

```csharp
if (budget is not null && !verdict.SelfImposed && !budget.TrySpend())
```

Deposits are untouched: `budget.Deposit()` fires only on `Ok`, so a refusal neither spends nor funds. It is not evidence in either direction.

The breaker needed no change, but one thing about it is worth recording, because the tempting simplification breaks it. `Breaker.Record` is still called for a self-imposed refusal. It records nothing - the early return handles that - but it also decrements the in-flight probe count, and skipping the call to save the work would leak the probe slot the refused call occupied and wedge the breaker half-open forever. A probe consumed by a refused call is wasted; a probe slot never returned is a breaker that never recovers.

## One bit, zero bytes

A verdict is live across the attempt `await`, so every byte of it is paid for in the state-machine box of every suspending call in the library - including calls in applications that never rate limit anything. A feature most users will not enable must not be charged to the users who do not enable it.

The flag was expected to pack into the padding that a single-byte `VerdictKind` leaves in front of a nullable `TimeSpan`. It does not: measured, the obvious `bool` field took the struct from 24 bytes to 32, because the runtime's automatic layout does not fill that padding.

So the flag shares the kind's byte instead. Four of its 256 values are enum members, and the top bit carries the origin:

```csharp
private const byte SelfImposedFlag = 0x80;

public VerdictKind Kind => (VerdictKind)(byte)(_packed & ~SelfImposedFlag);
public bool SelfImposed => (_packed & SelfImposedFlag) != 0;
```

Back to 24 bytes, gated by `The_verdict_carries_its_origin_for_free`. `AttemptRecord` already used the same trick to carry the verdict kind in the top eight bits of a 64-bit field, and it has the same spare capacity - so the flag survives into the attempt log for nothing as well, and a reader of `CallResult<T>.Attempts` can tell a limiter this process runs from a 429 the dependency sent.

The same accounting is why there is no `Admit` hook on the policy. A hook returning a new awaitable type adds a hoisted awaiter field to the state-machine box of every suspending call, configured or not - the same 16 bytes per call that `BeforeAttempt` returns `Task` specifically to avoid.

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

A refusal becomes a retry on the throttled curve, honoring the limiter's hint, capped by `Backoff.Max` and by the deadline, and visible in telemetry as a retry.

Queue time is none of those things. It is charged against `AttemptTimeout`, where it is indistinguishable from a slow dependency, and a `SlowCallThreshold` breaker will count it against a service that is answering perfectly well. The library has one mechanism for waiting between attempts and it is already tuned; a second one hidden inside the limiter would compete with it.

Queueing is available for the cases that want it. It is not what you get by not thinking about it.
