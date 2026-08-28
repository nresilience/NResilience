---
title: Reference
description: A comprehensive reference of the NResilience public API, organized by type family.
order: 7
---

# Reference

The **executor** is the internal loop that manages the execution of each attempt. It applies deadlines and attempt timeouts, handles backoff delays, and determines whether a call should be retried. To maintain simplicity and performance, the executor uses a flat execution pass rather than a strategy pipeline.

| Topic | Types and Members |
| :--- | :--- |
| [`Resilience`](resilience.md) | `Resilience`, execution methods, `NextAttempt`, `ResilienceDeadline`, `PolicyScope<TKey>` |
| [`CallResult<T>`](call-result.md) | `CallResult<T>`, `CallResult`, `StopReason`, `AttemptLog`, `Attempt` |
| [Classifier and verdicts](classifier.md) | `Classifier`, `Verdict`, `VerdictKind` |
| [`Backoff`](backoff.md) | `Backoff`, `BackoffKind`, `Jitter` |
| [`Breaker`](breaker.md) | `Breaker`, `BreakerSettings`, `BreakerState` |
| [`RetryBudget`](retry-budget.md) | `RetryBudget` |
| [`CallEvent`](events.md) | `CallEvent`, `CallEventKind` |
| [Exceptions](exceptions.md) | `CallRejectedException`, `DeadlineExceededException`, `AttemptTimeoutException`, `ResilienceConfigurationException` |
| [HTTP](http.md) | `ResilienceHandler`, `HttpResilienceOptions`, `ResilienceHttp` |
| [Options and registration](options.md) | `ResilienceOptions`, `BreakerOptions`, `IResiliencePolicies`, `ResilienceTelemetry`, `AddResilience`, `UseResilienceDeadline` |
| [Testing](testing.md) | `Sequence`, `Sequence<T>`, `EventRecorder`, `TestPolicy`, `ScriptedHttpHandler`, `SentRequest` |
| [Analyzers](analyzers.md) | Diagnostics `NRES001` through `NRES007` |
