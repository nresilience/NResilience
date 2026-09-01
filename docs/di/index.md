---
title: Dependency injection
description: Integrate NResilience with DI containers using named policies and hot reload.
order: 6
---

# Dependency injection

NResilience integrates with dependency injection (DI) containers to make resilience policies manageable in production applications. The `NResilience.Extensions` package lets you:
- Add handlers to the `IHttpClientFactory` pipeline.
- Name policies so clients are distinguishable in dashboards.
- Apply configuration changes without redeploying.

To get started, add the extensions package:

```bash
dotnet add package NResilience.Extensions
```

## Use resilience with HttpClient

Add resilience to a typed or named `HttpClient` with one method call.

<!-- snippet: di-http-client -->
```csharp
// The one line most people need. The handler is added, the transport timeout stops
// competing with the deadline, and the client is instrumented.
services.AddHttpClient<OrdersClient>().AddResilience();

// Or with a policy of your own, or a registered one by name.
services.AddHttpClient(name: "reports").AddResilience(policy: Resilience.Http with { Attempts = 5 });
services.AddHttpClient(name: "payments").AddResilience(policyName: "api", o => o.RetryUnsafeMethods = false);
```
<!-- endsnippet -->

`AddResilience` does the following:
- Adds the [resilience handler](../http/index.md).
- Configures the client so `HttpClient.Timeout` does not compete with the policy deadline.
- Names the policy after the client for observability.
- Attaches the [telemetry](telemetry.md) meter and the [log listener](logging.md).
- Records the handler so [health checks](health-checks.md) can report its per-host breakers.

## Register named policies

Named policies let you define a dependency's resilience requirements in one place and reuse them across the application.

<!-- snippet: di-register-named -->
```csharp
// Say what a dependency is worth once, in one place.
services.AddResilience(name: "api", policy: Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10) });

// Or in code, without a policy value.
services.AddResilience(name: "reports", o =>
{
    o.Preset = "Http";
    o.Attempts = 5;
    o.Deadline = TimeSpan.FromMinutes(value: 5);
});
```
<!-- endsnippet -->

Policy registration is validated eagerly: `Attempts = 0` fails at startup rather than on the first request.

### Inject the policy roster

Inject `IResiliencePolicies` (the roster of registered policies) rather than a specific policy instance.

<!-- snippet: di-inject -->
```csharp
public sealed class Orders(IResiliencePolicies policies)
{
    // Resolve on every call rather than into a readonly field: a policy captured at
    // construction time is a snapshot, and a configuration reload will never reach it.
    // The indexer is a dictionary lookup.
    public Task<string> ReadAsync(CancellationToken cancellationToken) =>
        policies[name: "api"].RunAsync(attempt => FetchAsync(cancellationToken: attempt), cancellationToken: cancellationToken).AsTask();

    private static Task<string> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(result: "ok");
}
```
<!-- endsnippet -->

> [!IMPORTANT]
> Resolve the policy from the roster on every call. Capturing a policy in a `readonly` field at construction creates a snapshot, which configuration reloads can never reach.

Use `TryGet` for a non-throwing lookup. The `Names` property lists all registered policy names.

## Hot reload and state persistence

Hot reload changes policy settings without a redeploy. Policies are immutable values, so a reload is a reference swap: `IOptionsMonitor` triggers, the configuration section projects onto a new `Resilience` instance, and the roster hands callers the new instance.

### Persisting breaker and budget state

Circuit breaker and retry budget states survive a reload, because discarding them would discard stability:
- A **circuit breaker** that is open because a dependency is down stays open across a configuration edit.
- A **retry budget** keeps its traffic history.

Budgets and breakers are pinned to the registration name rather than the policy instance to keep this continuity.

### HttpClient reload timing

An `HttpClient` observes a reloaded policy at the next handler rotation. `IHttpClientFactory` rebuilds handler chains every two minutes by default. Handlers hold per-host state, so rebuilding them on every request for instant reloads would throw that state away.

## Next steps

- [Configuration](configuration.md): The bindable configuration shape, and what JSON cannot express.
- [Telemetry](telemetry.md): Which metrics are on by default, and how to manage them.
- [Logging](logging.md): What a registered policy writes through `ILogger`, and how to filter it per policy. For profiles and levels, see [Logging](../features/logging.md).
