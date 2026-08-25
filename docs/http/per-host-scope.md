---
title: Per-host scope
description: Use per-host circuit breakers and retry budgets to prevent a single failing host from affecting calls to other healthy hosts.
order: 2
---

# Per-host scope

When a single `HttpClient` communicates with multiple hosts, a shared circuit breaker can create a "blast radius" problem: if one host fails, the breaker trips and blocks requests to all other healthy hosts. 

The NResilience handler prevents this by maintaining a separate circuit breaker and retry budget for every host it encounters. This ensures that a failure at one endpoint only affects requests sent to that specific host.

## Default behavior

`BreakerPerHost` and `BudgetPerHost` are enabled by default. The handler automatically creates a breaker and a retry budget for each authority it sees upon the first request to that host.

## Bound the host registry
 
The registry keeps 1024 hosts by default. Use `MaxHosts` to change the cap, or set it to `null` to remove it:
 
<!-- snippet: http-max-hosts -->
```csharp
var handler = new ResilienceHandler(options: new HttpResilienceOptions { MaxHosts = 64 });
```
<!-- endsnippet -->
 
The set of hosts a client communicates with is typically a property of the application rather than its traffic, so the cap is usually invisible. For a proxy, a crawler, or a webhook dispatcher that reaches the cap, the least-recently-seen hosts are dropped.
 
Eviction is approximate: a host seen since the last sweep survives the next one, and the registry can briefly exceed its cap while a sweep catches up. The cap bounds growth, and no request ever waits on a sweep.
 
> [!IMPORTANT]
> Eviction discards state. A dropped host forgets if its breaker was open. If this loss of protection is unacceptable, set `BreakerPerHost` and `BudgetPerHost` to `false` and define the guard scope directly on the policy instead.

## Monitor host state

You can inspect the state of breakers and budgets for all hosts seen by the handler. This is useful for implementing health endpoints or monitoring dashboards.

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

These methods return a snapshot of the hosts currently tracked. The dictionaries are empty until the first request is made to a host, and remain empty if the per-host switches are disabled and the policy does not provide its own breaker or budget. Hosts evicted by `MaxHosts` are removed from the snapshot.

## Configure a different scope

If you provide an explicit [`Breaker`](../features/circuit-breaker.md) or `Budget` on your policy, the handler respects that decision and does not apply per-host scoping to those guards.

`RetryBudget.Automatic` does not specify a scope, so `BudgetPerHost` scopes it to the host. To share a single budget across all hosts, provide a named instance: `Resilience.Http with { Budget = RetryBudget.Of() }`.

Depending on your requirements, you can achieve the following scopes:

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
When a request is marked as non-repeatable, the handler still runs the resilience policy but limits it to a single attempt. This ensures that the circuit breaker still observes the outcome and the retry budget still receives its deposit, but the request is never sent twice.

### State lifetime
The lifetime of the host state is tied to the handler's lifetime or to eviction, whichever comes first:
- **`IHttpClientFactory`**: Rotates handler chains every two minutes by default. Per-host state is reset when the handler is rotated.
- **`ResilienceHttp.CreateClient`**: Per-host state persists for the lifetime of the process if the client is maintained.
- **Eviction**: A host dropped by a `MaxHosts` sweep resets to a closed breaker and a full budget the next time it is seen.
