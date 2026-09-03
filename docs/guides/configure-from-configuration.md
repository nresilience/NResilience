---
title: Configure from appsettings
description: Implement dynamic policy configuration using appsettings.json, allowing settings to be updated without redeploying.
order: 3
---

# Configure from appsettings

In production you often need to tune resilience settings - deadlines, attempt counts - without redeploying. NResilience binds these settings to a configuration section (like `appsettings.json`) while classifiers, shared circuit breakers, and other live objects stay in code.

## Implementation example

This example registers policies from a configuration section and attaches a named policy to an `HttpClient`.

<!-- snippet: guide-configure-from-configuration -->
```csharp
// One policy per child of the section, each named by its key. Values reload; the roster is
// read once, because a name that appears in the file after the container is built has
// nothing to be injected into.
services.AddResilience(section: configuration.GetSection(key: "Resilience"));

services.AddHttpClient(name: "orders").AddResilience(policyName: "api");
```
<!-- endsnippet -->

### Example configuration

The `AddResilience` method expects a configuration section structured as follows:

<!-- snippet: appsettings.resilience.json -->
```json
{
  "Resilience": {
    "api": {
      "Preset": "Http",
      "Attempts": 3,
      "Deadline": "00:00:10",
      "AttemptTimeout": "00:00:03",
      "Backoff": {
        "TransientBase": "00:00:00.200",
        "ThrottledBase": "00:00:02",
        "Max": "00:00:10"
      },
      "Breaker": {
        "ConsecutiveFailures": 5,
        "BreakDuration": "00:00:15",
        "SlowCalls": { "Multiple": 3 }
      }
    },
    "reports": {
      "Preset": "Http",
      "Attempts": 5,
      "Deadline": "00:05:00",
      "Budget": { "Fraction": 0.2 }
    }
  }
}
```
<!-- endsnippet -->

### Key implementation details

- **Named policies**: Every child of the section becomes a policy named by its key (`"api"`, `"reports"`). `Preset` sets the starting settings; every other property overrides those defaults.
- **Eager validation**: Policy registration is validated at startup. An invalid value (a negative deadline, for example) fails the application immediately rather than on the first request.
- **Hot reload**: When the configuration file changes, NResilience updates the policies. The set of policy names is read once at startup, though - a name added to the JSON after startup does not become a new injectable policy.
- **Telemetry**: `AddResilience("api")` keeps the policy name in the telemetry tags, so you can tell policies apart in your monitoring tools.

## Use reloaded policies

To pick up configuration changes, inject `IResiliencePolicies` and resolve the policy by name on every call.

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

- **State persistence**: When a policy reloads, it keeps its live circuit breaker state and accumulated retry budget.
- **HttpClient rotation**: An `HttpClient` observes a reloaded policy at the next handler rotation. `IHttpClientFactory` rebuilds handler chains every two minutes by default.

## Add complex logic via callbacks

JSON cannot hold lambdas or live objects. For classifiers, hooks, or shared circuit breakers, use the configuration callback. It runs last, so it overrides anything from the JSON section.

<!-- snippet: di-configure-callback -->
```csharp
// Runs last, after the section and after the live objects are re-attached. A classifier is
// a lambda and JSON cannot hold one, so this is where one goes - along with a hook, or a
// breaker you mean to share with something else.
services.AddResilience(
    name: "api",
    section: configuration.GetSection(key: "Resilience:api"),
    policy => policy with
    {
        Classify = Classifier.Http.On<MyTransportException>(verdict: Verdict.Transient),
        Breaker = shared,
    });
```
<!-- endsnippet -->

## For more information

- [Configuration](../di/configuration.md): All bindable properties, and why NResilience binds through a DTO.
- [`ResilienceOptions` reference](../reference/options.md): The options object in detail.
