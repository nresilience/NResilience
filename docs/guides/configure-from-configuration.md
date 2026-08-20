---
title: Configure from appsettings
description: Policies in a configuration section, reloaded without a deploy, with a callback for what JSON cannot hold.
order: 3
---

# Configure from appsettings

## Scenario

Operations wants to change a deadline or an attempt count without a deploy, and you want the rest of
the policy - the classifier (the rule that decides which failures are worth retrying) and the shared
breaker (a switch that stops calling a failing dependency) - to stay in code where it belongs.

## Complete example

<!-- snippet: guide-configure-from-configuration -->
```csharp
// One policy per child of the section, each named by its key. Values reload; the roster is
// read once, because a name that appears in the file after the container is built has
// nothing to be injected into.
services.AddResilience(configuration.GetSection("Resilience"));

services.AddHttpClient("orders").AddResilience("api");
```
<!-- endsnippet -->

With this section:

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
        "SlowCallThreshold": "00:00:02"
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

## What's happening

- **One policy per child** of the section, named by its key. `Preset` picks the starting point and
  every other property overrides it.
- **Values reload; the roster does not.** The roster is the set of named policies registered at
  startup - a name that appears in the file after the container is built has nothing to be injected
  into, so the set of names is read once at registration.
- **Registration validates eagerly** (at startup, not on the first request), so a deadline of
  minus one second fails at startup rather than on the first request.
- **`AddResilience("api")` on a client** resolves the registered policy by name and keeps its name in
  the telemetry.

## Handle the outcome

Inject `IResiliencePolicies` and resolve per call - a policy captured in a `readonly` field is a
snapshot the reload will never reach:

<!-- snippet: di-inject -->
```csharp
public sealed class Orders(IResiliencePolicies policies)
{
    // Resolve on every call rather than into a readonly field: a policy captured at
    // construction time is a snapshot, and a configuration reload will never reach it.
    // The indexer is a dictionary lookup.
    public Task<string> ReadAsync(CancellationToken cancellationToken) =>
        policies["api"].RunAsync(attempt => FetchAsync(attempt), cancellationToken).AsTask();

    private static Task<string> FetchAsync(CancellationToken cancellationToken) => Task.FromResult("ok");
}
```
<!-- endsnippet -->

A reloaded policy keeps its live breaker and its accumulated retry budget. An `HttpClient` picks the
new policy up at the next handler rotation, every two minutes by default, rather than on the next
request.

## Add what JSON cannot hold

<!-- snippet: di-configure-callback -->
```csharp
// Runs last, after the section and after the live objects are re-attached. A classifier is
// a lambda and JSON cannot hold one, so this is where one goes - along with a hook, or a
// breaker you mean to share with something else.
services.AddResilience(
    "api",
    configuration.GetSection("Resilience:api"),
    policy => policy with
    {
        Classify = Classifier.Http.On<MyTransportException>(Verdict.Transient),
        Breaker = shared,
    });
```
<!-- endsnippet -->

## When to go deeper

- [Configuration](../di/configuration.md) - every bindable property, and why the target is a DTO.
- [`ResilienceOptions` reference](../reference/options.md).

