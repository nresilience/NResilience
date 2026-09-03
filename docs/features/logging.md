---
title: Logging
description: Read what a policy did through ILogger, and tune the level per record.
order: 10
---

# Logging

A policy writes log records through `ILogger`, each saying what an event means rather than dumping raw event fields. Logging is **on by default** for policies registered in a container and **opt-in** for policies you build yourself.

A healthy process writes nothing above the `Trace` level. A `Warning` indicates a circuit breaker opened, a retry budget was exhausted, a callback outlived its timeout, a nested retry was detected, or an exception type was not retried for the first time.

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

## The two knobs

| Knob | Decides | Who manages it |
| :--- | :--- | :--- |
| **Profile** | The level at which each record is emitted. | The library (usually). Three values. |
| **Category filter** | Which records are kept. | The user, via `appsettings.json` (no redeploy required). |

These knobs are independent, and the category filter is a platform feature rather than a library one: one line in `appsettings.json` can turn retry logs on in production or silence a noisy client.

## Profiles

| Profile | What it does |
| :--- | :--- |
| `Off` | Attaches no listener, so suppressed calls cost nothing. |
| `Default` | Uses the tabled levels. Healthy traffic is `Trace`, retried-then-successful calls are `Debug`, incidents are `Warning`. |
| `Verbose` | Raises every traffic-proportional record to `Information` and leaves incident records where they are. |

`Verbose` exists for sinks that enforce a minimum level. A platform that only ingests `Information` and above will never show `Debug` retry records, whatever the filter says.

A value that is not `Off`, `Default` or `Verbose` fails at registration with a message naming the valid values.

To retune a specific record in code, set the `Level` property. Return `null` to keep the profile's level, a specific level to override it, or `LogLevel.None` to drop the record.

<!-- snippet: logging-level -->
```csharp
// Event 1013 is "the circuit breaker opened". Everything else keeps the profile's level:
// return null to say nothing, or LogLevel.None to drop the record.
var payments = (Resilience.Http with { Name = "payments" }).WithLogging(
    logger: logger,
    options: new ResilienceLoggingOptions
    {
        Level = (id, _) => id.Id == 1013 ? LogLevel.Critical : null,
    });
```
<!-- endsnippet -->

## Instrument a policy you built yourself

Policies in static fields are not in a container, so they do not log by default.

<!-- snippet: logging-hand-built -->
```csharp
// A policy registered in a container logs for you. A policy in a static field does not -
// this says it, and the logger's category is what a filter matches.
var payments = (Resilience.Http with { Name = "payments" }).WithLogging(logger: logger);
```
<!-- endsnippet -->

For a console spike, a logger factory is one line:

<!-- snippet: logging-console -->
```csharp
using var factory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(level: LogLevel.Debug));

var payments = (Resilience.Http with { Name = "payments" })
    .WithLogging(logger: factory.CreateLogger(categoryName: ResilienceLogging.CategoryFor(policyName: "payments")));
```
<!-- endsnippet -->

`WithLogging` chains the listener after any existing `OnEvent` handler rather than replacing it. At most one log listener attaches per policy, and the first one attached wins.

## Flood control

The feature exists to handle pathological states; three noise types use three different mechanisms.

- **Rejections.** An open breaker refuses every call for the duration of the break. Events 1010 and 1011 warn at most once per `RepeatWindow` (30 seconds by default) per policy and reason. Within the window, rejections are counted and written as event 1012 at `Debug`, and the count is included in the `Suppressed` field of the next warning. No records are dropped, only demoted. Set `RepeatWindow` to `TimeSpan.Zero` to warn on every rejection.
- **Footguns.** `OrphanedWork` and `NestedRetry` are configuration errors. Each warns the first time it is detected for a policy and remains quiet thereafter.
- **Unretried exception types.** Event 1007 names an exception type the first time a policy declines to retry it. HTTP status codes are classified from responses and arrive without exceptions, so they follow the quiet path even for the ten thousandth 404.

