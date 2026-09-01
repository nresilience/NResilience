---
title: Telemetry in DI
description: Manage telemetry and observability for policies registered in a DI container.
order: 2
---

# Telemetry in DI

When you register policies in a DI container, NResilience instruments them automatically, giving you real-time call durations, retry rates, and circuit breaker state. The telemetry is exposed through a standard meter, so it works with most monitoring tools.

To disable telemetry for a specific policy, use one of the following switches:
- **Configuration**: Set `ResilienceOptions.Telemetry = false`.
- **HTTP registration**: Set `telemetry: false` during the registration call.

## Collect telemetry

Configure OpenTelemetry to use the NResilience meter and activity source:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(ResilienceTelemetry.MeterName))
    .WithTracing(t => t.AddSource(ResilienceTelemetry.ActivitySourceName));
```

Both `MeterName` and `ActivitySourceName` are `"NResilience"`. See [Telemetry](../features/telemetry.md) for the full instrument list. The primary metric to watch is the retry fraction: `nresilience.attempts ÷ nresilience.calls`.

## Distributed tracing and spans

NResilience adds a **span** (a unit of distributed tracing) for every call. 

For HTTP client registrations, NResilience places a telemetry handler ahead of the resilience handler, so the activity covers every attempt. That gives a clear boundary showing when multiple sends belong to one logical call.

Attempts, retries, and circuit breaker transitions are recorded as span events. Each event is tagged with:
- The attempt number.
- The verdict.
- The delay.
- The exception type (if applicable).

The library uses `StartActivity`, which returns `null` when the tracing system is not sampling, so if no one records traces, the registered handler adds negligible overhead.

## Instrument manually created policies

A policy you create manually (in a static field, say) is not instrumented by default. Enable it with `WithTelemetry()`.

<!-- snippet: telemetry-with-telemetry -->
```csharp
// A policy registered in a container is instrumented for you. A policy in a static field
// is not - this says it.
var api = (Resilience.Http with { Name = "payments" }).WithTelemetry();
```
<!-- endsnippet -->

`WithTelemetry` chains the instrumentation after any existing `OnEvent` listener rather than replacing it. Calling it multiple times on the same policy applies the instrumentation only once, so nothing is double-counted.

## Logging

A registered policy also writes `ILogger` records, under a category of `NResilience.<policy>` so you can filter them per policy from `appsettings.json`. Nothing above `Trace` is written while your dependencies are healthy.

The records say what each event means rather than dumping raw event fields - the difference between a log that resolves an incident and one that adds to it.

See [Logging in DI](logging.md).
