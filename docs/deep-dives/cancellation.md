---
title: The cancellation contract
description: Learn how NResilience manages different cancellation sources and the consequences of ignoring cancellation tokens.
order: 3
---

# The cancellation contract

A resilience attempt can end prematurely for three distinct reasons. Conflating these reasons is a common source of bugs. To ensure clarity, the executor manages the sources of cancellation directly and avoids using predicates to determine the cause.

| Scenario | Decision Maker | Outcome / Verdict |
| :--- | :--- | :--- |
| The caller cancels the provided token | External | Rethrown as `OperationCanceledException` |
| The attempt exceeds its time ceiling | Executor | Classified as `Transient` via `AttemptTimeoutException` |
| The callback throws an exception | Classifier | Determined by the configured `Classifier` |

### Caller cancellation behavior
The executor checks for caller cancellation at the start of the operation, after every attempt returns, and after every backoff delay. This ensures that if a token is cancelled during a backoff period, the operation aborts immediately instead of starting another attempt.

Caller cancellation is never retried, counted against a circuit breaker or retry budget, or converted into a timeout. No classifier can override this behavior. Even when using `TryRunAsync`, caller cancellation is thrown as an exception.

**Asymmetry in success**: If a caller cancels while an attempt is already succeeding, the executor returns the successful value. Since the caller has already waited for the attempt to complete, discarding the result provides no benefit. The post-attempt check exists primarily to prevent the loop from starting a subsequent attempt.

## The token the callback receives

When an attempt ceiling is defined, the callback receives a token linked from two sources: a pooled timer source and the caller's token. 

The executor does not hand out the pooled source's own token directly. This is because `TryReset` preserves token identity; if a callback outlived its attempt, it would observe the cancellation of the *next* operation, creating a data race.

If no ceiling is defined, the callback receives the caller's token unchanged.

**Naming convention**: In examples, this parameter is named `attempt` rather than `ct`. Using names that differ by more than just length reduces the risk of passing the caller's token where the attempt's token is required, which would silently disable the attempt timeout.

## Work that ignores the token

> [!CAUTION]
> A timeout cannot terminate a callback that ignores its cancellation token. If a callback ignores the token, the orphaned work continues to run, and the policy cannot proceed. The executor awaits the task that ignored the token, so a callback that never returns will hang the entire call.

This is a common failure mode in resilience libraries. Rather than racing the attempt against its timeout - which would require allocating a promise and registration for every suspending call - NResilience uses structural mitigations:

- **Required Tokens**: Every execution overload requires a callback that accepts a `CancellationToken`. There is no zero-argument form that allows you to forget the token.
- **Orphaned Work Events**: An `OrphanedWork` event is raised retrospectively the moment a callback finally returns if it overran its ceiling by more than one second. This catches every callback that ignores its token but eventually finishes.
- **Build-time Analyzers**: [NRES001 and NRES002](../reference/analyzers.md) analyze the callback at build time. They report when a call that accepts a cancellation token is handed the wrong token or no token at all.

While the library cannot fix uncooperative code that never finishes, it prevents forgetting the token and provides diagnostics when it happens. If a call hangs indefinitely, a stack dump is the necessary diagnostic tool.

## `HttpClient.Timeout`

The transport timeout is a bound that the resilience policy cannot see. By default, it is 100 seconds and covers the entire send operation, including all retries and backoff delays. Because having two silent timeout systems is problematic, the NResilience HTTP integration takes ownership of this setting. For more information, see the [HTTP guide](../http/index.md).
