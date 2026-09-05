---
title: Reference
description: A comprehensive reference of the NResilience public API, organized by type family.
order: 8
---

# Reference

The **executor** is the internal loop that runs each attempt: it applies deadlines and attempt timeouts, serves backoff delays, and decides whether a call should be retried. For simplicity and performance, it uses one flat execution pass rather than a strategy pipeline.

| Topic | Types and Members |
| :--- | :--- |
| [`Resilience`](resilience.md) | `Resilience`, execution methods, `MeasuredValues`, `NextAttempt`, `AmbientDeadline`, `NestedRetry`, `PolicyScope<TKey>` |
| [`CallResult<T>`](call-result.md) | `CallResult<T>`, `CallResult`, `StopReason`, `AttemptLog`, `Attempt` |
| [Classifier and verdicts](classifier.md) | `Classifier`, `Verdict`, `VerdictKind` |
| [`Backoff`](backoff.md) | `Backoff`, `BackoffKind`, `Jitter` |
| [`Breaker`](breaker.md) | `Breaker`, `BreakerSettings`, `BreakerState` |
| [`RetryBudget`](retry-budget.md) | `RetryBudget` |
| [`CallEvent`](events.md) | `CallEvent`, `CallEventKind` |
| [Exceptions](exceptions.md) | `CallRejectedException`, `DeadlineExceededException`, `AttemptTimeoutException`, `ResilienceConfigurationException` |
| [HTTP](http.md) | `HttpResilienceHandler`, `HttpResilienceOptions`, `HttpResilience`, `HttpRequestExtensions` |
| [Options and registration](options.md) | `ResilienceOptions`, `BreakerOptions`, `IResiliencePolicies`, `ResilienceTelemetry`, `AddResilience`, `UseResilienceDeadline`, `UseResilienceNestedRetry`, `AddResilienceExceptionHandler` |
| [Testing](testing.md) | `Sequence`, `Sequence<T>`, `EventRecorder`, `TestPolicy`, `ScriptedHttpHandler`, `SentRequest` |
| [Analyzers](analyzers.md) | Diagnostics `NRES001` through `NRES007` |
