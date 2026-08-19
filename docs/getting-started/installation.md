---
title: Installation
description: The four packages, what each one adds, and which ones you need.
order: 3
---

# Installation

```bash
dotnet add package NResilience              # the engine and the policy value
dotnet add package NResilience.Http         # the HttpClient handler
dotnet add package NResilience.Extensions   # AddResilience(), configuration, metrics
dotnet add package NResilience.Testing      # scripted callbacks and a recording listener
```

| Package | Add it when | Depends on |
| --- | --- | --- |
| `NResilience` | Always. It is the whole API for non-HTTP work. | Nothing |
| `NResilience.Http` | You call HTTP and want request cloning, idempotency and per-host scope. | `NResilience` |
| `NResilience.Extensions` | You have a DI container, configuration, or OpenTelemetry. | `NResilience.Http`, `Microsoft.Extensions.*` |
| `NResilience.Testing` | In test projects. | `NResilience` |

`NResilience` has no dependencies at all, targets `net8.0` and `net10.0`, is Native AOT and
trimming clean, and ships a checked-in public API manifest.

## Which one do I actually need?

If you are calling an HTTP API from an ASP.NET Core application, install `NResilience.Extensions`
(which brings the other two) and write one line:

<!-- snippet: migration-registration -->
```csharp
services.AddHttpClient<Client>().AddResilience();
```
<!-- endsnippet -->

Go deeper: [Dependency injection](../di/index.md).

