---
title: FAQ
description: The questions that are decisions rather than problems.
order: 12
---

# FAQ

### Why is there no builder?

A policy is a record, so `with` is the configuration language. A builder buys a validation hook at
`Build()` and costs a mutable-to-immutable transition, an ordering that matters, and a type that cannot
be a `static readonly` field. The trade was made knowingly: validation happens when you call
`Validate()`, eagerly at DI registration, and lazily on the first execution of each policy instance.

### Why is `Resilience` not generic?

Because the result type is a property of the call, not of the policy. One policy covers
`HttpResponseMessage`, `int`, `Stream` and `void`. Result classification is resolved per result type and
cached, so the policy never needs to know.

### Why is `Attempts` the total rather than the retry count?

Because "3 retries" is ambiguous and "3 attempts" is not. `Attempts = 1` means no retry, and nobody has
to remember which end the off-by-one is on.

### Is there a synchronous API?

No. A retry loop that blocks holds a thread through every backoff delay, and a library that offers both
either duplicates its engine or deadlocks somebody. `ResilienceHandler.Send` throws
`NotSupportedException` for the same reason.

### Where is hedging?

Not implemented. Hedging - issuing a second request before the first has failed - is a real technique
and a genuinely dangerous default: it multiplies load on a dependency that is already slow, which is
exactly when it fires. It needs a budget, a latency threshold that adapts, and an idempotency story per
request, and shipping it without all three would be shipping a foot-gun. The
[retry budget](features/retry-budget.md) and the [breaker](features/circuit-breaker.md) are the
groundwork it would need.

### Where is a rate limiter?

`System.Threading.RateLimiting` is in the platform and is good. A resilience library wrapping it would
add a knob and an opinion, not a capability.

### Where is bulkhead isolation?

A bulkhead is a limit on how many calls to one dependency can run at once, so a slow or failing
dependency cannot consume all your threads and starve calls to everything else. `SemaphoreSlim`,
or the platform's concurrency limiter, is all you need: everything a bulkhead does is available
without a policy being involved, and the interesting part - what to do when the bulkhead is full -
is a decision at the call site.

### Can I add my own policy layer?

Not by composition, because there is nothing to compose into: the engine is
[one flat method](deep-dives/one-executor.md). The extension points are the
[classifier](features/classification.md), `Backoff.Custom`, `BeforeAttempt` and `OnEvent`. That is
a smaller opening than a pipeline, and the argument for it is API stability: the surface
stays small because there is less to get wrong later.

### Is a `Breaker` thread-safe? Can I share one across policies?

Yes and yes - sharing one is how you say "these calls are the same dependency". It is guarded by an
uncontended lock. `with` copies the reference, never the state.

### Does the breaker see attempts or whole operations?

Attempts, always. That question has one answer here rather than depending on composition order.

### Why does refusing a call take 100 milliseconds?

Because a free rejection inside a polling loop is a CPU spin, and the guard that was meant to shed load
becomes a load generator. See [guarded rejection](deep-dives/guarded-rejection.md).

### Why is telemetry off for a hand-built policy but on for a registered one?

`OnEvent = null` costs nothing, and that is what makes free-when-unused meaningful. A registered
policy is part of an application that runs in production, so the registration attaches the listener.
`telemetry: false` and `ResilienceOptions.Telemetry = false` are the switches.

### Is it AOT and trimming safe?

Yes. AOT (ahead-of-time compilation, which compiles to native code at publish time rather than
generating it at runtime) and trimming (removing unused code at publish time to shrink the binary)
are both CI-enforced: `dotnet publish -p:PublishAot=true` with warnings as errors, plus a
published binary that executes a policy - including DI, configuration binding and the meter - and
re-checks the allocation budgets. There is no reflection anywhere in the core.

### Which frameworks are supported?

`net8.0` and `net10.0`. Both are tested, both are gated, and "no allocation cliff on net8" is a claim
with a test behind it.

### Does it work with `IHttpClientFactory`?

Yes - `.AddResilience()` on the client builder. Note the two-minute handler rotation for configuration
reload, described under [dependency injection](di/index.md).

