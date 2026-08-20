---
title: Telemetry in DI
description: What a registered policy records, how to see it, and how to turn it off.
order: 2
---

# Telemetry in DI

A policy you build by hand and hold in a `static readonly` field costs nothing when it is not
running - no listener attached, no allocations, no overhead. A policy registered in a container is
different: it is part of an application that runs in production, and a resilience library whose
metrics are off unless you find the switch is a library whose metrics are off. So a registered
policy records to the library's **meter** (a named set of instruments that a metrics collector can
read) **by default**.

The switches are `ResilienceOptions.Telemetry = false` in configuration and `telemetry: false` on the
HTTP registrations.

## Collecting it

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(ResilienceTelemetry.MeterName))
    .WithTracing(t => t.AddSource(ResilienceTelemetry.ActivitySourceName));
```

Both names are `"NResilience"`. The instruments themselves are listed under
[telemetry](../features/telemetry.md); the number to watch is
`nresilience.attempts ÷ nresilience.calls`.

## Spans

A **span** (a unit of distributed tracing that covers one operation) is added per call: an HTTP
client registration puts a telemetry handler **ahead** of the resilience handler, so its activity
covers every attempt. That is the boundary a per-attempt HTTP span cannot show you: "these three
sends were one call that eventually succeeded".

Attempts, retries and breaker transitions are added to the current activity as span events, tagged
with the attempt number, the verdict, the delay and the exception type. `StartActivity` returns null
when nobody is **sampling** (the tracing system decides which spans to record - when nobody is,
there is nothing to record and the call is free), which is what makes an always-registered handler
affordable.

## Instrumenting a policy you built by hand

<!-- snippet: telemetry-with-telemetry -->
```csharp
// A policy registered in a container is instrumented for you. A policy in a static field
// is not - this says it.
var api = (Resilience.Http with { Name = "payments" }).WithTelemetry();
```
<!-- endsnippet -->

`WithTelemetry` chains after whatever `OnEvent` already held rather than replacing it, and applying it
twice applies it once - so a defensive registration path cannot double-count.

## What is not here

No `ILogger` integration. `OnEvent` takes a lambda and a log line is one line of your code; a
built-in logger would invent an event-id vocabulary and message templates to maintain and defend, and
what it would save is a lambda. The [telemetry](../features/telemetry.md) page shows the line.

