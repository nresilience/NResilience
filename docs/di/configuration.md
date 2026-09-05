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
      "Backoff": {
        "TransientBase": "00:00:00.200",
        "ThrottledBase": "00:00:02",
        "Max": "00:00:10"
      },
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
      "Budget": { "Fraction": 0.2 }
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
| `Backoff` | A `BackoffOptions` section: `TransientBase`, `ThrottledBase`, `Max`, `Factor`, `Jitter`. |
| `Budget` | A `BudgetOptions` section: `Enabled`, `Fraction`, `MinimumPerSecond`, `Shared`. |
| `AttemptCeiling` | An `AttemptCeilingOptions` section. On by default; `"AttemptCeiling": { "Enabled": false }` leaves `AttemptTimeout` as the only per-attempt bound. |
| `Backoff:MeasuredBase` | A `MeasuredBaseOptions` subsection. Off by default; `"Backoff": { "MeasuredBase": { "Multiple": 1 } }` measures the transient base from recent latency. |
| `Breaker` | A `BreakerOptions` section. Omit it, or write `"Enabled": false`, for no circuit breaker. |
| `Hedge` | A `HedgeOptions` section. Omit it, or write `"Enabled": false`, for no hedging. |
| `Adaptive` | Set to `false` to turn off every measured term in the policy **and its breaker**. See [Turning measurement off](#turning-measurement-off). |
| `Telemetry` | Set to `false` to opt this policy out of the telemetry meter. |

## Every feature is a section, and every section has `Enabled`

`Backoff`, `Budget`, `AttemptCeiling`, `Breaker` and `Hedge` are objects whose keys are the property names
of the type each one configures, so there is no second spelling to learn. `"Enabled": false` turns a
feature off wherever it appears:

<!-- snippet: appsettings.resilience.production.json -->
```json
{
  "Resilience": {
    "api": {
      "Breaker": { "Enabled": false }
    }
  }
}
```
<!-- endsnippet -->

That is the only way to remove a feature a base file turned on, because configuration providers
merge sections and never delete a key. It replaces per-feature magic numbers; values like `"Multiple": 0`, `"Fraction": 0` and `"BudgetFraction": 0`
fail at registration with a message naming `"Enabled": false`.

`Backoff` has no `Enabled`, because a policy always has a backoff curve.

## Turning measurement off

Several bounds are measured from the dependency rather than guessed: the attempt ceiling, and the
breaker's two relative trips. `"Adaptive": false` turns all of them off at once:

<!-- snippet: appsettings.resilience.deterministic.json -->
```json
{
  "Resilience": {
    "api": {
      "Adaptive": false,
      "AttemptTimeout": "00:00:03",
      "Breaker": { "ConsecutiveFailures": 5 }
    }
  }
}
```
<!-- endsnippet -->

What is left is the constants you wrote: `AttemptTimeout` bounds each attempt, and the breaker opens
on `ConsecutiveFailures` and on `FailureRatio` or `SlowCallThreshold` if you named them.

Unlike the `Resilience.Adaptive` property it sets, the key reaches the breaker too. A section builds
a breaker for this policy alone rather than sharing one, so there is no second holder for the switch
to surprise. `"Breaker": { "Adaptive": true }` overrides it for the breaker only.

`Adaptive` suppresses defaults; it does not overrule what you wrote. A section that says `false` and
then configures `AttemptCeiling`, `Hedge`, `Breaker:SlowCalls`, `Breaker:Failures` or
`Backoff:MeasuredBase` has said two incompatible things, and registration fails naming both.
 
The `Breaker` section mirrors [`BreakerSettings`](../reference/breaker.md) and supports `ConsecutiveFailures`, `FailureRatio`, `MinimumCalls`, `TripWindow`, `BreakDuration`, `MaxBreakDuration`, `BreakJitter`, `HalfOpenProbes`, `ProbeSuccesses`, `SlowCallThreshold`, and `SlowCallRatio`. `Recovery` is a subsection of its own - `"Recovery": {}` turns the [recovery ramp](../features/circuit-breaker.md#hand-the-traffic-back-over-a-ramp) on at its defaults, `"Recovery": { "Length": 0.5 }` changes it, and `"Enabled": false` turns it back off. `BreakJitter` binds by name - `"Equal"` (the default), `"Full"`, or `"None"` for a break that expires at exactly `BreakDuration`.

`Breaker:SlowCalls` is a nested section rather than a flat property, and the [adaptive brownout trip](../features/circuit-breaker.md#trip-on-brownouts-without-guessing-a-number) it configures is on by default. Every setting has a default, so the section is only needed to change one; it accepts `Multiple`, `Quantile`, `Window`, and `MinimumSamples`. `"SlowCalls": { "Enabled": false }` turns the trip off. Naming `SlowCallThreshold` as well composes rather than colliding - a call is slow when it is above either threshold.

`Breaker:Failures` is a nested section on the same pattern, and the [relative failure trip](../features/circuit-breaker.md#trip-on-errors-without-guessing-a-rate) it configures is on by default too. Every setting has a default, so the section is only needed to change one; it accepts `Multiple`, `Window`, `MinimumSamples`, and `Floor`, with `"Failures": { "Enabled": false }` turning the trip off. Set `FailureRatio` as well when you have a rate you never want exceeded - it becomes the ceiling, and the relative trip can only fire sooner.

`AttemptCeiling` is likewise a nested section, configuring the [measured attempt ceiling](../features/deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it) the default policy already carries. It accepts `Multiple`, `Quantile`, `Window`, `MinimumSamples`, and `Floor`, with `"AttemptCeiling": { "Enabled": false }` turning the measured term off. It never lengthens `AttemptTimeout` - the measured term can only lower the ceiling - so the two settings compose rather than compete.

`Backoff:MeasuredBase` is a subsection of the backoff section, configuring the [measured backoff base](../features/retry.md#measure-the-backoff-base-instead-of-guessing-it). Unlike every other measured term it is off unless you ask for it, because it is the one that can lengthen a delay rather than only shorten one. It accepts `Multiple`, `Quantile`, `Window`, `MinimumSamples`, and `Spread`, with `"MeasuredBase": { "Enabled": false }` dropping one a lower configuration layer added.

## Projection via ResilienceOptions

NResilience binds configuration onto `ResilienceOptions`, a flat, mutable Data Transfer Object (DTO), and the `ToPolicy` method projects that DTO into a `Resilience` policy.

> [!NOTE]
> Avoid binding a configuration section directly onto a `Resilience` instance. Direct binding is silently partial: scalars and `init` properties bind correctly, but complex objects do not. `Classify: "Http"` is ignored, leaving a policy that does not retry a 503 - a classifier is a set of predicates and no binder can build one from a string. `Breaker:ConsecutiveFailures` is worse: it constructs a live circuit breaker with default settings, ignoring the value you set. Neither is something a setter could fix, which is why the binding target is `ResilienceOptions`.

The middle case is the dangerous one, because the half that worked is the evidence people use to conclude the other half did too. The DTO ensures the final policy matches the section exactly. All three failures are gated by a test.

### An unrecognized key is an error

A section is bound with `ErrorOnUnknownConfiguration`, so a key that `ResilienceOptions` does not have
fails during policy resolution, naming the key:

```
The configuration section for policy "api" could not be bound. 'ErrorOnUnknownConfiguration' was set
on the provided BinderOptions instance, but the following properties were not found on the instance
of ResilienceOptions: 'Timeouts'. Check the spelling, and check whether the key was renamed - see
"Migrating an existing file" in the configuration documentation.
```

This follows the same principle as the preceding note. A misspelled or renamed key binds
nothing, and a policy that quietly kept its defaults reads exactly like a policy nobody configured -
so the one thing a configuration file must never do is look applied when it is not. The check runs at
every nesting depth, and under Native AOT.

`RateLimitOptions` is bound the same way, at registration rather than on resolution, because a
limiter is built eagerly.

### Backoff settings patch the base policy's curve

A section that sets some of the backoff knobs patches the curve the base policy already carries: any
knob the section does not mention keeps the base policy's value rather than falling back to a factory
default. So a base policy built with `Backoff.Exponential(transientBase: TimeSpan.FromMilliseconds(500))`
plus a section setting only `"Backoff": { "Max": "00:00:05" }` keeps the 500 ms transient base and gets the 5 s cap.

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
