---
title: Configuration
description: Configure resilience policies using bindable settings, JSON sections, and custom configuration callbacks.
order: 1
---

# Configuration

NResilience binds policy settings directly from configuration providers such as `appsettings.json`, so you can tune deadlines, attempt counts, and breaker thresholds without redeploying.

Most settings are bindable. Logic that requires code - custom classifiers, shared breaker instances - goes through configuration callbacks instead.

Bind policies from a configuration section with `AddResilience`:

<!-- snippet: di-register-section -->
```csharp
services.AddResilience(section: configuration.GetSection(key: "Resilience"));
```
<!-- endsnippet -->

Every child of the section becomes a policy, named by its key.

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
        "SlowCalls": { "Multiple": 3 }
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

All properties are nullable. A `null` leaves the property as it is on the base policy rather than reverting to a default: a section that only specifies `Attempts` changes only the attempt count.

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
| `Timeouts` | An `AttemptTimeoutsOptions` section. On by default; `"Timeouts": { "Multiple": 0 }` leaves `AttemptTimeout` as the only per-attempt bound. |
| `Telemetry` | Set to `false` to opt this policy out of the telemetry meter. |

The `Breaker` section mirrors [`BreakerSettings`](../reference/breaker.md) and supports `ConsecutiveFailures`, `FailureRatio`, `MinimumCalls`, `Window`, `BreakDuration`, `MaxBreakDuration`, `BreakJitter`, `HalfOpenProbes`, `ProbeSuccesses`, `SlowCallThreshold`, and `SlowCallRatio`. `Recovery` is a subsection of its own - `"Recovery": {}` turns the [recovery ramp](../features/circuit-breaker.md#hand-the-traffic-back-over-a-ramp) on at its defaults, `"Recovery": { "Fraction": 0.5 }` changes it, and `"Fraction": 0` turns it back off. `BreakJitter` binds by name - `"Equal"` (the default), `"Full"`, or `"None"` for a break that expires at exactly `BreakDuration`.

`Breaker:SlowCalls` is a nested section rather than a flat property, and the [adaptive brownout trip](../features/circuit-breaker.md#trip-on-brownouts-without-guessing-a-number) it configures is on by default. Every setting has a default, so the section is only needed to change one; it accepts `Multiple`, `Quantile`, `Window`, and `MinimumSamples`. `"SlowCalls": { "Multiple": 0 }` turns the trip off, and so does naming `SlowCallThreshold` instead - they are the same trip defined two ways, and a section that sets both is rejected when the breaker is built.

`Breaker:Failures` is a nested section on the same pattern, and the [relative failure trip](../features/circuit-breaker.md#trip-on-errors-without-guessing-a-rate) it configures is on by default too. Every setting has a default, so the section is only needed to change one; it accepts `Multiple`, `Window`, `MinimumSamples`, and `AbsoluteFloor`, with `"Failures": { "Multiple": 0 }` turning the trip off. Set `FailureRatio` as well when you have a rate you never want exceeded - it becomes the ceiling, and the relative trip can only fire sooner.

`Timeouts` is likewise a nested section, configuring the [measured attempt ceiling](../features/deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it) the default policy already carries. It accepts `Multiple`, `Quantile`, `Window`, `MinimumSamples`, and `Floor`, with `"Timeouts": { "Multiple": 0 }` turning the measured term off. It never lengthens `AttemptTimeout` - the measured term can only lower the ceiling - so the two settings compose rather than compete.

## Projection via ResilienceOptions

NResilience binds configuration onto `ResilienceOptions`, a flat, mutable Data Transfer Object (DTO), and the `ToPolicy` method projects that DTO into a `Resilience` policy.

> [!NOTE]
> Avoid binding a configuration section directly onto a `Resilience` instance. Direct binding is silently partial: properties like `Attempts` bind correctly, but computed properties (such as backoff caps) or complex objects (such as classifiers and circuit breakers) are ignored or incorrectly initialized. For example, `Backoff:Max` is dropped because the cap is a computed property; `Classify: "Http"` is ignored, leaving a policy that does not retry a 503; and `Breaker:ConsecutiveFailures` constructs a live circuit breaker with default settings, ignoring the value you set.

The middle case is the dangerous one, because the half that worked is the evidence people use to conclude the other half did too. The DTO ensures the final policy matches the section exactly. All three failures are gated by a test.

### Backoff settings patch the base policy's curve

A section that sets some of the backoff knobs patches the curve the base policy already carries: any
knob the section does not mention keeps the base policy's value rather than falling back to a factory
default. So a base policy built with `Backoff.Exponential(transientBase: TimeSpan.FromMilliseconds(500))`
plus a section setting only `"MaxDelay": "00:00:05"` keeps the 500 ms transient base and gets the 5 s cap.

Patching only makes sense against an exponential curve. If the base policy carries a
`Backoff.Constant(...)` or a `Backoff.Custom(...)`, any backoff knob in the section replaces it with a
fresh exponential built on the shipped defaults - a `Custom` delegate cannot be patched, and a
`Constant` curve has no factor to preserve. To keep a constant or custom curve, leave the backoff
knobs out of the section and set it in the configuration callback instead. `Jitter` on its own is a
modifier rather than a backoff knob, so it does not trigger this and leaves the curve alone.

## Use the configuration callback for complex logic

JSON cannot hold lambdas or live objects. For classifiers, `BeforeAttempt` hooks, `OnEvent` listeners, or shared circuit breakers, use the configuration callback.

The callback runs last - after the section is applied and live objects are re-attached - so its settings always win.

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
