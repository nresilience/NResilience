---
title: Telemetry in DI
description: What a registered policy records, how to see it, and how to turn it off.
order: 2
---

# Telemetry in DI

A policy registered in a container records to the library's meter **by default**. This is the one
place NResilience is not free-when-unused, and it is deliberate: `Resilience.Http with { … }` in a
static field costs nothing, but a registered policy is part of an application that runs in
production - and a resilience library whose metrics are off unless you find the switch is a library
whose metrics are off.

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

Metric names, tag names and event names share nothing with Polly's `resilience.polly.*` vocabulary,
so a process running both is legible.

## Spans

An HTTP client registration adds a telemetry handler **ahead** of the resilience handler, so its
activity covers every attempt. That is the boundary a per-attempt HTTP span cannot show you: "these
three sends were one call that eventually succeeded".

Attempts, retries and breaker transitions are added to the current activity as span events, tagged
with the attempt number, the verdict, the delay and the exception type. `StartActivity` returns null
when nobody is sampling, which is what makes an always-registered handler affordable.

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

