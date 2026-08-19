---
title: Configuration
description: The bindable shape of a policy, one section per policy, and the callback for what JSON cannot hold.
order: 1
---

# Configuration

<!-- snippet: di-register-section -->
```csharp
services.AddResilience(configuration.GetSection("Resilience"));
```
<!-- endsnippet -->

Each child of the section becomes a policy named by its key:

<!-- snippet: appsettings.resilience.json -->
```json
{
  "Resilience": {
    "api": {
      "Preset": "Http",
      "Attempts": 3,
      "Deadline": "00:00:10",
      "AttemptTimeout": "00:00:03",
      "TransientBaseDelay": "00:00:00.200",
      "ThrottledBaseDelay": "00:00:02",
      "MaxDelay": "00:00:10",
      "Breaker": {
        "ConsecutiveFailures": 5,
        "BreakDuration": "00:00:15",
        "SlowCallThreshold": "00:00:02"
      }
    },
    "reports": {
      "Preset": "Http",
      "Attempts": 5,
      "Deadline": "00:05:00",
      "BudgetFraction": 0.2
    }
  }
}
```
<!-- endsnippet -->

Every property is nullable, and null means "say nothing" rather than "set the default": a section
mentioning only `Attempts` changes only the attempt count, whatever the base policy was.

## The bindable properties

| Property | Maps to |
| --- | --- |
| `Preset` | `"None"`, `"Default"` or `"Http"` - the starting point, case-insensitive |
| `Name` | `Resilience.Name`. Defaults to the registration name |
| `Attempts` | `Resilience.Attempts` - the total, including the first |
| `Deadline`, `AttemptTimeout` | The two bounds. `"-00:00:00.0010000"` is `Timeout.InfiniteTimeSpan` |
| `TransientBaseDelay`, `ThrottledBaseDelay`, `MaxDelay`, `BackoffFactor`, `Jitter` | The backoff curve |
| `BudgetFraction`, `BudgetMinimumPerSecond`, `SharedBudget` | The retry budget. `0` turns it off |
| `Breaker` | A `BreakerOptions` section, or absent for no breaking |
| `Telemetry` | `false` opts this policy out of the meter |

`BreakerOptions` mirrors [`BreakerSettings`](../reference/breaker.md): `ConsecutiveFailures`,
`FailureRatio`, `MinimumCalls`, `Window`, `BreakDuration`, `MaxBreakDuration`, `HalfOpenProbes`,
`ProbeSuccesses`, `SlowCallThreshold`, `SlowCallRatio`.

## What JSON cannot hold

A classifier is a lambda, and so are `BeforeAttempt` and `OnEvent`. A breaker you mean to share with
something else is a live object. All of it goes in the `configure` callback, which runs **last** -
after the section and after the live objects are re-attached, so it always wins.

<!-- snippet: di-configure-callback -->
```csharp
// Runs last, after the section and after the live objects are re-attached. A classifier is
// a lambda and JSON cannot hold one, so this is where one goes - along with a hook, or a
// breaker you mean to share with something else.
services.AddResilience(
    "api",
    configuration.GetSection("Resilience:api"),
    policy => policy with
    {
        Classify = Classifier.Http.On<MyTransportException>(Verdict.Transient),
        Breaker = shared,
    });
```
<!-- endsnippet -->

## Why the binding target is a DTO

> [!NOTE]
> Binding a section straight onto `Resilience` looks like it works, and is **silently partial**.
> `Attempts` and `Deadline` bind; `Backoff:Max` is dropped because the cap is a computed property;
> `Classify: "Http"` is ignored, leaving a policy that does not retry a 503; and
> `Breaker:ConsecutiveFailures` constructs a live circuit breaker with default settings, ignoring the
> value you set.

The middle case is the dangerous one, because the half that worked is the evidence people use to
conclude the other half did too. `ResilienceOptions` is a flat, mutable DTO and `ToPolicy` does the
projection by hand, so what a section says is what the policy gets. All three failures are gated by a
test.

Go deeper: [`ResilienceOptions` reference](../reference/options.md).

