---
title: Per-host scope
description: Use per-host circuit breakers and retry budgets to prevent a single failing host from affecting calls to other healthy hosts.
order: 2
---

# Per-host scope

When one `HttpClient` talks to multiple hosts, a shared circuit breaker creates a "blast radius" problem: one host fails, the breaker trips, and requests to every other healthy host are blocked.

The NResilience handler prevents that by keeping a separate circuit breaker and retry budget for every host it sees, so a failure at one endpoint only affects requests to that endpoint.

## Default behavior

`BreakerPerHost` and `BudgetPerHost` are on by default. The handler creates a breaker and a retry budget for each authority the first time a request goes to it.

Each of those breakers measures that host's own normal latency and error rate, because [`SlowCalls` and `Failures`](../features/circuit-breaker.md#trip-conditions) are on by default - so a brownout at one host trips that host's breaker and nothing else. The estimates cost about 3.5 KB per host: at the default `MaximumHosts` of 1024, a ceiling of roughly 3.5 MB for a client talking to a thousand hosts, and nothing at all for the usual client talking to three.

## Bound the host registry
 
The registry keeps 1024 hosts by default. Use `MaximumHosts` to change the cap:
 
<!-- snippet: http-max-hosts -->
```csharp
var handler = new ResilienceHandler(options: new HttpResilienceOptions { MaximumHosts = 64 });
```
<!-- endsnippet -->
 
The set of hosts a client talks to is usually a property of the application, not its traffic, so the cap is usually invisible. For a proxy, a crawler, or a webhook dispatcher that reaches it, the least-recently-seen hosts are dropped.
 
Eviction is approximate: a host seen since the last sweep survives the next one, and the registry can briefly exceed its cap while a sweep catches up. The cap bounds growth, and no request ever waits on a sweep.

There is no unbounded mode, for the reason [`PolicyScope<TKey>`](../features/policy-scope.md) has none: unbounded keying is a memory leak with a breaker and a budget on every entry. `MaximumHosts` must be at least 1, and `int.MaxValue` is how you say "effectively unbounded" if you want it anyway.
 
> [!IMPORTANT]
> Eviction discards state. A dropped host forgets if its breaker was open. If this loss of protection is unacceptable, set `BreakerPerHost` and `BudgetPerHost` to `false` and define the guard scope directly on the policy instead.

## The same scoping, keyed by something else

Per-host scoping is one instance of a general mechanism. To key a policy by a tenant, a shard, a queue, or a gRPC channel, use [`PolicyScope<TKey>`](../features/policy-scope.md) - the same bound, the same eviction, keyed the way you choose.

## Monitor host state

Inspect breaker and budget state for every host the handler has seen - useful for health endpoints or dashboards.

<!-- snippet: http-per-host -->
```csharp
// A breaker whose scope is a variable with a name is one an operator can be told about.
var breakers = handler.BreakersByHost();
var budgets = handler.BudgetsByHost();

foreach (var (host, breaker) in breakers)
{
    Console.WriteLine(value: $"{host}: {breaker.State} since {breaker.OpenedAt:O}");
}
```
<!-- endsnippet -->

These methods return a snapshot of the hosts currently tracked. The dictionaries are empty until the first request to a host, and stay empty if the per-host switches are off and the policy provides no breaker or budget of its own. Hosts evicted by `MaximumHosts` are removed from the snapshot.

## Configure a different scope

If you set an explicit [`Breaker`](../features/circuit-breaker.md) or `Budget` on your policy, the handler respects that and does not apply per-host scoping to those guards.

`RetryBudget.Automatic` does not specify a scope, so `BudgetPerHost` scopes it to the host. To share one budget across all hosts, provide a named instance: `Resilience.Http with { Budget = RetryBudget.Of() }`.

The achievable scopes:

| Desired Scope | Configuration |
| :--- | :--- |
| One breaker per host (default) | No change required. |
| One breaker for the entire client (across all hosts) | Set the `Breaker` property on the policy. |
| No circuit breaking | Leave `Breaker` as `null` and set `BreakerPerHost = false`. |
| One budget per host (default) | No change required. |
| One budget for the entire client (across all hosts) | Set `Budget` to a `RetryBudget.Of(...)` or `RetryBudget.Shared(...)` instance. |
| No retry budget | Set `Budget` to `RetryBudget.None` (or `null`) and set `BudgetPerHost = false`. |

## Implementation details

### Telemetry and naming
To allow dashboards to separate hosts, the handler renames the policy for the specific request. For example, a policy named `orders` will appear as `orders:orders.example` in events and telemetry tags. The breaker itself is also named after the host.

### Non-repeatable requests
When a request is marked non-repeatable, the handler still runs the resilience policy but limits it to one attempt. The breaker still observes the outcome and the budget still gets its deposit; the request is simply never sent twice.

### State lifetime
Host state lives as long as the handler does, or until eviction, whichever comes first:
- **`IHttpClientFactory`**: Rotates handler chains every two minutes by default. Per-host state is reset when the handler is rotated.
- **`HttpResilience.CreateClient`**: Per-host state persists for the lifetime of the process if the client is maintained.
- **Eviction**: A host dropped by a `MaximumHosts` sweep resets to a closed breaker and a full budget the next time it is seen.
