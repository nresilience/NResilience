---
title: Resource isolation with bulkheads
description: Prevent one dependency's resource exhaustion from starving other parts of your application.
---

# Resource isolation with bulkheads

A **bulkhead** keeps one dependency's resource usage from spilling onto everything else. When a dependency slows down, a bulkhead stops its requests from monopolizing your thread pool or connection pool.

## The problem: cascading thread pool starvation

Imagine your application calls three dependencies: User API (fast), Payment API (sometimes slow), and Search API (fast). When Payment becomes slow, returning responses in 30 seconds instead of 100 ms:

```
┌─────────────────────────────┐
│ Thread pool (200 threads)    │
└──────────┬──────────────────┘
           │
      ┌────┴─────────────────────────┐
      │                              │
   ┌──▼─────┐  ┌────────┐  ┌────────┐
   │User    │  │Payment │  │Search  │
   │API     │  │API     │  │API     │
   │(fast)  │  │(slow)  │  │(fast)  │
   └────────┘  └────────┘  └────────┘
```

Without isolation:

1. 30 requests arrive for Payment API → block 30 threads, each waiting 30 seconds
2. 20 new requests for User API arrive → only 170 threads available, but Payment has 30
3. User API waits for Payment threads to free → User API latency becomes 30+ seconds
4. Clients timeout waiting for User API → cascade failure

**The slow dependency starves the fast ones.**

## The solution: concurrency limits

Bound how many calls run against one dependency at once with `Limit.Concurrency`:

```csharp
using var paymentLimiter = Limit.Concurrency(10);  // at most 10 concurrent calls

var result = await policy.RunAsync(async ct =>
{
    using var lease = await paymentLimiter.AcquireAsync(ct);
    return await paymentApi.AuthorizeAsync(orderId, ct);
}, cancellationToken);
```

> [!TIP]
> `10` is a number you have to keep right as your pod count and the dependency's capacity change. `Limit.Adaptive` measures it instead - see [Let it find its own concurrency](../features/rate-limiting.md#let-it-find-its-own-concurrency).

Now:

1. First 10 requests for Payment acquire permits and run
2. Request 11 is rejected with backoff → doesn't block a thread
3. User API requests run freely on available threads → User API stays fast
4. As Payment requests complete, queued requests retry on the long backoff curve

**The fast dependencies stay responsive while Payment recovers.**

## When to use bulkheads

Add a bulkhead when:

- **Multi-dependency systems** - your application calls several external services
- **Resource contention** - high concurrency (> 1,000 RPS) where one slow dependency could monopolize your resources
- **Variable performance** - dependencies that are sometimes fast and sometimes slow
- **Shared resource pools** - database connection pools, OAuth token quotas, or rate limit buckets that must be shared fairly

## When you don't need bulkheads

Skip bulkheads if:

- **Single dependency** - your application only calls one backend
- **Low concurrency** - fewer than 100 requests per second
- **Service mesh** - a service mesh like Istio/Envoy handles isolation at the infrastructure layer
- **Separate pods per dependency** - each pod connects to only one database or service

## Size a local limit for a fleet

A bulkhead bounds concurrency inside one process, so the limit you configure is per pod, not per fleet. Scale horizontally and the aggregate concurrency against a dependency is the per-pod limit times the replica count.

The per-process design is deliberate: a local limit needs no coordination, so it stays fast. The trade-off is that the library cannot see the fleet - sizing the local limit for the fleet is your job.

To size the limit, start from the external dependency's capacity and divide by the number of replicas you run:

```text
local limit = floor(external capacity / replica count)
```

Suppose a payment provider allows 1,000 concurrent connections. You expect to run 50 pods. Set the per-pod bulkhead to 20, not 1,000:

```text
floor(1000 / 50) = 20 concurrent calls per pod
```

A limit of 1,000 per pod feels safe in a local test, but at 50 pods it is 50,000 concurrent connections against a provider that caps at 1,000. The provider rejects the overflow with rate-limit errors, and the local bulkhead never tripped once.

Work backwards from the external cap, not forwards from a number that looks right on one machine. If the replica count changes, the local limit must change with it - a horizontal pod autoscaler can make a fixed limit wrong within an hour of deploying it.

> [!IMPORTANT]
> A local bulkhead protects the process; it does not orchestrate the fleet. For a hard ceiling across a cluster, use a service mesh or an external rate limiter that the dependency provider enforces, and keep the in-process bulkhead as a second line of defense.

## Implement a bulkhead for HTTP

For HTTP clients registered through DI, the handler can scope a limiter per host automatically:

```csharp
services
    .AddHttpClient<PaymentClient>("payments")
    .AddResilience(Resilience.Http)
    .AddRateLimit(options =>
    {
        options.Concurrency = 10;        // at most 10 concurrent calls to payments API
        options.PerHost = true;          // separate limits per host (the default)
    });

services
    .AddHttpClient<UserClient>("users")
    .AddResilience(Resilience.Http)
    .AddRateLimit(options =>
    {
        options.Concurrency = 50;        // user API is fast, allow more concurrency
    });

services
    .AddHttpClient<SearchClient>("search")
    .AddResilience(Resilience.Http);    // no limit needed, it's plenty fast
```

