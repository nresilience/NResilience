---
title: The cancellation contract
description: Learn how NResilience manages different cancellation sources and the consequences of ignoring cancellation tokens.
order: 3
---

# The cancellation contract

A resilience attempt can end early for three distinct reasons, and conflating them is a common bug. The executor manages the cancellation sources itself rather than using predicates to guess the cause.

| Scenario | Decision Maker | Outcome / Verdict |
| :--- | :--- | :--- |
| The caller cancels the provided token | External | Rethrown as `OperationCanceledException` |
| The attempt exceeds its time ceiling | Executor | Classified as `Transient` via `AttemptTimeoutException` |
| The callback throws an exception | Classifier | Determined by the configured `Classifier` |

### Caller cancellation behavior
The executor checks for caller cancellation at the start of the operation, after every attempt returns, and after every backoff delay. So a token cancelled during a backoff aborts the call immediately instead of starting another attempt.

Caller cancellation is never retried, never counted against a breaker or budget, and never converted into a timeout. No classifier can override it. Even with `TryRunAsync`, caller cancellation is thrown as an exception.

**Asymmetry in success**: If a caller cancels while an attempt is already succeeding, the executor returns the successful value. The caller has already waited for the attempt; discarding the result buys nothing. The post-attempt check exists to keep the loop from starting another attempt.

## The token the callback receives

When an attempt ceiling is defined, the callback receives a token linked from two sources: a pooled timer source and the caller's token.

The executor never hands out the pooled source's own token directly, because `TryReset` preserves token identity: a callback that outlived its attempt would observe the cancellation of the *next* operation - a data race.

If no ceiling is defined, the callback receives the caller's token unchanged.

**Naming convention**: In examples, this parameter is named `attempt` rather than `ct`. Using names that differ by more than just length reduces the risk of passing the caller's token where the attempt's token is required, which would silently disable the attempt timeout.

## Work that ignores the token

> [!CAUTION]
> A timeout cannot terminate a callback that ignores its cancellation token. If a callback ignores the token, the orphaned work continues to run, and the policy cannot proceed. The executor awaits the task that ignored the token, so a callback that never returns will hang the entire call.

This is a common failure mode in resilience libraries. Instead of racing the attempt against its timeout - which would allocate a promise and registration on every suspending call - NResilience uses structural mitigations:

- **Required tokens**: Every execution overload requires a callback that accepts a `CancellationToken`. There is no zero-argument form that lets you forget the token.
- **Orphaned work events**: An `OrphanedWork` event is raised retrospectively the moment a callback that overran its ceiling by more than one second finally returns, catching every token-ignoring callback that eventually finishes.
- **Build-time analyzers**: [NRES001 and NRES002](../reference/analyzers.md) analyze the callback at build time and report when a call that accepts a cancellation token is handed the wrong token or none.

The library cannot fix code that never finishes, but it prevents forgetting the token and diagnoses it when it happens. A call that hangs indefinitely needs a stack dump.

## The deadline a caller sent

A deadline is the honest bound on a call, and the argument for it over a per-attempt timeout is that only the deadline answers the question the caller actually asked: how long will I be waiting. That argument stops working at the process edge. A service holding 200 ms of its caller's patience sends a request that the next service will happily work on for ten seconds, and the work is garbage before it finishes. Nobody in the chain is behaving unreasonably; nobody has the number they would need to behave otherwise.

`UseAmbientDeadline` passes the number. The effective deadline becomes `min(Deadline, the time the caller is still waiting)`, and no new concept enters the model: the attempt ceiling was already `min(AttemptTimeout, time left)`, so a shorter deadline shortens the attempts, the backoff that will not fit, and the retry that would start too late - all through arithmetic that was already there. This is also the honest answer to the local-versus-fleet objection: it makes a bound true across a call graph without coordinating any state, by passing one number that needs no coordination.

Three decisions in it are worth stating, because each one costs something.

**It is opt-in.** The inbound deadline lives in an `AsyncLocal<T>`, and reading one is not free. Most calls in most processes have no inbound deadline to read, so the read is behind a policy property rather than always-on. A policy that leaves it false pays one branch per call.

**The read happens once per call, not once per attempt.** An inbound deadline is a fixed point in time, so re-reading it can only ever produce the same answer more expensively - and the callers who would pay for that are exactly the ones who opted in. Resolving it once means the effective deadline is a local, live across the attempt `await`, and therefore a field in the state-machine box of *every* suspending call: **16 bytes**, whether or not anybody set the property. The budgets in `tests/NResilience.Gates/Budgets.cs` record the move and the reasoning; the shipping loop now sits 9 B above the hand-written floor it is measured against, which is stated there rather than smoothed over.

**The policy is not derived per call.** The tempting alternative - clamp by handing the loops a `policy with { Deadline = clamped }` - would cost non-users nothing at all. It is wrong for a reason that has nothing to do with allocation: the automatic retry budget and the hedging latency window are keyed by policy instance, so a fresh policy per call would hand every call a fresh budget and a fresh latency estimate. A budget that resets on every call is not a budget. The 16 bytes buy a clamp that leaves both of those where they are.

An inherited deadline that has already expired stops the call before it starts: no attempt runs, `DeadlineExceededException` reports the deadline that applied, and the dependency is never asked for an answer nobody is waiting for. That is the whole point of the feature, and it is why the inbound middleware does not reject the request itself - the request may still be answerable from cache, and refusing it would be a policy decision the library has no standing to make.

## `HttpClient.Timeout`

The transport timeout is a bound the resilience policy cannot see: 100 seconds by default, covering the whole send operation including all retries and backoff. Two silent timeout systems is a problem, so the NResilience HTTP integration takes ownership of the setting. See the [HTTP guide](../http/index.md).
