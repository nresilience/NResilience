---
title: Configuration
description: Configure resilience policies using bindable settings, JSON sections, and custom configuration callbacks.
order: 1
---

# Configuration

NResilience supports binding policy settings directly from configuration providers, such as `appsettings.json`. This allows you to tune parameters like deadlines, attempt counts, and circuit breaker thresholds without redeploying your application.

While most settings are bindable, logic that requires code - such as custom classifiers or shared circuit breaker instances - is managed via configuration callbacks.

To bind policies from configuration, use the `AddResilience` method with a configuration section.

<!-- snippet: di-register-section -->
```csharp
services.AddResilience(section: configuration.GetSection(key: "Resilience"));
```
<!-- endsnippet -->

Every child of the specified section is treated as a policy, with the key serving as the policy name.

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

All properties are nullable. A `null` value means the property remains unchanged from the base policy rather than reverting to a default. For example, if a section only specifies `Attempts`, only the attempt count is modified.

## Bindable properties

| Property | Description |
| :--- | :--- |
| `Preset` | The starting point. Supports `"None"`, `"Default"`, or `"Http"` (case-insensitive). |
| `Name` | The policy name. Defaults to the registration name. |
| `Attempts` | The total number of attempts, including the first call. |
| `Deadline`, `AttemptTimeout` | Time bounds for the call. Use `"-00:00:00.0010000"` for `Timeout.InfiniteTimeSpan`. |
| `TransientBaseDelay`, `ThrottledBaseDelay`, `MaxDelay`, `BackoffFactor`, `Jitter` | Settings for the backoff curve. |
| `BudgetFraction`, `BudgetMinimumPerSecond`, `SharedBudget` | Settings for the retry budget. Use `0` to disable the budget. |
| `Breaker` | A `BreakerOptions` section. Omit this to disable the circuit breaker. |
| `Telemetry` | Set to `false` to opt this policy out of the telemetry meter. |

The `Breaker` section mirrors [`BreakerSettings`](../reference/breaker.md), supporting properties such as `ConsecutiveFailures`, `FailureRatio`, `MinimumCalls`, `Window`, `BreakDuration`, `MaxBreakDuration`, `HalfOpenProbes`, `ProbeSuccesses`, `SlowCallThreshold`, and `SlowCallRatio`.

## Projection via ResilienceOptions

NResilience uses `ResilienceOptions` as a flat, mutable Data Transfer Object (DTO) to handle configuration binding. The `ToPolicy` method then projects this DTO into a `Resilience` policy.

> [!NOTE]
> Avoid binding a configuration section directly onto a `Resilience` instance. Direct binding is silently partial: properties like `Attempts` bind correctly, but computed properties (such as backoff caps) or complex objects (such as classifiers and circuit breakers) are ignored or incorrectly initialized. For example, `Backoff:Max` is dropped because the cap is a computed property; `Classify: "Http"` is ignored, leaving a policy that does not retry a 503; and `Breaker:ConsecutiveFailures` constructs a live circuit breaker with default settings, ignoring the value you set.

The middle case is the dangerous one, because the half that worked is the evidence people use to conclude the other half did too. Using a DTO ensures that the final policy exactly matches the configuration provided in the section. All three failures are gated by a test.

## Use the configuration callback for complex logic

JSON cannot store lambdas or live objects. To configure classifiers, `BeforeAttempt` hooks, `OnEvent` listeners, or shared circuit breakers, use the configuration callback.

The callback runs last - after the configuration section is applied and live objects are re-attached - ensuring the callback's settings always take precedence.

<!-- snippet: di-configure-callback -->
```csharp
// Runs last, after the section and after the live objects are re-attached. A classifier is
// a lambda and JSON cannot hold one, so this is where one goes - along with a hook, or a
// breaker you mean to share with something else.
services.AddResilience(
    name: "api",
    section: configuration.GetSection(key: "Resilience:api"),
    policy => policy with
    {
        Classify = Classifier.Http.On<MyTransportException>(verdict: Verdict.Transient),
        Breaker = shared,
    });
```
<!-- endsnippet -->

For more information, see the [`ResilienceOptions` reference](../reference/options.md).
