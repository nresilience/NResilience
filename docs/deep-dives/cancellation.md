---
title: The cancellation contract
description: Which token cancels what, who decides what a cancellation meant, and what happens to work that ignores it.
order: 3
---

# The cancellation contract

Three things can end an attempt early, and conflating any two of them is a bug people spend afternoons
on. So the executor never asks a predicate which one happened - it knows, because it owns the sources.

| What happened | Who decides | Verdict |
| --- | --- | --- |
| The caller cancelled the token they passed in | Nobody. It is not a failure | Rethrown, always |
| The attempt exceeded its ceiling | The executor | `Transient`, with an `AttemptTimeoutException` |
| The callback threw something else | Your classifier | Whatever it says |

Caller cancellation is checked at the top of the call, after every attempt returns, and after every
backoff delay - because a token cancelled 400 milliseconds into a backoff must abort the operation
rather than start another attempt. It is never retried, never counted against a breaker or a budget,
never converted into a timeout, and no classifier can override it. `TryRunAsync` reports every other
failure and still throws this one.

There is one deliberate asymmetry. A caller who cancels while an attempt is already succeeding gets the
value. They have waited for that attempt either way, and throwing away work that is done and paid for
helps nobody; the post-attempt check exists to stop the loop starting *another* attempt.

## The token the callback receives

When there is an effective attempt ceiling, the callback receives a token linked from two sources: a
pooled source driving the timer, and the caller's token. The pooled source's own token is never handed
out, because `TryReset` preserves token identity - a callback that outlived its attempt would observe
the *next* operation's cancellation, which is a data race dressed up as a timeout.

When there is no ceiling, the callback receives the caller's token unchanged.

The samples name that parameter `attempt` rather than `ct`, because two tokens in scope with names
that differ only in length is how a call site ends up passing the wrong one - and passing the caller's
token where the attempt's belongs disables the attempt timeout without any visible symptom.

## Work that ignores the token

> [!CAUTION]
> A timeout cannot kill a callback that ignores its cancellation token. The orphaned work keeps
> running, and the policy does not move on: the executor is awaiting the very task that ignored its
> token, so a callback that never returns hangs the call along with itself.

This is the single most-hit footgun in the ecosystem - four separate Polly issues, the last of them a
reviewer pointing out that Polly's own shipped sample demonstrated it.

Moving on regardless would mean racing the attempt against its timeout, and that allocates a promise
and a registration on every suspending call whether or not anything ever times out. That price is not
worth a diagnostic, so the mitigations are structural instead:

- **Every execution overload requires a callback that takes a `CancellationToken`.** There is no
  zero-argument form to forget.
- **An `OrphanedWork` event names the policy** when an attempt overruns its ceiling by more than a
  second. It is raised retrospectively, the moment the work finally does return, which catches every
  callback that ignores its token and eventually finishes.
- **Two analyzers say it at build time.** [NRES001 and NRES002](../reference/analyzers.md) read the
  callback and report a call that takes a cancellation token and was handed the wrong one, or none.
  They ship inside the package, so nobody has to know they exist.

A callback that never finishes leaves the call itself hanging, and there the diagnostic you need is a
stack dump rather than an event. The library cannot fix uncooperative code. It can refuse to make
forgetting easy, and it can tell you when it happened.

## `HttpClient.Timeout`

The transport timeout is the one bound nothing in the policy can see: it defaults to 100 seconds and
covers the entire send, retries and backoff included. Two timeout systems interacting silently is the
worst available default, so the HTTP integration takes it over. See [HTTP](../http/index.md).

