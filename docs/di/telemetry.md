---
title: Telemetry in DI
description: Manage telemetry and observability for policies registered in a DI container.
order: 2
---

# Telemetry in DI

Observability is critical for managing resilience in production. When you register policies in a DI container, NResilience automatically instruments them to provide real-time insights into call durations, retry rates, and circuit breaker state. This telemetry is exposed through a standard meter, making it compatible with most monitoring tools.

To disable telemetry for a specific policy, use one of the following switches:
- **Configuration**: Set `ResilienceOptions.Telemetry = false`.
- **HTTP registration**: Set `telemetry: false` during the registration call.

## Collect telemetry

To collect metrics and traces, configure OpenTelemetry to use the NResilience meter and activity source.

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(ResilienceTelemetry.MeterName))
    .WithTracing(t => t.AddSource(ResilienceTelemetry.ActivitySourceName));
```

Both `MeterName` and `ActivitySourceName` are `"NResilience"`. For a full list of instruments, see [Telemetry](../features/telemetry.md). The primary metric to monitor is the retry fraction: `nresilience.attempts ÷ nresilience.calls`.

## Distributed tracing and spans

NResilience adds a **span** (a unit of distributed tracing) for every call. 

For HTTP client registrations, NResilience places a telemetry handler ahead of the resilience handler. This ensures the activity covers every attempt, providing a clear boundary that shows when multiple sends belong to a single logical call.

Attempts, retries, and circuit breaker transitions are recorded as span events. Each event is tagged with:
- The attempt number.
- The verdict.
- The delay.
- The exception type (if applicable).

The library uses `StartActivity`, which returns `null` if the tracing system is not sampling. This ensures that if no one is recording traces, the registered handler adds negligible overhead.

## Instrument manually created policies

If you create a policy manually (e.g., in a static field), it is not instrumented by default. Use the `WithTelemetry()` method to enable it.

<!-- snippet: telemetry-with-telemetry -->
```csharp
// A policy registered in a container is instrumented for you. A policy in a static field
// is not - this says it.
var api = (Resilience.Http with { Name = "payments" }).WithTelemetry();
```
<!-- endsnippet -->

The `WithTelemetry` method chains the instrumentation after any existing `OnEvent` listener rather than replacing it. Calling it multiple times on the same policy only applies the instrumentation once to prevent double-counting.

## Logging

A registered policy also writes `ILogger` records, under a category of `NResilience.<policy>` so you can filter them per policy from `appsettings.json`. Nothing above `Trace` is written while your dependencies are healthy.

The records carry what each event means rather than a generic dump of the event fields, which is the difference between a log that resolves an incident and one that adds to it.

See [Logging in DI](logging.md).
