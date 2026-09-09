---
title: Simulation
description: Run your policy against a modeled dependency on a virtual clock and measure the load multiplier, availability, and p99 it produces.
order: 2
---

# Simulation

[Fault injection](fault-injection.md) tells you what happens when you break something once. A simulation measures the result: the load multiplier, the availability, and the p99 your configuration produces over five simulated minutes of a brownout. Nothing sleeps, so those five minutes cost about as much as a unit test.

`Simulate` lives in `NResilience.Testing` and touches no shipping path.

```bash
dotnet add package NResilience.Testing
```

## Run one

Describe a dependency, offer it load, and run:

<!-- snippet: simulation-run -->
```csharp
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

var report = Simulate.Policy(policy: api)
    .Against(dependency: Dependency
        .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
        .Brownout(after: TimeSpan.FromSeconds(value: 30), slower: 8, lasting: TimeSpan.FromMinutes(value: 1)))
    .Under(load: Load.Constant(perSecond: 500))
    .For(duration: TimeSpan.FromMinutes(value: 5))
    .Run(seed: 42);

// The number the dependency's owner asks for: attempts that reached it, per call you made.
Assert.True(condition: report.LoadMultiplier <= 1.2);

// And the one your own caller feels.
Assert.True(condition: report.Latency(quantile: 0.99) < TimeSpan.FromSeconds(value: 1));
```
<!-- endsnippet -->

`report.ToString()` prints the whole measurement as one block:

<!-- snippet: simulation-report.txt -->
```text
Simulation seed 42 over 00:05:00
  Calls            149742
  Succeeded        135155
  Failed           14587
  Availability     0.9026
  LoadMultiplier   1.016
  Amplification    1.104
  Latency p50      00:00:00.0255265
  Latency p99      00:00:00.4341090
  BreakerOpens     0
  TimeToRecover    00:00:00.0006885
```
<!-- endsnippet -->

## Read the report

| Member | What it answers |
| :--- | :--- |
| `LoadMultiplier` | Attempts that reached the dependency, per call you made. One is a policy that retried nothing. Below one is a policy whose breaker or budget refused calls before they left. |
| `Amplification` | The same ratio in the worst one-second window. A dependency feels the worst second, not the five-minute average. |
| `Availability` | The fraction of calls that ended in success. |
| `Latency(quantile)` | Caller-observed latency, including every retry and backoff the call served. |
| `BreakerOpens` | How many times a breaker tripped. |
| `TimeToRecover` | How long after the last impairment ended before a full second of calls all succeeded. `null` when the run ended first, and a `null` of that kind is the finding. |
| `CountOf(kind)` | How many [`CallEvent`](../reference/events.md)s of one kind the run raised, so a claim about suppressed hedges or budget refusals is a number. |

`Calls`, `Succeeded`, `Failed`, and `Reached` are the raw counts the ratios come from.

## Model the dependency

`Dependency.Healthy` fits a curve to two quantiles: the sampled latencies hit both exactly, and the first percentile sits as far below the median as the 99th sits above it. Each method returns a new value, so a dependency can live in a `static readonly` field.

| Member | Description |
| :--- | :--- |
| `Dependency.Healthy(p50, p99)` | A dependency that answers every call, with this latency spread. |
| `Brownout(after, slower, lasting)` | A stretch during which it is slower but still answering. |
| `Outage(after, lasting)` | A stretch during which every call fails immediately. |
| `Failing(rate)` | The fraction of calls that fail while nothing else is wrong. A failure is an `IOException`, which both `Classifier.Default` and `Classifier.Http` call `Transient`. |
| `Capacity(concurrent)` | How many calls it serves at once. Past the bound it queues in proportion to how far past it is, and past three times it sheds. |

Reach for `Brownout` before `Outage`. A dependency that is down trips the breaker immediately and the call ends; a dependency that is eight times slower is the one that fills your attempt budget while every individual call still succeeds, and that is the shape most incidents take.

## Offer the load

