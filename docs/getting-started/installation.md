---
title: Installation
description: Learn about the NResilience packages and which ones you need for your project.
order: 3
---

# Installation

Install the packages you need using the .NET CLI:

```bash
dotnet add package NResilience              # Core engine, policy values, and HttpClient handler
dotnet add package NResilience.Extensions   # Dependency injection, configuration, and metrics
dotnet add package NResilience.Testing      # Scripted callbacks and recording listeners
```

## Package details

| Package | Use case | Depends on |
| --- | --- | --- |
| `NResilience` | Always. This contains the full API, including HTTP support. | None |
| `NResilience.Extensions` | Use this for projects with a DI container, configuration, or OpenTelemetry. | `NResilience`, `Microsoft.Extensions.*` |
| `NResilience.Testing` | Use this in test projects. | `NResilience` |

`NResilience` has no external dependencies, targets `net8.0` and `net10.0`, and is compatible with Native AOT and trimming. The `HttpClient` handler is included in the core package to simplify installation.

`NResilience.Extensions` is kept separate because it depends on the `Microsoft.Extensions.*` family. Use the core package for libraries to avoid imposing a specific hosting model on your consumers. Use the extensions package for applications with a DI container.

## Which package do I need?

If you call an HTTP API from an ASP.NET Core application, install `NResilience.Extensions` (which includes `NResilience`) and register the resilience handler:

<!-- snippet: migration-registration -->
```csharp
services.AddHttpClient<Client>().AddResilience();
```
<!-- endsnippet -->

For more information, see [Dependency injection](../di/index.md).