## Sample the steady state

The most valuable records occur during an incident, but the cost is the steady-state volume. A profile is chosen at registration and cannot make this trade, so `Sampling` handles it per call instead: **opt-in**, off until you set it.

<!-- snippet: logging-sampling -->
```csharp
// One call in twenty is written while the policy is healthy, and every record for a minute
// after its breaker opens or starts refusing calls. The first 20 of each record are written
// in full whatever happens, so a development run logs exactly as it did before.
services.AddResilienceLogging(o => o.Sampling = LogSampling.OneIn(keepOneIn: 20));
```
<!-- endsnippet -->

`LogSampling.OneIn(20)` keeps one traffic record in twenty while the policy is healthy and every record for a minute after an incident. Three knobs, and the first is the only one most callers set.

| Property | Default | What it does |
| :--- | :--- | :--- |
| `KeepOneIn` | none - you supply it | One traffic record in this many is kept while the policy is healthy. `1` is no sampling. |
| `IncidentWindow` | 1 minute | How long after an incident every record is kept, measured from the most recent one. |
| `MinimumSamples` | 20 | How many of each record are written in full before sampling starts. |

**What is sampled** is the records whose volume is proportional to traffic: events 1000-1005 and the three hedge records, 1022-1024. Breaker transitions, first sightings, rejections, adapted estimates and policy resolution are never sampled - each is already one line per event rather than one line per call.

**What opens the window** is a breaker opening (1013) or a call refused by a breaker or by the retry budget (1010, 1011). Footguns and first-sighting exception types do not, because they recur for the life of the process and a window they hold open is sampling turned off without saying so. The window opens on the event rather than on the written record, so an incident whose warning your filter is not carrying still restores the detail.

The counting is exact rather than random: every twentieth record, counted per record and per policy. Two processes at the same traffic write the same number of lines, and an HTTP client with a policy per host samples each host on its own.

> [!IMPORTANT]
> Sampling drops records. Unlike the rejection repeat window it does not demote them, and no count of what it dropped reaches the log. When you need one call followed attempt by attempt - a reproduction, an integration test - set `KeepOneIn` to `1` for the run.

## Correlate interleaved records

A busy process interleaves `Debug` records from many concurrent calls of the same policy. The records carry no call identity, so use trace and span IDs to line them up.

<!-- snippet: logging-correlation -->
```csharp
// A busy process interleaves records from many concurrent calls of the same policy. The
// trace and span IDs are what line them back up, and for an HTTP client the telemetry
// handler already starts one span per logical operation.
services.AddLogging(b => b.Configure(o => o.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId));
```
<!-- endsnippet -->

For HTTP clients, this alone is enough because the telemetry handler starts one activity per logical operation. Every record from one retry sequence shares the same span ID, lining up with the `System.Net.Http.HttpClient` records for the same request.

## Assert on what a policy logged

Use `FakeLogger` from `Microsoft.Extensions.Diagnostics.Testing` to assert on what a policy logged. It is the standard ecosystem tool; nothing library-specific is needed.

<!-- snippet: logging-assert -->
```csharp
var logger = new FakeLogger();

var payments = (Resilience.Http with { Name = "payments", Backoff = Backoff.None })
    .WithLogging(logger: logger);

await payments.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

// 1005 is "succeeded on attempt N". Every ID is tabled in docs/reference/events.md.
Assert.Contains(expected: 1005, collection: logger.Collector.GetSnapshot().Select(record => record.Id.Id));
```
<!-- endsnippet -->

## Go deeper

- [Logging in DI](../di/logging.md): How a registered policy logs, and how to filter it per policy from `appsettings.json`.
- [Logging internals](../deep-dives/logging-internals.md): Why the levels are proportional to volume, and what the records deliberately do not carry.
- [Event IDs](../reference/events.md#log-event-ids): The full table, which is the contract an alert is built on.
