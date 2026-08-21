---
title: Logging in DI
description: Read what a policy did through ILogger, and filter it per policy from appsettings.json.
order: 3
---

# Logging in DI

A policy registered in a DI container writes log records through `ILogger`. No calls or lambdas are required; the library understands each event and emits the corresponding record.

A healthy process writes nothing above the `Trace` level. A `Warning` indicates a circuit breaker opened, a retry budget was exhausted, a callback outlived its timeout, a nested retry was detected, or an exception type was not retried for the first time.

<!-- snippet: logging-registered -->
```csharp
services.AddLogging();
services.AddResilience("payments", Resilience.Http);

// Nothing else to call. The policy logs under "NResilience.payments", which is the category
// an appsettings.json filter matches.
```
<!-- endsnippet -->

A policy you build yourself is not logged until you say so. See [Instrument a policy you built yourself](#instrument-a-policy-you-built-yourself).

## The two knobs

| Knob | Decides | Who manages it |
| :--- | :--- | :--- |
| **Profile** | The level at which each record is emitted. | The library (usually). Three values. |
| **Category filter** | Which records are kept. | The user, via `appsettings.json` (no redeploy required). |

These knobs are orthogonal, and the category filter is a platform feature rather than a library one. For example, adding one line to `appsettings.json` can enable retry logs in production or silence a noisy client.

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

The category is fixed when the listener attaches. Because an HTTP client derives a policy per host, the category does not include the host; otherwise, a client talking to fifty hosts would create fifty categories. The host-scoped name is still included in the `Policy` field of every record for structured output queries.

Use `ResilienceLogging.CategoryFor(name)` to get the category in code.

## Profiles

| Profile | What it does |
| :--- | :--- |
| `Off` | Attaches no listener, eliminating the cost of suppressed calls. |
| `Default` | Uses the tabled levels. Healthy traffic is `Trace`, retried-then-successful calls are `Debug`, and incidents are `Warning`. |
| `Verbose` | Raises every traffic-proportional record to `Information` and leaves the incident records where they are. |

`Verbose` allows you to raise records above a threshold enforced by a logging sink. For example, a platform that only ingests `Information` and above will never show `Debug` retry records, regardless of the filter settings.

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

A value that is not `Off`, `Default` or `Verbose` fails at registration with a message naming the valid ones.

To retune a specific record in code, set the `Level` property. Return `null` to keep the profile's level, a specific level to override it, or `LogLevel.None` to drop the record.

<!-- snippet: logging-level -->
```csharp
// Event 1013 is "the circuit breaker opened". Everything else keeps the profile's level:
// return null to say nothing, or LogLevel.None to drop the record.
var payments = (Resilience.Http with { Name = "payments" }).WithLogging(
    logger,
    new ResilienceLoggingOptions
    {
        Level = (id, _) => id.Id == 1013 ? LogLevel.Critical : null,
    });
```
<!-- endsnippet -->

## What the levels mean

A record's level is proportional to its volume.

| Volume scales with | Level | Reason |
| :--- | :--- | :--- |
| Traffic (per call, per attempt) | `Trace` or `Debug` | Metrics already count these; one line per call would duplicate `nresilience.calls` at significant cost. |
| Incidents (state transitions, first sightings) | `Warning` or `Information` (for recovery) | One line per incident is readable. |
| Caller-visible failures | `Debug` | The exception reaches you, and you log it with the business context the library does not have. |
| Policy resolution | `Debug` | One line per policy per reload. Provenance, not traffic. |

Failed calls are recorded at `Debug` because they end by throwing an exception to the caller. The library records internal details—such as the attempt number, backoff, and verdict—rather than duplicating your own error logs.

## What it looks like

A retried call, with the filter at `Debug`:

```
dbug: NResilience.payments[1001] payments:api.example.com attempt 1 failed in 812 ms: Transient HttpRequestException
dbug: NResilience.payments[1003] payments:api.example.com waiting 217 ms before attempt 2 after a Transient outcome
dbug: NResilience.payments[1005] payments:api.example.com succeeded on attempt 2 after 1104 ms
```

A dependency going down and coming back, at the default filter. Four lines for an incident that refused fifteen hundred calls:

```
warn: NResilience.payments[1013] payments:api.example.com opened its circuit breaker on attempt 3. Calls are refused until the break duration elapses.
warn: NResilience.payments[1010] payments:api.example.com refused a call because its circuit breaker is open. Rejections logged quietly since the previous warning: 0.
warn: NResilience.payments[1010] payments:api.example.com refused a call because its circuit breaker is open. Rejections logged quietly since the previous warning: 1483.
info: NResilience.payments[1015] payments:api.example.com closed its circuit breaker and is taking traffic again
```

Startup, with the filter at `Debug`:

```
dbug: NResilience.payments[1020] payments resolved: 4 attempts, deadline 20s, attempt timeout 3s, backoff max 1s, jitter Full, breaker 2 consecutive failures / 15s break, own budget, telemetry on, logging Default
```

## Flood control

The feature exists to handle pathological states; three noise types use three different mechanisms.

- **Rejections.** An open breaker refuses every call for the duration of the break. Events 1010 and 1011 warn at most once per `RepeatWindow` (30 seconds by default) per policy and reason. Within the window, rejections are counted and written as event 1012 at `Debug`, and the count is included in the `Suppressed` field of the next warning. No records are dropped, only demoted. Set `RepeatWindow` to `TimeSpan.Zero` to warn on every rejection.
- **Footguns.** `OrphanedWork` and `NestedRetry` are configuration errors. Each warns the first time it is detected for a policy and remains quiet thereafter.
- **Unretried exception types.** Event 1007 names an exception type the first time a policy declines to retry it. HTTP status codes are classified from responses and arrive without exceptions, so they follow the quiet path even for the ten thousandth 404.

## Provenance

Binding a configuration section is silently partial. Event 1020 reports the effective policy once per resolution at `Debug`; a reload produces a new entry, showing exactly what changed.

Event 1021 is a `Trace` companion that dumps the classifier's state. This allows you to determine what the policy will retry without reading the source code. It is guarded to ensure it costs nothing when `Trace` is disabled.

## Instrument a policy you built yourself

Policies in static fields are not in a container, so they do not log by default.

<!-- snippet: logging-hand-built -->
```csharp
// A policy registered in a container logs for you. A policy in a static field does not -
// this says it, and the logger's category is what a filter matches.
var payments = (Resilience.Http with { Name = "payments" }).WithLogging(logger);
```
<!-- endsnippet -->

For a console spike, a logger factory is one line:

<!-- snippet: logging-console -->
```csharp
using ILoggerFactory factory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Debug));

var payments = (Resilience.Http with { Name = "payments" })
    .WithLogging(factory.CreateLogger(ResilienceLogging.CategoryFor("payments")));
```
<!-- endsnippet -->

`WithLogging` chains the listener after any existing `OnEvent` handler rather than replacing it. At most one log listener attaches per policy, and the first listener attached takes precedence. An explicit `WithLogging` call in a `configure` callback overrides the automatic listener added by a container.

## Correlate interleaved records

A busy process interleaves `Debug` records from many concurrent calls of the same policy. Because these records carry no call identity, use trace and span IDs to correlate them.

<!-- snippet: logging-correlation -->
```csharp
// A busy process interleaves records from many concurrent calls of the same policy. The
// trace and span IDs are what line them back up, and for an HTTP client the telemetry
// handler already starts one span per logical operation.
services.AddLogging(b => b.Configure(o => o.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId));
```
<!-- endsnippet -->

For HTTP clients, this is sufficient because the telemetry handler starts one activity per logical operation. Every record from one retry sequence shares the same span ID, aligning it with the `System.Net.Http.HttpClient` records for the same request.

## What the records do not carry

Gaps in the records are intentional to avoid duplication; correlation is more efficient.

| Not in the records | Where it lives |
| :--- | :--- |
| The request URI, method and status code | `Microsoft.Extensions.Http`'s own `System.Net.Http.HttpClient.<name>.LogicalHandler` category. |
| The breaker's state and break duration | `Breaker.State`, `Breaker.OpenedAt` and `Breaker.Settings`, which a health endpoint already reads. |
| The retry budget's utilization | `RetryBudget.Utilisation`, which is the documented dashboard number. |
| The full attempt history | `AttemptLog.Of(exception)` on the thrown exception. |
| Anything for a call the caller cancelled | Nothing. Caller cancellation rethrows before any event is raised, so a cancelled call is silent by construction. |

Exception objects attach to terminal records, allowing providers to render stack traces. Per-attempt and retry records include the exception type in the message and only attach the object when `IncludeStackTracesOnRetry` is enabled. This prevents a three-attempt call from writing three stack traces for a single failure.

## Assert on what a policy logged

Use `FakeLogger` from `Microsoft.Extensions.Diagnostics.Testing` to assert on what a policy logged. This is the standard ecosystem approach and requires no library-specific tools.

<!-- snippet: logging-assert -->
```csharp
var logger = new FakeLogger();
var payments = (Resilience.Http with { Name = "payments", Backoff = Backoff.None })
    .WithLogging(logger);

await payments.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken);

// 1005 is "succeeded on attempt N". Every ID is tabled in docs/reference/events.md.
Assert.Contains(1005, logger.Collector.GetSnapshot().Select(record => record.Id.Id));
```
<!-- endsnippet -->

## Next steps

- [Event IDs](../reference/events.md#log-event-ids): The full table, which is the contract an alert is built on.
- [Telemetry](telemetry.md): The metrics and traces the records deliberately do not duplicate.
- [Options reference](../reference/options.md): `Logging` and the rest of the bindable shape.
