---
title: FAQ
description: Technical reasoning behind design decisions in NResilience.
order: 13
---

# FAQ

## Design decisions

### Why is there no builder?
A policy is a record, so `with` expressions serve as the configuration language. A builder would provide a validation hook at `Build()` but would require a mutable-to-immutable transition, introduce an ordering dependency, and prevent the use of `static readonly` fields. 

Validation occurs when you call `Validate()`, eagerly during dependency injection registration, or lazily on the first execution of each policy instance.

### Why is `Resilience` not generic?
The result type is a property of the call, not the policy. A single policy can handle `HttpResponseMessage`, `int`, `Stream`, or `void`. Result classification is resolved per result type and cached, so the policy does not need to be generic.

### Why is `Attempts` the total count rather than the retry count?
Using total attempts removes ambiguity. `Attempts = 1` means no retry occurs, eliminating off-by-one errors common in retry count configurations.

### Is there a synchronous API?
No. A retry loop that blocks holds a thread through every backoff delay. Offering both synchronous and asynchronous APIs would either duplicate the engine or risk deadlocks. For this reason, `ResilienceHandler.Send` and `ResilienceInterceptor.BlockingUnaryCall` both throw a `NotSupportedException` rather than passing the call through unprotected.

### Where is gRPC?
In the separate `NResilience.Grpc` package: `AddGrpcResilience()` on the builder that `AddGrpcClient<T>()` returns. See [gRPC](grpc/index.md).

It is a separate package and a separate registration, not an overload of `AddResilience()`, because a gRPC call is the wrong shape for the HTTP handler. Every gRPC call is an HTTP `POST`, which the handler refuses to retry by default, and a gRPC failure travels in the `grpc-status` trailer on an HTTP `200`, which the HTTP classifier reads as a success. On a gRPC client, `AddResilience()` is an inert handler that adds overhead and retries nothing.

Unary calls are covered. Server-streaming gRPC is not wrapped yet; client-streaming and duplex calls are passed through untouched and always will be, for the same reason a partially consumed stream cannot be retried.

### Can I retry a stream?
Yes, through the `RunAsync` overloads that take an `IAsyncEnumerable<T>` source. The retry stops at the first element, because once the caller has received one, a retry would duplicate or drop work they have already acted on. Everything after the first element is handed to the caller untouched. See [Streaming](features/streaming.md).

### Where is hedging?
It is here, and it is opt-in: set `Hedge = Hedge.At(0.95)`. See [Hedging](features/hedging.md).

Issuing a second request before the first fails multiplies load on a dependency exactly when it is slow. That objection is correct - against a **fixed** delay, which is why there is no `Hedge.After(TimeSpan)`. Hedging against a live quantile of recent latency removes the failure mode by construction: a brownout carries the quantile up with it, so the fraction of calls that hedge stays at about `1 - Quantile`.

The three gates a hedge passes are a budget, an adaptive latency threshold, and a per-request idempotency strategy. See [Hedging internals](deep-dives/hedging-internals.md) for the argument.

### Where is a rate limiter?
`NResilience.Extensions` provides one, and it does not reimplement `System.Threading.RateLimiting` - it gives the platform's limiters a correct place to stand. See [Rate limiting](features/rate-limiting.md).

What the library adds is the composition the platform cannot decide for you: the permit is taken once per attempt rather than once per operation, the wait is bounded by the time left on the deadline, and a refusal is classified as self-imposed throttling - so it takes the long backoff curve, never counts as evidence against the dependency, and is never charged to the [retry budget](features/retry-budget.md). For the reasoning, see [Admission control](deep-dives/admission-control.md).

### Where is bulkhead isolation?
`Limit.Concurrency` is the bulkhead: it bounds how many calls run against one dependency at once, per host by default. See [Resource isolation with bulkheads](guides/resource-isolation.md) for a complete guide.

```csharp
using var limiter = Limit.Concurrency(10);  // at most 10 concurrent calls

var result = await policy.RunAsync(async ct =>
{
    using var lease = await limiter.AcquireAsync(ct);
    return await dependency.CallAsync(ct);
}, cancellationToken);
```

