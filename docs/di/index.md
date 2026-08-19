---
title: Dependency injection
description: AddResilience() on a client or a service collection, named policies, and hot reload.
order: 5
---

# Dependency injection

```bash
dotnet add package NResilience.Extensions
```

## The one line

<!-- snippet: di-http-client -->
```csharp
// The one line most people need. The handler is added, the transport timeout stops
// competing with the deadline, and the client is instrumented.
services.AddHttpClient<OrdersClient>().AddResilience();

// Or with a policy of your own, or a registered one by name.
services.AddHttpClient("reports").AddResilience(Resilience.Http with { Attempts = 5 });
services.AddHttpClient("payments").AddResilience("api", o => o.RetryUnsafeMethods = false);
```
<!-- endsnippet -->

That adds the [handler](../http/index.md), stops `HttpClient.Timeout` competing with the deadline,
names the policy after the client so a dashboard can tell four clients apart, and attaches the
[meter](telemetry.md).

## Named policies

<!-- snippet: di-register-named -->
```csharp
// Say what a dependency is worth once, in one place.
services.AddResilience("api", Resilience.Http with { Deadline = TimeSpan.FromSeconds(10) });

// Or in code, without a policy value.
services.AddResilience("reports", o =>
{
    o.Preset = "Http";
    o.Attempts = 5;
    o.Deadline = TimeSpan.FromMinutes(5);
});
```
<!-- endsnippet -->

Registration validates eagerly, so `Attempts = 0` fails at startup rather than on the first request.

Inject the roster, not a policy:

<!-- snippet: di-inject -->
```csharp
public sealed class Orders(IResiliencePolicies policies)
{
    // Resolve on every call rather than into a readonly field: a policy captured at
    // construction time is a snapshot, and a configuration reload will never reach it.
    // The indexer is a dictionary lookup.
    public Task<string> ReadAsync(CancellationToken cancellationToken) =>
        policies["api"].RunAsync(ct => FetchAsync(ct), cancellationToken).AsTask();

    private static Task<string> FetchAsync(CancellationToken cancellationToken) => Task.FromResult("ok");
}
```
<!-- endsnippet -->

> [!IMPORTANT]
> Resolve on every call. A policy captured into a `readonly` field at construction time is a
> snapshot, and a configuration reload will never reach it. The indexer is a dictionary lookup.

`TryGet` is the non-throwing form; `Names` is the roster, which is also what the exception message
lists when a name is missing.

## What reloads, and what survives it

A policy is an immutable value, so hot reload is a reference swap: `IOptionsMonitor` fires, the
section is projected onto a new `Resilience`, and the roster hands out the new one. There is no
in-flight execution to drain and no pipeline to rebuild.

**Live breakers and budgets are not replaced**, because their state is the point. A breaker that
opened because a dependency is down stays open across a configuration edit, and the automatic retry
budget keeps the traffic history it has accumulated - it is pinned to the registration name rather
than to the policy instance for exactly that reason.

An `HttpClient` sees a reloaded policy at the next handler rotation - every two minutes by default -
rather than on the next request. The handler holds the per-host state, and rebuilding it per request
to make reload instant would throw that state away on every call.

## Next

- [Configuration](configuration.md) - the bindable shape, and what JSON cannot hold.
- [Telemetry](telemetry.md) - what is on by default here, and how to turn it off.

