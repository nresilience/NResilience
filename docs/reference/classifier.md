---
title: Classifier and verdicts
description: The classifier's members, the four verdict kinds, and what the shipped classifiers know.
order: 3
---

# `Classifier`

`sealed class Classifier`. Immutable: every derivation returns a new instance.

| Member | Meaning |
| --- | --- |
| `Classifier.Default` | `TimeoutException`, `IOException` and `SocketException` are `Transient`. Anything unrecognized is `Permanent`. |
| `Classifier.Http` | `Default` plus `HttpRequestException` as `Transient` and a status-code rule for `HttpResponseMessage`. |
| `Classifier.RetryEverything` | No rules; every exception is `Transient`. |
| `On<TException>(Verdict)` | A fixed verdict for an exception type, matched including subclasses. |
| `On<TException>(Func<TException, Verdict>)` | A predicate that can inspect the exception. |
| `OnResult<T>(Func<T, Verdict>)` | A rule for a returned value. `T` is matched exactly, not by assignability. |
| `ClassifyException(Exception)` | The verdict for an exception. |
| `ClassifyResult<T>(T)` | The verdict for a returned value, or `Verdict.Ok` when nothing is registered for `T`. |
| `ToString()` | Every rule, in evaluation order, including the unrecognized-exception verdict. |

Rules are evaluated most-recently-added first, so a rule you add beats one it was derived from.

`Classifier.Http`'s status-code rule:

| Status | Verdict |
| --- | --- |
| 429 | `Throttled`, carrying `Retry-After` when present |
| 503 with `Retry-After` | `Throttled`, carrying it |
| Other 5xx, and 408 | `Transient` |
| Everything else | `Ok` |

`Retry-After` is read in both forms: a delta in seconds, and an HTTP date, which is converted to a
delay and floored at zero.

## `Verdict`

`readonly struct Verdict`.

| Member | Meaning |
| --- | --- |
| `Kind` | The `VerdictKind`. |
| `RetryAfter` | Server pushback, honored in preference to any backoff curve. Null when the server said nothing. |
| `Verdict.Ok` | The call worked. |
| `Verdict.Transient` | A failure that may not recur. |
| `Verdict.Permanent` | A failure that will recur. |
| `Verdict.Throttled(TimeSpan?)` | The dependency is defending itself, with optional pushback. |

Value equality; `ToString()` prints `Throttled (retry after 2s)`.

## `VerdictKind`

| Value | Retried? | Counts against the breaker? |
| --- | --- | --- |
| `Ok` | Returned | Sampled as a success |
| `Transient` | Yes, short curve | Yes |
| `Throttled` | Yes, long curve or `Retry-After` | No - the dependency is working correctly |
| `Permanent` | Never | No - overwhelmingly a client-side fact |

Two verdicts are produced by the [executor](index.md) rather than by a classifier, and no classifier can override
either: its own attempt timeout, which is `Transient`, and caller cancellation, which is not a failure
at all.

A classifier that calls an *exception* `Ok` is read as `Permanent`, because an exception cannot be
turned into a value.

