---
title: Backoff
description: Reference for the Backoff structure, supported delay curves, and jitter configurations.
order: 4
---

# `Backoff`

`Backoff` is a `readonly record struct` that determines the delay between retry attempts.

| Member | Description |
| :--- | :--- |
| `Backoff.Default` | Uses `Exponential()` with a 100 ms transient base, 1 s throttled base, factor of 2, 30 s cap, and full jitter. |
| `Backoff.None` | Retries immediately. Use this only when the dependency is not shared. |
| `Backoff.Exponential(transientBase, throttledBase, factor, max)` | Uses exponential backoff with separate bases for different retryable verdicts. All parameters are optional. |
| `Backoff.Constant(delay)` | Applies the same delay before every retry. |
| `Backoff.Custom(Func<NextAttempt, TimeSpan>)` | Allows you to compute the delay yourself. This mode ignores the `Max` property and jitter. |
| `Jitter` | Determines the amount of randomness applied to the delay. |
| `Max` | The maximum allowable delay for any single attempt. Defaults to 30 s. Use `Timeout.InfiniteTimeSpan` for no cap. |
| `TransientBase` | The base delay for a `Transient` verdict. Zero for a `Custom` curve. |
| `ThrottledBase` | The base delay for a `Throttled` verdict. Zero for a `Custom` curve. |
| `Factor` | The growth per attempt. |
| `Kind` | Which curve this is: `Exponential`, `Constant`, or `Custom`. |
| `Compute(in NextAttempt)` | Calculates the delay before the specified attempt. This value is never negative. |

### Reading a backoff back

The five readable properties report the values `Compute` will actually use, so an unconstructed
`default(Backoff)` reports the shipped defaults rather than zeros:

```csharp
var backoff = default(Backoff);

backoff.Kind;           // BackoffKind.Exponential
backoff.TransientBase;  // 00:00:00.1000000
backoff.ThrottledBase;  // 00:00:01
backoff.Factor;         // 2
backoff.Max;            // 00:00:30
```

That makes a curve round-trippable - read the properties off one `Backoff`, change one, and rebuild:

```csharp
var slower = Backoff.Exponential(
    existing.TransientBase,
    existing.ThrottledBase,
    existing.Factor,
    TimeSpan.FromMinutes(2)) with { Jitter = existing.Jitter };
```

The factories remain the only way to *construct* a `Backoff`; the properties are read-only. A
`Backoff` built with a positive `Factor` is what `Normalized()` recognizes as constructed, so
there is no partial-object shape to get wrong.

### Exponential backoff calculation
For exponential backoff, the delay for attempt *n* is calculated as:
`base × factor^(n-2)`

The result is capped at `Max` and then jittered. The first retry is served the base delay.

**Default parameters**:
- `transientBase`: 100 ms
- `throttledBase`: 1 s
- `factor`: 2.0
- `max`: 30 s

### Priority and constraints
The `Verdict.RetryAfter` value takes precedence over all backoff curves. It is honored verbatim, capped only by `Max`, and no jitter is applied.

The [executor](index.md) also ensures that a delay does not consume the remaining time on the deadline. If a delay would exceed the deadline, the call fails immediately with a deadline exception instead of sleeping.

**Note**: `default(Backoff)` is equivalent to `Backoff.Default`.

## `BackoffKind`

`BackoffKind` identifies which curve a `Backoff` follows. Read it from the `Kind` property.

| Value | Curve |
| :--- | :--- |
| `Exponential` | The delay grows by `Factor` on each attempt. Set by `Backoff.Exponential` and `Backoff.Default`. |
| `Constant` | The same delay every time. Set by `Backoff.Constant` and `Backoff.None`. |
| `Custom` | A caller-supplied delegate computes the delay. Set by `Backoff.Custom`. |

## `Jitter`

Jitter adds randomness to the delay to prevent "thundering herd" problems where multiple clients retry simultaneously.

| Value | Resulting Delay |
| :--- | :--- |
| `Full` | `random(0, computed)`. This is the default and the most effective way to break correlation between clients. |
| `Equal` | `(computed / 2) + random(0, computed / 2)`. This maintains a minimum delay floor. |
| `None` | No randomness is applied. This is typically only used in tests. |
