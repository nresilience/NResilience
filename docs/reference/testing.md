---
title: Testing reference
description: Sequence, Sequence<T> and EventRecorder.
order: 11
---

# Testing reference

Namespace `NResilience.Testing`, package `NResilience.Testing`.

## `Sequence`

| Member | Meaning |
| --- | --- |
| `Sequence.For<T>(TimeProvider? time = null)` | A script of `T`-returning calls. |
| `Sequence.ForVoid(TimeProvider? time = null)` | A script for the void execution overloads, returning `Sequence<Void>`. |

Pass the same `TimeProvider` the policy was given, or a scripted delay is a real sleep.

## `Sequence<T>`

| Member | Meaning |
| --- | --- |
| `Returns(T result)` / `Returns(T result, int count)` | Appends steps that return. |
| `Throws(Exception)` / `Throws(Exception, int count)` | Appends steps that throw. The same instance each time, so a test can assert on reference equality. |
| `Delays(TimeSpan)` | Makes the next step take that long. Repeated calls accumulate. |
| `NextAsync(CancellationToken)` | Serves the next step. This is the callback. |
| `NextVoidAsync(CancellationToken)` | The same, returning `Task`, so the void overloads bind. |
| `CallCount` | Calls served, including the one that ran off the end of the script. |
| `Remaining` | Steps left. |

A step with no delay completes synchronously; a step with a delay suspends and observes the token.
Running off the end throws `InvalidOperationException` naming the script length and the call number.

Building the script is not thread-safe; serving it is.

## `EventRecorder`

| Member | Meaning |
| --- | --- |
| `Record(CallEvent)` | The listener. Assign it: `policy with { OnEvent = recorder.Record }`. |
| `Events` | Every event, in order. |
| `Kinds` | Every kind, in order. The usual assertion surface. |
| `this[int index]` | One event. |
| `Count` | How many. |
| `CountOf(kind)` / `Contains(kind)` / `OfKind(kind)` | The narrower questions. |
| `Single(kind)` | The one event of that kind, or a failure. |
| `Clear()` | Forget everything, for a second act in the same test. |
| `ToString()` | Every event on its own line. |

Thread-safe.

