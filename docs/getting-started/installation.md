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
dotnet add package NResilience.Grpc         # The gRPC client interceptor
dotnet add package NResilience.AspNetCore   # Middleware that reads an inbound deadline
```

## Package details

| Package | Use case | Depends on |
| --- | --- | --- |
| `NResilience` | Always. This contains the full API, including HTTP support. | None |
| `NResilience.Extensions` | Use this for projects with a DI container, configuration, or OpenTelemetry. | `NResilience`, `Microsoft.Extensions.*` |
| `NResilience.Testing` | Use this in test projects. | `NResilience` |
| `NResilience.Grpc` | Use this for a gRPC client. See [gRPC](../grpc/index.md). | `NResilience`, `NResilience.Extensions`, `Grpc.*` |
| `NResilience.AspNetCore` | Use this in a service that should inherit its callers' deadlines. | `NResilience`, ASP.NET Core |

`NResilience` has no external dependencies, targets `net8.0` and `net10.0`, and is compatible with Native AOT and trimming. The `HttpClient` handler is included in the core package to simplify installation.

`NResilience.AspNetCore` is one middleware and nothing else. It is a separate package because it is the only part of NResilience that requires the ASP.NET Core shared framework, which a worker or a console app should not be made to carry - see [deadline propagation](../features/deadlines.md#propagate-the-deadline-across-a-hop).

`NResilience.Grpc` is separate because it depends on `Grpc.Core.Api` and `Grpc.Net.ClientFactory`, and the core package keeps its no-package-dependencies claim. A gRPC client's dependency graph already contains most of that weight.

`NResilience.Extensions` is kept separate because it depends on the `Microsoft.Extensions.*` family. Use the core package for libraries to avoid imposing a specific hosting model on your consumers. Use the extensions package for applications with a DI container.

## Which package do I need?

If you call an HTTP API from an ASP.NET Core application, install `NResilience.Extensions` (which includes `NResilience`) and register the resilience handler:

<!-- snippet: migration-registration -->
```csharp
services.AddHttpClient<Client>().AddResilience();
```
<!-- endsnippet -->

For more information, see [Dependency injection](../di/index.md).
