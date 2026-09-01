---
title: Logging in DI
description: Read what a registered policy writes through ILogger, and filter it per policy from appsettings.json.
order: 3
---

# Logging in DI

A policy registered in a DI container writes log records through `ILogger` with no calls or lambdas from you: the library understands each event and emits the matching record.

<!-- snippet: logging-registered -->
```csharp
services.AddLogging();
services.AddResilience(name: "payments", policy: Resilience.Http);

// Nothing else to call. The policy logs under "NResilience.payments", which is the category
// an appsettings.json filter matches.
```
<!-- endsnippet -->

A policy you build yourself is not logged until you say so. See [Logging](../features/logging.md#instrument-a-policy-you-built-yourself).

## Filter per policy

Each policy logs under its own category: `NResilience` for a policy with no name, and `NResilience.<name>` otherwise. For a registered policy the name is the registration name; for an `HttpClient` it is the client name.

<!-- snippet: appsettings.logging.json -->
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "NResilience": "Warning",
      "NResilience.payments": "Debug"
    }
  }
}
```
<!-- endsnippet -->

The category is fixed when the listener attaches. Because an HTTP client derives a policy per host, the category does not include the host - otherwise a client talking to fifty hosts would create fifty categories. The host-scoped name still appears in the `Policy` field of every record for structured output queries.

Use `ResilienceLogging.CategoryFor(name)` to get the category in code.

## Set the profile per policy

Set the profile per policy from a section, or for the whole process with `AddResilienceLogging`.

<!-- snippet: appsettings.logging-verbose.json -->
```json
{
  "Resilience": {
    "payments": {
      "Preset": "Http",
      "Logging": "Verbose"
    },
    "reports": {
      "Preset": "Http",
      "Logging": "Off"
    }
  }
}
```
<!-- endsnippet -->

An explicit `WithLogging` call in a `configure` callback overrides the automatic listener added by a container.

## Provenance

Binding a configuration section is silently partial. Event 1020 reports the effective policy once per resolution at `Debug`; a reload produces a new entry, showing exactly what changed.

Event 1021 is a `Trace` companion that dumps the classifier's state, so you can see what the policy will retry without reading the source. It is guarded to cost nothing when `Trace` is disabled.

## Next steps

- [Logging](../features/logging.md): The profiles, the levels, and how to instrument a policy you built yourself.
- [Event IDs](../reference/events.md#log-event-ids): The full table, which is the contract an alert is built on.
- [Options reference](../reference/options.md): `Logging` and the rest of the bindable shape.