> [!IMPORTANT]
> `.AddRateLimit()` must come **after** `.AddResilience()`. Handlers execute in registration order (outermost first), so the limiter sits inside the retry loop, taking one permit per attempt rather than one per operation.

## Implement a bulkhead for non-HTTP calls

For database queries, queues, and other dependencies, put the limiter inside the policy callback:

```csharp
using var queryLimiter = Limit.Concurrency(20);

var result = await policy.RunAsync(async ct =>
{
    using var lease = await queryLimiter.AcquireAsync(ct);
    return await database.QueryAsync<User>("SELECT ...", ct);
}, cancellationToken);
```

The `using` block releases the permit when the attempt completes, whether it succeeds or fails. Placing the limiter *inside* the callback means:

- Retries acquire a fresh permit for each attempt (not cached across retries)
- The wait is bounded by the deadline (no separate timeout to configure)
- Refusals are classified correctly (see [verdict integration](#verdict-integration-why-refusals-dont-open-breakers) below)

## Verdict integration: why refusals don't open breakers

When a limiter denies a call, the outcome is classified as `Verdict.Throttled(SelfImposed: true)`:

```csharp
var result = await policy.TryRunAsync(async ct =>
{
    using var lease = await paymentLimiter.AcquireAsync(ct);
    return await dependency.CallAsync(ct);
}, cancellationToken);

if (!result.IsSuccess && result.Attempts[0].Verdict.SelfImposed)
{
    // This is our own throttling, not a dependency failure.
    // It will retry on the long backoff curve (1 second base, not 100 ms).
    // The circuit breaker records nothing.
    // The retry budget is not charged.
}
```

Three things follow from that verdict:

| Aspect | Effect | Why |
| :--- | :--- | :--- |
| Retry curve | Long (1 s base) | You are defending a healthy dependency |
| Circuit breaker | Not recorded | Only `Transient` is evidence; refusals are self-imposed |
| Retry budget | Not charged | The call never left this process; no amplification cost |

## Real-world example: tiered concurrency limits

A microservice talks to a database (with 20 connection pool slots) and runs three different queries:

```csharp
// Expensive aggregation query - limit concurrency to prevent it from
// monopolizing the connection pool
using var aggregateQueryLimiter = Limit.Concurrency(3);

// Fast point queries - allow higher concurrency
using var fastQueryLimiter = Limit.Concurrency(15);

// Writes - give them priority by allowing all available connections
using var writeQueryLimiter = Limit.Concurrency(20);

public async Task<AggregateResult> GetAggregateAsync(CancellationToken ct)
{
    return await policy.RunAsync(async innerCt =>
    {
        using var lease = await aggregateQueryLimiter.AcquireAsync(innerCt);
        // Run expensive aggregation (e.g., 5 second query)
        return await db.QueryAsync<AggregateResult>("SELECT ... GROUP BY ...", innerCt);
    }, ct);
}

public async Task<User> GetUserAsync(int id, CancellationToken ct)
{
    return await policy.RunAsync(async innerCt =>
    {
        using var lease = await fastQueryLimiter.AcquireAsync(innerCt);
        // Run fast point query (e.g., 10 ms query)
        return await db.QueryFirstAsync<User>("SELECT * FROM Users WHERE Id = @Id", id, innerCt);
    }, ct);
}

public async Task UpdateUserAsync(int id, string name, CancellationToken ct)
{
    await policy.RunAsync(async innerCt =>
    {
        using var lease = await writeQueryLimiter.AcquireAsync(innerCt);
        // Run write (e.g., 20 ms query)
        return await db.ExecuteAsync("UPDATE Users SET Name = @Name WHERE Id = @Id", 
            new { Id = id, Name = name }, innerCt);
    }, ct);
}
```

**Result**: Under heavy load, expensive queries are limited to 3 concurrent calls (using 3 connections), fast queries use up to 15 (fair share of connections), and writes get all remaining slots. No single query type starves the others.

## Shared bulkheads across multiple policies

If multiple policies must respect the same limit (a global rate limit across a feature, say), share the limiter instance:

```csharp
// One limiter shared across all OAuth operations
using var oauthLimiter = Limit.Concurrency(5);

public async Task<Token> GetTokenAsync(string scopes, CancellationToken ct)
{
    return await oauthPolicy.RunAsync(async innerCt =>
    {
        using var lease = await oauthLimiter.AcquireAsync(innerCt);
        return await oauthProvider.AcquireTokenAsync(scopes, innerCt);
    }, ct);
}

public async Task<Token> RefreshTokenAsync(string refreshToken, CancellationToken ct)
{
    return await oauthPolicy.RunAsync(async innerCt =>
    {
        using var lease = await oauthLimiter.AcquireAsync(innerCt);
        return await oauthProvider.RefreshTokenAsync(refreshToken, innerCt);
    }, ct);
}
```

Both operations share the same 5-call limit, so 3 concurrent `GetTokenAsync` calls plus 2 concurrent `RefreshTokenAsync` calls saturate the limiter.

## Monitoring

The rate limiter reports two metrics on the same meter as other resilience events:

| Metric | What it measures |
| :--- | :--- |
| `nresilience.limiter.leases` (counter) | Permits acquired or denied, tagged with outcome |
| `nresilience.limiter.wait.duration` (histogram) | How long callers waited (only if queueing is enabled) |

Access them through the standard OpenTelemetry API:

```csharp
var meter = new Meter("MyApplication");
var limiterLeases = meter.CreateObservableCounter<long>(
    "nresilience.limiter.leases",
    () => /* read from instrumentation */);
```

Refusals also raise a `CallEvent`:

```csharp
var policy = Resilience.Http with
{
    OnEvent = e =>
    {
        if (e.IsRejection)
        {
            logger.LogWarning("A guard refused a call to {Service}: {Kind}", e.PolicyName, e.Kind);
        }
    }
};
```

## Tuning concurrency limits

Start conservative: pick a limit that seems low, then raise it based on observed latency and error rates.

| Scenario | Starting point |
| :--- | :--- |
| Database queries (20-connection pool) | 10-15 concurrent |
| HTTP APIs (unlimited connections) | 20-50 concurrent |
| OAuth token service (limited quota) | 5-10 concurrent |
| Microservice (high throughput) | 50-100 concurrent |

Use these signals to decide whether your limit is right:

- **Permit deny rate**: If > 5% of attempts are denied, the limit may be too low
- **Dependency latency**: If latency is stable, the limit is probably good
- **Thread pool queue depth**: If threads are queuing, consider raising the limit
- **Application latency p99**: If your app's latency increases but the dependency's doesn't, investigate whether a limit is too strict

## Compare to other patterns

Bulkheads work alongside the other resilience patterns:

| Pattern | Controls | Example |
| :--- | :--- | :--- |
| **Timeout** | How long to wait for one call | `AttemptTimeout = 10s` |
| **Retry** | Whether and how often to retry | `Attempts = 3` |
| **Breaker** | Stop calling broken dependencies | Open after 5 consecutive errors |
| **Budget** | Retries as fraction of traffic | 10% of requests may retry |
| **Bulkhead** | Concurrent calls per dependency | At most 10 calling at once |

All five are defensive. Together:

1. **Timeout** prevents hung calls
2. **Retry** recovers from transient failures
3. **Breaker** stops calling broken services
4. **Budget** prevents retry storms
5. **Bulkhead** prevents resource exhaustion

## FAQ

**Q: Should I set a bulkhead limit equal to my database connection pool size?**

A: Not exactly. If your pool has 20 connections, a limit of 20 means *all* queries could acquire a connection. But:
- Aggregate queries might hold a connection for 5 seconds
- Fast queries release in 10 ms
- If you allow 20 concurrent aggregate queries, you use all 20 connections for 5 seconds

Better: limit expensive queries to 3-5 concurrent calls, fast queries to 15, and writes to the remainder.

**Q: What's the difference between a bulkhead and the retry budget?**

A: A **bulkhead** bounds absolute concurrency. The **retry budget** bounds retries as a fraction of traffic.

- Budget prevents retry storms: 10% of requests can retry, so max 1.1× amplification
- Bulkhead prevents thread starvation: at most 10 concurrent calls, period

Use both. The budget prevents storms; the bulkhead prevents starvation.

**Q: Can I use a bulkhead on the policy instead of in the callback?**

A: Not usefully. With the limiter outside the retry loop, one operation holds one permit across all of its attempts:

```csharp
// ❌ Wrong: bulkhead outside the callback
services.AddHttpClient("api")
    .AddRateLimit(o => o.Concurrency = 10)     // Outside retry
    .AddResilience(Resilience.Http);           // Inside bulkhead

// Problem: A single operation acquires one permit, then makes up to 3 attempts.
// If all 3 attempts fail, you "wasted" the permit on a failed operation.
```

```csharp
// ✅ Right: bulkhead inside the callback
services.AddHttpClient("api")
    .AddResilience(Resilience.Http)            // Retry
    .AddRateLimit(o => o.Concurrency = 10);    // Inside retry

// Benefit: Each attempt acquires its own permit. Retries can immediately retry
// without waiting for the first permit to release.
```

With the limiter inside the retry loop, each attempt acquires its own permit, so a retry does not wait for the first permit to release.

**Q: Does my bulkhead limit need to account for retries?**

A: No. If you set `Limit.Concurrency(10)` and one call retries twice, it still counts as occupying one slot: one in-flight operation, three attempts.

The limit bounds what's *in flight*, not how many attempts happen.

**Q: What happens if a call acquires a permit, then times out?**

A: The `using` block releases the permit automatically when the attempt times out.

```csharp
using var lease = await limiter.AcquireAsync(ct);
try
{
    return await dependency.CallAsync(ct);  // times out → permit released in finally
}
// The permit is released here, in the finally block
```

## See also

- [Rate limiting](../features/rate-limiting.md) - full reference
- [Circuit breaker](../features/circuit-breaker.md) - detect and stop calling failures
- [Retry budget](../features/retry-budget.md) - prevent retry storms
- [Admission control](../deep-dives/admission-control.md) - deep dive on how refusals are classified
