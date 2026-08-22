# NResilience.Extensions

Dependency injection, configuration binding, rate and concurrency limiting, and OpenTelemetry-shaped telemetry for [NResilience](https://github.com/nresilience/NResilience).

## Install

Install the package using the .NET CLI:

```bash
dotnet add package NResilience.Extensions
```

## What it adds

The extensions package provides the following features:

- `AddResilience()` on `IHttpClientFactory` - the one line most apps need. Adds the resilience handler, stops `HttpClient.Timeout` competing with the policy deadline, names the policy after the client, and attaches the telemetry meter and log listener.
- Named policies - define a dependency's resilience requirements once, resolve them by name from `IResiliencePolicies` anywhere in the app.
- Configuration binding with hot reload - bind a `Resilience` value from `IConfiguration`. A reload is a reference swap, not a rebuild, so breaker and budget state survive.
- Rate and concurrency limiting - `System.Threading.RateLimiting`-backed limiters that compose with the executor.
- Telemetry - OpenTelemetry-shaped metrics and traces, plus `ILogger` structured logs with per-policy filtering.

## Quick start

Use the following examples to get started:

```csharp
// The one line most people need. The handler is added, the transport timeout stops
// competing with the deadline, and the client is instrumented.
services.AddHttpClient<OrdersClient>().AddResilience();

// Or with a policy of your own, or a registered one by name.
services.AddHttpClient("reports").AddResilience(Resilience.Http with { Attempts = 5 });
services.AddHttpClient("payments").AddResilience("api", o => o.RetryUnsafeMethods = false);
```

## Documentation

For more information, see the following resources:

- [Dependency injection](https://github.com/nresilience/NResilience/blob/main/docs/di/index.md) - `AddResilience`, named policies, hot reload, state persistence.
- [Configuration](https://github.com/nresilience/NResilience/blob/main/docs/di/configuration.md) - the bindable shape and JSON limitations.
- [Telemetry](https://github.com/nresilience/NResilience/blob/main/docs/di/telemetry.md) - which metrics are on by default and how to manage them.
- [Logging](https://github.com/nresilience/NResilience/blob/main/docs/di/logging.md) - what a registered policy writes through `ILogger` and how to filter it.

## Feedback

Provide feedback using these channels:

- [Usage questions](https://github.com/nresilience/NResilience/discussions)
- [Bug reports and feature requests](https://github.com/nresilience/NResilience/issues/new/choose)
- [Security vulnerabilities](https://github.com/nresilience/NResilience/security/advisories/new) - private advisory, not a public issue.