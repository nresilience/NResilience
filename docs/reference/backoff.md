---
title: Backoff
description: The delay between attempts - the shipped curves, the parameters, and jitter.
order: 4
---

# `Backoff`

`readonly record struct Backoff`.

| Member | Meaning |
| --- | --- |
| `Backoff.Default` | `Exponential()` - 100 ms transient base, 1 s throttled base, factor 2, 30 s cap, full jitter. |
| `Backoff.None` | Retry immediately. Correct only when the dependency is known not to be shared. |
| `Backoff.Exponential(transientBase, throttledBase, factor, max)` | Exponential, with separate bases per retryable verdict. All four parameters are optional. |
| `Backoff.Constant(delay)` | The same delay every time. |
| `Backoff.Custom(Func<NextAttempt, TimeSpan>)` | Compute it yourself. Ignores `Max` and jitter. |
| `Jitter` | How much randomness to apply. `init`-settable, so `Backoff.Default with { Jitter = Jitter.None }` works. |
| `Max` | The hard cap on any single delay, or `Timeout.InfiniteTimeSpan`. |
| `Compute(in NextAttempt)` | The delay before that attempt. Never negative. |

Defaults: `transientBase` 100 ms, `throttledBase` 1 s, `factor` 2.0, `max` 30 s.

The delay for attempt *n* is `base × factor^(n-2)`, capped at `Max`, then jittered. So the first retry
is served the base delay.

`Verdict.RetryAfter` wins over every curve: it is honored verbatim, capped only by `Max`, with no
jitter applied. The [executor](index.md) additionally refuses to serve any delay that would consume the rest of
the deadline - the call fails with the deadline instead of sleeping through it.

`default(Backoff)` reads as `Backoff.Default`, because `policy with { Backoff = default }` compiles.

## `Jitter`

| Value | Delay |
| --- | --- |
| `Full` | `random(0, computed)`. The default, and the only shape that destroys the correlation between clients. |
| `Equal` | `computed/2 + random(0, computed/2)`. Keeps a floor under the delay. |
| `None` | No randomness. Only correct in tests, and rarely there. |

