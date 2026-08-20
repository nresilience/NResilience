---
title: FAQ
description: Technical reasoning behind design decisions in NResilience.
order: 12
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
No. A retry loop that blocks holds a thread through every backoff delay. Offering both synchronous and asynchronous APIs would either duplicate the engine or risk deadlocks. For this reason, `ResilienceHandler.Send` throws a `NotSupportedException`.

### Where is hedging?
Hedging is not implemented. Issuing a second request before the first fails is a dangerous default because it multiplies load on a dependency exactly when it is slow. Implementing hedging safely requires a budget, an adaptive latency threshold, and a per-request idempotency strategy. The [retry budget](../features/retry-budget.md) and the [circuit breaker](../features/circuit-breaker.md) provide the necessary groundwork for this feature.

### Where is a rate limiter?
`System.Threading.RateLimiting` is available in the .NET platform. Wrapping it in a resilience library would introduce a specific opinion rather than a new capability.

### Where is bulkhead isolation?
A bulkhead limits how many concurrent calls can run against one dependency to prevent a failing service from exhausting all available threads. `SemaphoreSlim` or the platform's concurrency limiter provides this functionality. Because the decision of how to handle a full bulkhead occurs at the call site, it does not require a policy.

### Can I add my own policy layer?
You cannot add layers through composition because the engine is [one flat method](../deep-dives/one-executor.md). Extension points include the [classifier](../features/classification.md), `Backoff.Custom`, `BeforeAttempt`, and `OnEvent`. This restricted surface ensures long-term API stability.

### Is a `Breaker` thread-safe? Can I share one across policies?
Yes. Sharing a breaker allows you to treat multiple different calls as the same dependency. The breaker is guarded by an uncontended lock. Using `with` copies the reference to the breaker, not its internal state.

### Does the breaker see attempts or whole operations?
The breaker always samples individual attempts. This provides a consistent behavior regardless of how the policy is configured.

### Why does refusing a call take 100 milliseconds?
A free rejection inside a polling loop creates a CPU spin, turning a load-shedding guard into a load generator. For more details, see [Guarded rejection](../deep-dives/guarded-rejection.md).

### Why is telemetry off for hand-built policies but on for registered ones?
Setting `OnEvent = null` ensures that telemetry is "free when unused." Since policies registered via dependency injection are typically used in production environments, the registration automatically attaches a listener. You can disable this using `telemetry: false` or `ResilienceOptions.Telemetry = false`.

## Compatibility and performance

### Is it AOT and trimming safe?
Yes. Both ahead-of-time (AOT) compilation and trimming are enforced in CI. The build process runs `dotnet publish -p:PublishAot=true` with warnings treated as errors and verifies that the resulting binary executes a policy - including dependency injection, configuration binding, and the meter - while respecting allocation budgets. The core contains no reflection.

### Which frameworks are supported?
NResilience supports `net8.0` and `net10.0`. Both frameworks are tested and gated; specifically, the library ensures there is no "allocation cliff" on `net8.0`.

### Does it work with `IHttpClientFactory`?
Yes. You can use `.AddResilience()` on the client builder. For more information about the two-minute handler rotation for configuration reloads, see [Dependency injection](../di/index.md).