This approach is correct for several reasons:

1. **Zero allocation when unused** - no limiter object means no overhead
2. **Per-attempt permits** - each retry acquires its own permit, so retries don't reuse the same slot
3. **Deadline-aware** - the acquire respects the remaining time on the deadline; no separate timeout to configure
4. **Correct verdict** - refusals are classified as `Verdict.Throttled(SelfImposed: true)`:
   - Retried on the long backoff curve (1 second base, not 100 ms) to defend the dependency
   - Never opens the circuit breaker (this is your own throttling, not evidence the dependency is broken)
   - Never charged to the retry budget (the call never left this process; no amplification)

**For HTTP via dependency injection**, the handler scopes limiters per host automatically:

```csharp
services.AddHttpClient("api")
    .AddResilience()
    .AddRateLimit(options => options.Concurrency = 10);  // Per-host
```

The callback-based approach respects NResilience's design philosophy: explicit insertion point, zero cost when unused, and integration with the verdict system. A hand-rolled `SemaphoreSlim` requires manual handling of the deadline, exception flow on timeout, and outcome classification. The limiter handles these for you.

### Can I add my own policy layer?
You cannot add layers through composition because the engine is [one flat method](./deep-dives/one-executor.md). Extension points include the [classifier](./features/classification.md), `Backoff.Custom`, `BeforeAttempt`, `Admit`, and `OnEvent`. This restricted surface ensures long-term API stability.

A custom admission-control guard - a distributed lock, a hand-rolled limiter, anything that should
refuse a call before it reaches the dependency - is not a sixth item on that list. It composes
through the callback and the classifier, or through `Admit` directly, and gets the same treatment as
the built-in rate limiter: correct backoff curve, no charge to the retry budget, no evidence against
the breaker. See [Building a custom guard](./deep-dives/admission-control.md#building-a-custom-guard)
for the classifier-based recipe and [The Admit hook](./deep-dives/admission-control.md#the-admit-hook)
for the value-returning one. Do not build this with `BeforeAttempt`: it runs outside the classified
region, so an exception it throws is never turned into a verdict.

### Is a `Breaker` thread-safe? Can I share one across policies?
Yes. Sharing a breaker allows you to treat multiple different calls as the same dependency. The breaker is guarded by an uncontended lock. Using `with` copies the reference to the breaker, not its internal state.

### Does the breaker see attempts or whole operations?
The breaker always samples individual attempts. This provides a consistent behavior regardless of how the policy is configured.

### Why does refusing a call take 100 milliseconds?
A free rejection inside a polling loop creates a CPU spin, turning a load-shedding guard into a load generator. For more details, see [Guarded rejection](./deep-dives/guarded-rejection.md).

### Why is telemetry off for hand-built policies but on for registered ones?
Setting `OnEvent = null` ensures that telemetry is "free when unused." Since policies registered via dependency injection are typically used in production environments, the registration automatically attaches a listener. You can disable this using `telemetry: false` or `ResilienceOptions.Telemetry = false`. Logging works the same way and for the same reason: a registered policy logs, a hand-built one opts in with `WithLogging`, and `ResilienceOptions.Logging = "Off"` turns it off. See [Logging](features/logging.md).

## Compatibility and performance

### Is it AOT and trimming safe?
Yes. Both ahead-of-time (AOT) compilation and trimming are enforced in CI. The build process runs `dotnet publish -p:PublishAot=true` with warnings treated as errors and verifies that the resulting binary executes a policy - including dependency injection, configuration binding, and the meter - while respecting allocation budgets. The core contains no reflection.

### Which frameworks are supported?
NResilience supports `net8.0` and `net10.0`. Both frameworks are tested and gated; specifically, the library ensures there is no "allocation cliff" on `net8.0`.

### Does it work with `IHttpClientFactory`?
Yes. You can use `.AddResilience()` on the client builder. For more information about the two-minute handler rotation for configuration reloads, see [Dependency injection](./di/index.md).