`Load.Constant(perSecond)` offers a steady rate. Gaps between arrivals are spread rather than even, so the run contains the short bursts a real second of traffic contains, and the seed puts those bursts in the same place every time.

`peers` says how many other processes offer the same rate to the same dependency:

<!-- snippet: simulation-peers -->
```csharp
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

// Four hundred calls at once is what this dependency serves before it starts queueing, and
// fifty pods offering five hundred a second each is what decides whether it gets there.
var shared = Dependency
    .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
    .Brownout(after: TimeSpan.FromSeconds(value: 30), slower: 8, lasting: TimeSpan.FromMinutes(value: 1))
    .Capacity(concurrent: 400);

var report = Simulate.Policy(policy: api)
    .Against(dependency: shared)
    .Under(load: Load.Constant(perSecond: 500, peers: 50))
    .For(duration: TimeSpan.FromMinutes(value: 5))
    .Run(seed: 42);

// The five-minute average hides the second that tipped the dependency over.
Assert.True(condition: report.Amplification > report.LoadMultiplier);
```
<!-- endsnippet -->

> [!IMPORTANT]
> Peers are a term in the dependency's arithmetic and nothing more. Their offered load counts against `Capacity`; their own retries, breakers, and budgets are not simulated. That is enough to answer "does my retry budget hold when I am one of fifty" and not enough to claim fifty policies were simulated.

## Compare two configurations

The seed fixes the run, so two simulations that differ in one setting differ only because of that setting. This is how a configuration argument becomes a number:

<!-- snippet: simulation-compare -->
```csharp
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

var slowing = Dependency
    .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
    .Brownout(after: TimeSpan.FromSeconds(value: 30), slower: 8, lasting: TimeSpan.FromMinutes(value: 1));

// The same seed and the same dependency, one setting different - so the difference between
// the two reports is the setting rather than the run.
var unbudgeted = Simulate.Policy(policy: api with { Budget = RetryBudget.None })
    .Against(dependency: slowing)
    .Under(load: Load.Constant(perSecond: 500))
    .For(duration: TimeSpan.FromMinutes(value: 5))
    .Run(seed: 42);

var budgeted = Simulate.Policy(policy: api)
    .Against(dependency: slowing)
    .Under(load: Load.Constant(perSecond: 500))
    .For(duration: TimeSpan.FromMinutes(value: 5))
    .Run(seed: 42);

Assert.True(condition: budgeted.LoadMultiplier < unbudgeted.LoadMultiplier);
Assert.True(condition: budgeted.CountOf(kind: CallEventKind.RejectedByBudget) > 0);
```
<!-- endsnippet -->

## What is fake, and what is not

Three things are fake: the clock, the random source, and the dependency. The policy is yours, and the executor, the breaker, the retry budget, the classifier, and every estimator are the shipping ones - nothing about the decision logic is re-implemented for the simulator. A simulator that modeled the library would be worse than no simulator.

The report is reproducible to the last byte: `Run(seed)` twice with the same seed and `ToString()` returns the same string, on any operating system and any processor, because the simulator's own arithmetic uses only operations IEEE-754 specifies exactly. Assert on that in your own suite if you like - a run that stops reproducing means something on the path is reading a clock or a random source it does not own.

## Limits

Three, and each is a property of the design rather than a gap to fill:

- **One process.** The simulator cannot model fifty pods making their own decisions. `peers` scales the load without simulating the peers, which is an approximation and says so.
- **[Saturation](../features/saturation.md) reads nothing.** The thread pool it measures is the real one, and the real one is idle while a simulation runs.
- **The dependency is a model.** Latency, capacity, and impairment are the three dimensions it has. A dependency whose failure mode is none of those is one the simulator cannot show you.

## Go deeper

- [Fault injection](fault-injection.md) - injecting faults at a rate, in a real process, on real time.
- [Retry budget](../features/retry-budget.md) - the guard the load multiplier is mostly a measurement of.
- [Testing reference](../reference/testing.md) - every member, in a table.
