---
title: Testing reference
description: Reference for the Sequence and EventRecorder tools used for testing resilience policies.
order: 11
---

# Testing reference

The testing utilities are located in the `NResilience.Testing` namespace within the `NResilience.Testing` package.

## `Sequence`

The `Sequence` class is a factory for creating scripted call sequences that return pre-defined results or throw exceptions.

| Member | Description |
| :--- | :--- |
| `Sequence.For<T>(TimeProvider? time = null)` | Creates a scripted sequence that returns values of type `T`. |
| `Sequence.ForVoid(TimeProvider? time = null)` | Creates a scripted sequence for void execution overloads, returning a `Sequence<Void>`. |

To ensure that scripted delays are handled deterministically and do not introduce real-time sleeps, pass the same `TimeProvider` to the `Sequence` that you provided to the resilience policy.

## `Sequence<T>`

`Sequence<T>` allows you to define a series of outcomes to be served to the policy during a test.

| Member | Description |
| :--- | :--- |
| `Returns(T result)` / `Returns(T result, int count)` | Appends one or more steps that return the specified result. |
| `Throws(Exception)` / `Throws(Exception, int count)` | Appends one or more steps that throw the specified exception. The same exception instance is used for all counts, allowing for reference equality assertions in tests. |
| `Delays(TimeSpan)` | Configures the next step to take the specified amount of time to complete. Multiple calls to `Delays` accumulate. |
| `NextAsync(CancellationToken)` | Serves the next step in the sequence. This is the method typically used as the resilience callback. |
| `NextVoidAsync(CancellationToken)` | Similar to `NextAsync`, but returns a `Task` to support void execution overloads. |
| `CallCount` | The total number of calls served, including any call that exceeded the script length. |
| `Remaining` | The number of steps remaining in the script. |

### Execution behavior

- **Timing**: A step with no delay completes synchronously. A step with a delay suspends execution and observes the provided `CancellationToken`.
- **Bounds**: If a call is made after the script has been exhausted, the sequence throws an `InvalidOperationException` specifying the script length and the call number.
- **Thread Safety**: Building the script is not thread-safe, but serving the script via `NextAsync` is thread-safe.

## `EventRecorder`

`EventRecorder` is a utility for capturing and asserting on the events emitted by a resilience policy.

| Member | Description |
| :--- | :--- |
| `Record(CallEvent)` | The event listener method. Assign this to the policy's `OnEvent` property: `policy with { OnEvent = recorder.Record }`. |
| `Events` | A collection of all captured events in the order they occurred. |
| `Kinds` | A collection of all captured `CallEventKind` values. This is the primary surface for assertions. |
| `this[int index]` | The event at the specified 0-based index. |
| `Count` | The total number of captured events. |
| `CountOf(kind)` | The number of events of a specific kind. |
| `Contains(kind)` | `true` if at least one event of the specified kind was captured. |
| `OfKind(kind)` | A collection of all events of the specified kind. |
| `Single(kind)` | The only event of a specific kind. Throws an exception if more than one (or none) are found. |
| `Clear()` | Clears all captured events, allowing the recorder to be reused in the same test. |
| `ToString()` | Returns a human-readable list of all events, with one event per line. |

`EventRecorder` is thread-safe.
