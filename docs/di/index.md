---
title: Dependency injection
description: Integrate NResilience with DI containers using named policies and hot reload.
order: 5
---

# Dependency injection

NResilience integrates with dependency injection (DI) containers to simplify the management of resilience policies in production applications. The `NResilience.Extensions` package allows you to:
- Integrate handlers into the `IHttpClientFactory` pipeline.
- Name policies to distinguish clients in monitoring dashboards.
- Apply configuration changes without redeploying your application.

To get started, add the extensions package:

```bash
dotnet add package NResilience.Extensions
```

## Use resilience with HttpClient

You can add resilience to a typed or named `HttpClient` with a single method call.

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

Calling `AddResilience` performs the following actions:
- Adds the [resilience handler](../http/index.md).
- Configures the client so that `HttpClient.Timeout` does not compete with the policy deadline.
- Names the policy after the client for better observability.
- Attaches the [telemetry](telemetry.md) meter and the [log listener](logging.md).

## Register named policies

Named policies allow you to define the resilience requirements for a dependency in one place and reuse them across the application.

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

Policy registration is validated eagerly. For example, setting `Attempts = 0` causes a failure at startup rather than during the first request.

### Inject the policy roster

Inject `IResiliencePolicies` (the roster of registered policies) instead of a specific policy instance.

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
> Resolve the policy from the roster on every call. If you capture a policy in a `readonly` field during construction, you create a snapshot. This prevents configuration reloads from reaching the policy.

Use `TryGet` for a non-throwing lookup. The `Names` property returns the list of all registered policy names.

## Hot reload and state persistence

Hot reload allows you to change policy settings without redeploying. Because policies are immutable values, a reload is a simple reference swap: `IOptionsMonitor` triggers, the configuration section projects onto a new `Resilience` instance, and the roster provides the new instance to callers.

### Persisting breaker and budget state

Circuit breaker and retry budget states are not replaced during a reload because their internal state is critical for stability.
- A **circuit breaker** that is open because a dependency is down remains open across a configuration edit.
- A **retry budget** preserves its traffic history.

Budgets and breakers are pinned to the registration name rather than the policy instance to ensure this continuity.

### HttpClient reload timing

An `HttpClient` observes a reloaded policy at the next handler rotation. By default, `IHttpClientFactory` rebuilds handler chains every two minutes. Handlers hold per-host state; rebuilding them on every request to achieve instant reloads would discard this state.

## Next steps

- [Configuration](configuration.md): Learn about the bindable configuration shape and JSON limitations.
- [Telemetry](telemetry.md): Learn which metrics are enabled by default and how to manage them.
- [Logging](logging.md): Learn what a registered policy writes through `ILogger` and how to filter it per policy. For the profiles and levels, see [Logging](../features/logging.md).
