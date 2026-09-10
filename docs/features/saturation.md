---
title: Local saturation
description: Stop the measured terms learning from a duration that is mostly this process's own thread-pool queue.
order: 12
---

# Local saturation

Every measured term in this library times the callback with a wall clock and attributes the result to the dependency. When the local thread pool is the bottleneck that attribution is wrong: a work item waiting 400 ms for a thread looks, from inside the executor, exactly like a dependency that got 400 ms slower.

**Saturation** is the switch that tells the two apart. Above a multiple of this process's own normal queue delay, the policy stops feeding its estimates - they hold what they last learned until the queue drains.

It is **opt-in** - off in `Resilience.Default`, off in `Resilience.Http`, and off in `AddResilience()` - because it changes what every other measured term learns.

## Why it matters

Three things read the estimate a policy measures, and a contaminated number moves all three in the same direction at the same moment:

| Reader | What a contaminated number does |
| :--- | :--- |
| [`AttemptCeiling`](deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it) | Raises the ceiling, so a doomed attempt runs longer |
| [`Backoff.MeasuredBase`](retry.md#measure-the-backoff-base-instead-of-guessing-it) | Lengthens the backoff, so the retry that would have worked waits |
| [`Hedge.At`](hedging.md) | Raises the hedge threshold, so hedges stop firing |

The moment they all move is a local incident, and the library's response to a slow dependency - relax, give it room - is the opposite of what a starved pool needs. Neither the breaker nor the retry budget can see it, because the dependency is fine.

## Turn it on

<!-- snippet: saturation-configure -->
```csharp
var api = Resilience.Http with
{
    AttemptCeiling = AttemptCeiling.Above(multiple: 3),

    // Five times this process's own normal queue delay. Above that, the attempt ceiling, the
    // measured backoff base and the hedge threshold stop being fed - they hold what they last
    // learned until the queue drains. Nothing is refused and no bound moves.
    Saturation = Saturation.Above(multiple: 5),
};
```
<!-- endsnippet -->

`Saturation.Above(5)` is a complete configuration. The properties you can change:

| Property | Default | Description |
| :--- | :--- | :--- |
| `Multiple` | none - you supply it | How many times this process's normal queue delay counts as saturated. Must be greater than 1. |
| `Floor` | `20 ms` | A floor under the delay that counts as saturated, whatever the multiple says. |
| `MinimumSamples` | `20` | How many probes the baseline needs before it is used at all. |

Both bars have to be cleared. A healthy pool queues in microseconds, so five times normal is still microseconds - and a policy that stopped measuring every time a garbage collection moved one probe would never learn anything. The `Floor` is the "do not bother" line, and it is what makes a low `Multiple` safe; the multiple is what makes the floor portable. A host whose pool normally queues 25 ms is over the 20 ms floor all day, and 25 ms is what normal looks like there - so a busy host is not automatically in an incident.

## What it does, and what it does not

- **It only declines to record.** Nothing is refused, no bound moves, and no delay is added. A policy that finds itself saturated makes the same attempts, in the same shape, with the same verdicts - the only difference is what it learned.
- **A cold baseline is never saturated.** The probe samples four times a second at most, so a process is not saturated for its first few seconds however deep its queue is. That is the same cold-start rule every measured term follows: no estimate means no opinion, not a guessed one.
- **It resumes the moment the queue drains.** The estimates are windowed, so they catch up from the samples that arrive after the episode rather than being reset.
- **It does not reach the breaker or a limiter.** Both are live objects two policies may share, so a switch on one policy may not silently reconfigure a guard the other is holding - the same rule `Adaptive` follows. The breaker has a second reason: its latency baseline is recorded and read in one step, so declining to feed it while still judging against it would trip the breaker on a local incident.

## Read what it produces

<!-- snippet: saturation-read -->
```csharp
var api = TestPolicy.Instant with
{
    AttemptCeiling = AttemptCeiling.Above(multiple: 3),
    Saturation = Saturation.Above(multiple: 5),
    OnEvent = e =>
    {
        if (e.Kind == CallEventKind.SaturationDetected)
        {
            // Once per episode, not once per call, so this counts local incidents. e.Delay is
            // the queue delay that was measured.
            Console.WriteLine(value: $"pool queueing for {e.Delay?.TotalMilliseconds} ms");
        }
    },
};

// The live number, for a dashboard. Null until the process-wide probe has a baseline, which
// takes a few seconds - the probe samples four times a second at most.
var queueDelay = api.Measured.QueueDelay;
```
<!-- endsnippet -->

`Measured.QueueDelay` is the only reading on [`MeasuredValues`](../reference/resilience.md#measuredvalues) that describes this process rather than the dependency, and the only one that is process-wide: there is one thread pool, so every policy in the process reports the same number.

Put it beside `Measured.AttemptCeiling` and `Measured.BackoffBase`. When all three move together, the dependency did not get slower and this host did. The `nresilience.pool.delay` histogram records the same number at the onset of each episode, so a count of samples is a count of local incidents. See [Telemetry](../di/telemetry.md) for the full instrument list.

## From configuration

<!-- snippet: appsettings.resilience.saturation.json -->
```json
{
  "Resilience": {
    "api": {
      "Preset": "Http",
      "AttemptCeiling": { "Multiple": 3 },
      "Saturation": {
        "Multiple": 5,
        "Floor": "00:00:00.020",
        "MinimumSamples": 20
      }
    }
  }
}
```
<!-- endsnippet -->

`"Saturation": { "Enabled": false }` removes it again, which is the only way a merged `appsettings.Production.json` can take back what a base file turned on.

## The failure mode

A process that is **always** saturated learns a high queue delay as normal and stops finding itself saturated. The baseline is relative, so all it can tell you is that this minute is unlike the last hour - and a host that has been queueing for a week has no unlike-the-last-hour to report.

That is the same shape as the [adaptive limiter](rate-limiting.md#let-it-find-its-own-concurrency)'s "a process starting into an existing queue learns the queued latency as normal", and it has the same answer: fix the pool. This feature keeps a local incident from corrupting your dependency measurements; it is not a substitute for having enough threads.

## Go deeper

- [Deadlines and attempt timeouts](deadlines.md#measure-the-attempt-ceiling-instead-of-guessing-it) - the reader whose contamination is easiest to see.
- [`Resilience` reference](../reference/resilience.md) - the `Saturation` property and the `Measured` readings.
- [Events](../reference/events.md) - `SaturationDetected` and what it carries.
- [Simulation](../testing/simulation.md#model-the-thread-pool) - `Pool` models a stalled thread pool on a virtual clock, so what this switch does to your availability and p99 is a number rather than an argument.
