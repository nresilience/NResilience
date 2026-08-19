---
title: Options and registration
description: ResilienceOptions, BreakerOptions, IResiliencePolicies, ResilienceTelemetry and the AddResilience overloads.
order: 10
---

# Options and registration

Package `NResilience.Extensions`. The registration methods live in
`Microsoft.Extensions.DependencyInjection`, which is where people look for one.

## `AddResilience` on `IServiceCollection`

| Overload | What it does |
| --- | --- |
| `AddResilience(name, Resilience policy, configure = null)` | Registers a policy value. Validates eagerly. |
| `AddResilience(name, Action<ResilienceOptions> configureOptions, configure = null)` | Registers one configured in code. |
| `AddResilience(name, IConfiguration section, configure = null)` | Registers one bound to a section. Reloads. |
| `AddResilience(IConfiguration section)` | Registers every child of the section as a policy named by its key. |
| `AddResilience()` | Registers `IResiliencePolicies` and no policies. |

`configure` is a `Func<Resilience, Resilience>` that runs **last**, after the section and after live
objects are re-attached.

## `AddResilience` on `IHttpClientBuilder`

| Overload | What it does |
| --- | --- |
| `AddResilience(Resilience? policy = null, Action<HttpResilienceOptions>? configureOptions = null, bool telemetry = true)` | Adds the handler with a policy value, defaulting to `Resilience.Http`. |
| `AddResilience(string policyName, Action<HttpResilienceOptions>? configureOptions = null, bool telemetry = true)` | Adds the handler with a registered policy, resolved when the handler chain is built. |

The policy is named after the client unless it carries a name of its own; a preset's name does not
count, so four clients on `Resilience.Http` do not all report as `http`.

## `IResiliencePolicies`

| Member | Meaning |
| --- | --- |
| `this[string name]` | The current policy for that name. Throws `ResilienceConfigurationException` listing what is registered. |
| `Names` | Every registered name. |
| `TryGet(name, out policy)` | The non-throwing form. `policy` is `Resilience.Default` when there is none. |

Resolve per call. A policy captured at construction is a snapshot no reload will reach.

## `ResilienceOptions`

`sealed class`. Flat, mutable, and every property nullable, where null means "say nothing".

`Preset`, `Name`, `Attempts`, `Deadline`, `AttemptTimeout`, `TransientBaseDelay`,
`ThrottledBaseDelay`, `MaxDelay`, `BackoffFactor`, `Jitter`, `BudgetFraction`,
`BudgetMinimumPerSecond`, `SharedBudget`, `Breaker`, `Telemetry`.

`ToPolicy(Resilience? baseline = null)` projects onto a policy: the preset first when set, then every
property that is not null. It does not validate - the caller does, so a bad section fails at
registration.

`BudgetFraction = 0` is the off switch, because "retries may add at most 0%" is not a budget anyone
can spend from.

See [Configuration](../di/configuration.md) for the section shape and for why the binding target is a
DTO rather than the record.

## `BreakerOptions`

The bindable shape of [`BreakerSettings`](breaker.md), with the same property names, all nullable.
`ToBreaker(string? name = null)` builds the live breaker. A configured breaker is created once per
policy and survives configuration reloads, because its state is the point.

## `ResilienceTelemetry`

`static class`.

| Member | Meaning |
| --- | --- |
| `MeterName` | `"NResilience"`. |
| `ActivitySourceName` | `"NResilience"`. |
| `Meter` | The meter every instrument is created on. |
| `ActivitySource` | The source the HTTP registration gives a logical operation a span from. |
| `Listener` | The `Action<CallEvent>` that records to the instruments. Stateless and allocation-free. |
| `WithTelemetry(this Resilience policy)` | The policy with `Listener` chained after whatever `OnEvent` held. Idempotent. |

The instruments are listed under [telemetry](../features/telemetry.md).

