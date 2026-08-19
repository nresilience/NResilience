---
title: Installation
description: The three packages, what each one adds, and which ones you need.
order: 3
---

# Installation

```bash
dotnet add package NResilience              # the engine, the policy value, the HttpClient handler
dotnet add package NResilience.Extensions   # AddResilience(), configuration, metrics
dotnet add package NResilience.Testing      # scripted callbacks and a recording listener
```

| Package | Add it when | Depends on |
| --- | --- | --- |
| `NResilience` | Always. It is the whole API, HTTP included. | Nothing |
| `NResilience.Extensions` | You have a DI container, configuration, or OpenTelemetry. | `NResilience`, `Microsoft.Extensions.*` |
| `NResilience.Testing` | In test projects. | `NResilience` |

`NResilience` has no dependencies at all, targets `net8.0` and `net10.0`, is Native AOT and
trimming clean, and ships a checked-in public API manifest. The `HttpClient` handler is in it
rather than in a package of its own: the core already classifies `HttpResponseMessage` and reads
`Retry-After`, and the handler needs nothing beyond the shared framework, so separating it would
have bought a consumer nothing and cost them an extra install.

`NResilience.Extensions` is the one package deliberately kept separate. It pulls in the
`Microsoft.Extensions.*` family, and a library that wants resilience internally should not have to
impose a hosting model on its own consumers.

## Which one do I actually need?

If you are calling an HTTP API from an ASP.NET Core application, install `NResilience.Extensions`
(which brings in `NResilience`) and write one line:

<!-- snippet: migration-registration -->
```csharp
services.AddHttpClient<Client>().AddResilience();
```
<!-- endsnippet -->

Go deeper: [Dependency injection](../di/index.md).

