---
title: Configure from appsettings
description: Implement dynamic policy configuration using appsettings.json, allowing settings to be updated without redeploying.
order: 3
---

# Configure from appsettings

In production environments, you often need to tune resilience parameters - such as deadlines or attempt counts - without redeploying your entire application. NResilience allows you to bind these settings to a configuration section (like `appsettings.json`) while keeping complex logic, such as classifiers and shared circuit breakers, in the application code.

## Implementation example

The following example demonstrates how to register policies from a configuration section and attach a named policy to an `HttpClient`.

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
      "TransientBaseDelay": "00:00:00.200",
      "ThrottledBaseDelay": "00:00:02",
      "MaxDelay": "00:00:10",
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
      "BudgetFraction": 0.2
    }
  }
}
```
<!-- endsnippet -->

### Key implementation details

- **Named Policies**: Every child of the configuration section becomes a policy named by its key (e.g., `"api"` and `"reports"`). The `Preset` property defines the starting settings, and all other properties override those defaults.
- **Eager Validation**: Policy registration is validated at startup. If a value is invalid (for example, a negative deadline), the application fails immediately rather than failing during the first request.
- **Hot Reload**: When the configuration file changes, NResilience updates the policies. However, the set of registered policy names is read only once at startup; adding a new name to the JSON file after the application has started will not result in a new injectable policy.
- **Telemetry**: Using `AddResilience("api")` on a client ensures that the policy name is preserved in the telemetry tags, making it easy to distinguish between different policies in your monitoring tools.

## Use reloaded policies

To ensure your application picks up configuration changes, inject `IResiliencePolicies` and resolve the policy by name on every call.

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

### Persistence and Timing

- **State Persistence**: When a policy is reloaded, it preserves its live circuit breaker state and its accumulated retry budget.
- **HttpClient Rotation**: An `HttpClient` observes a reloaded policy at the next handler rotation. By default, `IHttpClientFactory` rebuilds handler chains every two minutes.

## Add complex logic via callbacks

JSON cannot store lambdas or live objects. To configure classifiers, hooks, or shared circuit breakers, use the configuration callback. This callback runs last, ensuring it overrides any settings provided in the JSON section.

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

- [Configuration](../di/configuration.md): Learn about all bindable properties and why NResilience uses a DTO for binding.
- [`ResilienceOptions` reference](../reference/options.md): Detailed reference for the options object.
