---
title: Reference
description: Every public member, in a stable order, one page per type family.
order: 7
---

# Reference

| Page | Types |
| --- | --- |
| [`Resilience`](resilience.md) | `Resilience`, the execution methods, `NextAttempt` |
| [`CallResult<T>`](call-result.md) | `CallResult<T>`, `CallResult`, `StopReason`, `AttemptLog`, `Attempt` |
| [Classifier and verdicts](classifier.md) | `Classifier`, `Verdict`, `VerdictKind` |
| [`Backoff`](backoff.md) | `Backoff`, `Jitter` |
| [`Breaker`](breaker.md) | `Breaker`, `BreakerSettings`, `BreakerState` |
| [`RetryBudget`](retry-budget.md) | `RetryBudget` |
| [`CallEvent`](events.md) | `CallEvent`, `CallEventKind` |
| [Exceptions](exceptions.md) | `CallRejectedException`, `DeadlineExceededException`, `AttemptTimeoutException`, `ResilienceConfigurationException` |
| [HTTP](http.md) | `ResilienceHandler`, `HttpResilienceOptions`, `ResilienceHttp` |
| [Options and registration](options.md) | `ResilienceOptions`, `BreakerOptions`, `IResiliencePolicies`, `ResilienceTelemetry`, `AddResilience` |
| [Testing](testing.md) | `Sequence`, `Sequence<T>`, `EventRecorder` |
| [Analyzers](analyzers.md) | NRES001-NRES007, the diagnostics that ship in the package |

The public surface is small on purpose, and it is a checked-in manifest: surface growth is a reviewed
diff rather than an accident.

