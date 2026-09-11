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
| `AvailabilityAt(criticality)` | The fraction of calls at one [`Criticality`](#offer-a-mix-of-criticality) that ended in success. `CallsAt` and `SucceededAt` are the counts it comes from. |
| `RefusedByLimiter` | Attempts a [limiter](#bound-what-leaves-the-process) refused before they could leave the process. They are not in `Reached`. |
| `Timeline` | Every event the run raised, each with the virtual time it was raised at. `null` unless the run was asked to [record one](#record-a-timeline). |
| `EngineVersion` | Which build of the library produced the report. Determinism is a promise about one version, so two reports are comparable when this matches. |

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

### Offer a mix of criticality

Not all of a service's traffic matters equally, and [`Criticality`](../features/criticality.md) is how the library says so: a policy that opts in holds back half its retry budget from `Sheddable` work and never hedges it. `Mix` offers a share of the load at a level, published the way a service publishes one - through `AmbientCriticality`, so the executor reads a level that arrived rather than one the simulator handed it.

<!-- snippet: simulation-criticality -->
```csharp
var api = Resilience.Http with
{
    Deadline = TimeSpan.FromSeconds(value: 10),
    UseAmbientCriticality = true,
    Name = "api",
};

// Seven calls in ten are a backfill nobody is waiting on. Critical is the remainder, which is
// what a call with no level already is - so it is not set, it is what is left.
var load = Load.Constant(perSecond: 500).Mix(criticality: Criticality.Sheddable, fraction: 0.7);

var report = Simulate.Policy(policy: api)
    .Against(dependency: Dependency
        .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
        .Brownout(after: TimeSpan.FromSeconds(value: 30), slower: 8, lasting: TimeSpan.FromMinutes(value: 1)))
    .Under(load: load)
    .For(duration: TimeSpan.FromMinutes(value: 5))
    .Run(seed: 42);

// The measurement the setting exists to move. The aggregate averages the two together and can
// hide the whole trade.
Assert.True(condition: report.AvailabilityAt(criticality: Criticality.Critical)
                       > report.AvailabilityAt(criticality: Criticality.Sheddable));
```
<!-- endsnippet -->

`Critical` is the remainder and cannot be set. It is what a call with no level already is, so a load that names shares for the other three has said everything there is to say - and a fourth number that had to agree with the first three would only ever be a way to disagree with them.

The mix is a property of the run, not of the policy: the traffic is what it is whether or not the policy sets `UseAmbientCriticality` to notice. That is the comparison worth running, and it is the same reason `WithPool` is accepted on a policy with no `Saturation`.

> [!IMPORTANT]
> **Read the availability per level, not the aggregate.** Criticality reallocates rather than creates: what it holds back from the backfill it spends on the checkout, so the dependency serves about as many calls either way and `Availability` barely moves. `AvailabilityAt(criticality)` is the number the setting exists to change, and a run read only for the aggregate will report that it does nothing.

The mix is this process's own traffic. `peers` scales what the dependency is offered without simulating the peers, and it does not label their calls either.

## Model the thread pool

Every estimator in the library times the callback with a wall clock and attributes all of it to the dependency. When the local thread pool is the bottleneck that attribution is wrong: a queue delay of 400 ms reads, from inside the executor, exactly like a dependency that got 400 ms slower. [`Saturation`](../features/saturation.md) is the switch that tells the two apart, and `WithPool` is how a run gets a pool for it to measure.

`Pool.Healthy(delay)` is the baseline every stall is judged against - `Saturation.Above(5)` is relative, so it is not enough for a stall to be deep, it has to be deep relative to what this process normally does. `Stall` is a stretch during which work items wait far longer than that:

<!-- snippet: simulation-pool -->
```csharp
var api = Resilience.Http with
{
    Deadline = TimeSpan.FromSeconds(value: 10),
    AttemptCeiling = AttemptCeiling.Above(multiple: 3),
    Saturation = Saturation.Above(multiple: 5),
    Name = "api",
};

// A healthy pool queues for tens of microseconds. This one stops keeping up half a minute in:
// work items wait 400 ms for a thread, and every call looks 400 ms slower from inside the
// executor while the dependency is fine.
var pool = Pool
    .Healthy(delay: TimeSpan.FromMicroseconds(value: 80))
    .Stall(after: TimeSpan.FromSeconds(value: 30), delay: TimeSpan.FromMilliseconds(value: 400),
        lasting: TimeSpan.FromSeconds(value: 20));

var report = Simulate.Policy(policy: api)
    .Against(dependency: Dependency
        .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200)))
    .Under(load: Load.Constant(perSecond: 200))
    .WithPool(pool: pool)
    .For(duration: TimeSpan.FromMinutes(value: 2))
    .Run(seed: 42);

// One event for one episode, raised at its onset - so this is a count of local incidents.
Assert.Equal(expected: 1, actual: report.CountOf(kind: CallEventKind.SaturationDetected));
```
<!-- endsnippet -->

The modeled delay is spent inside the call, not just reported to the probe. That is the point: a stall lengthens every attempt, so it reaches the availability, the latency quantiles, and the load multiplier the same way a slow dependency does - which is exactly why the two are hard to tell apart, and why a simulation that only counted events would flatter the feature.

A pool is a property of the run, not of the policy, so `WithPool` is accepted on a policy with no `Saturation` at all. That is the comparison worth running: the same pool and the same seed against a policy that can tell a stalled pool from a slow dependency and one that cannot. Refusing it would leave only "stalled" against "not stalled", which measures the stall rather than the setting.

> [!IMPORTANT]
> The simulator reproduces the probe's two blind spots rather than smoothing them over, because both change what your policy would actually do:
>
> - **A cold baseline is never saturated.** Probes are queued four times a second, so `Saturation.MinimumSamples` of 20 is five seconds during which no stall registers however deep it is.
> - **A long stall stops being detected partway through.** The baseline is a rolling median over the last minute, so a stall that comes to cover more than half of it *becomes* the median. The onset is reported and then the episode goes quiet while the queue is still deep - which is why a count of `SaturationDetected` is a count of onsets rather than of minutes.

Leaving `WithPool` off does not fall back to the real thread pool. A policy that configures `Saturation` then reads a modeled pool that never queues and so is never saturated, which keeps a report reproducible to the last byte on a machine whose own pool happens to be busy.

## Bound what leaves the process

A [limiter](../features/rate-limiting.md) is the other half of controlling amplification. A [retry budget](../features/retry-budget.md) bounds the *share* of traffic that is retried; a limiter bounds how much goes out at all, before anything has gone wrong. `WithLimiter` builds one against the run's virtual clock and acquires a permit inside every attempt:

<!-- snippet: simulation-limiter -->
```csharp
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

// Four hundred calls a second at 50 ms apiece is about twenty in flight, so sixty permits is
// headroom while the dependency is well - and a wall the moment calls start piling up.
Simulation Bulkhead(Dependency dependency) =>
    Simulate.Policy(policy: api)
        .Against(dependency: dependency)
        .Under(load: Load.Constant(perSecond: 400))
        .For(duration: TimeSpan.FromSeconds(value: 20))
        .WithLimiter(limiter: _ => Limit.Concurrency(permits: 60));

var well = Dependency.Healthy(p50: TimeSpan.FromMilliseconds(value: 50), p99: TimeSpan.FromMilliseconds(value: 300));

var healthy = Bulkhead(dependency: well).Run(seed: 7);
var brownout = Bulkhead(dependency: well
    .Brownout(after: TimeSpan.FromSeconds(value: 5), slower: 8, lasting: TimeSpan.FromSeconds(value: 10))).Run(seed: 7);

// A guard that costs nothing while nothing is wrong is the whole argument for setting one.
Assert.Equal(expected: 0, actual: healthy.RefusedByLimiter);

// And one that bites the moment calls pile up is what stops the pile-up spreading. These
// attempts never reached the dependency, so they are not in Reached either.
Assert.True(condition: brownout.RefusedByLimiter > 0);
Assert.True(condition: brownout.Reached < healthy.Reached);
```
<!-- endsnippet -->

Nothing here is modeled. The run builds your limiter and asks it, and the executor's own handling of the refusal is the shipping one: `RateLimitedException` becomes `Verdict.Refused`, which never reaches the classifier, never counts as evidence against the breaker, and is never charged to the retry budget - because the call did not leave. `RefusedByLimiter` counts those attempts, which is what tells a load multiplier below one apart from a breaker or a budget pulling it the same way.

`WithLimiter` takes a **factory** rather than a limiter, for two reasons. A limiter that takes a `TimeProvider` needs this run's clock, and you have no way to reach it. And a limiter is stateful: `RunAll` sharing one would let the first seed's discovered limit decide the second seed's run, which would make a band of a scenario a band of the order its seeds were listed in.

The permit is acquired **inside** the attempt - after the modeled pool's queue, before the dependency - because that is where a real callback acquires one, and because a guard a retry bypasses is not a guard. The lease is held for the length of the attempt, which is what makes a concurrency limit a bulkhead.

> [!IMPORTANT]
> **Two kinds of limiter are refused, and the run says so rather than reporting a number.**
>
> - **A replenishing limiter** - `Limit.PerSecond` and `Limit.PerWindow` - refills against the wall clock, and a simulation has none. Five virtual minutes are a few real milliseconds, so one would hand out a single window of permits and refuse everything afterwards: a limit nobody configured.
> - **A queueing limiter** - any built with a `queueLimit` above zero - ends its wait when another caller releases a permit, and the platform resumes that wait on the thread pool. Every other suspension in a run is on the virtual clock and resumes on the one thread the run drives everything from. Build it with `queueLimit: 0`, which is the default and what the library recommends anyway.
>
> `Limit.Concurrency` and `Limit.Adaptive` are what a simulation can answer for, and they are the two worth asking about: a rate limit caps a number the load already fixes, while a bulkhead and an adaptive limit both *react* to the dependency getting slow. `Limit.Adaptive` runs its whole discovery loop on simulated seconds, so a run reports the limit it settled on.

## Compare two configurations

The seed fixes the run, so two simulations that differ in one setting differ only because of that setting.

One seed is still one sample. Arrival gaps, latency draws, failure draws, and backoff jitter all come off it, so the numbers a tuning decision turns on - the worst second's amplification, the time to recover, how many times the breaker tripped - move from seed to seed even where the averages hold still. Two policies compared on seed 42 can swap places on seed 43, and nothing in a single pair of reports says which happened.

`RunAll` runs the same scenario once per seed and reports a [`Band`](../reference/testing.md) per measurement - `Median`, `Minimum`, `Maximum`, and `Spread` - instead of a number. Nothing sleeps, so twenty seeds cost twenty times almost nothing. `Separates` is the comparison worth making: two bands that overlap are two policies this scenario cannot tell apart, and saying so is the point.

The seeds run at the same time as each other, on as many threads as the machine has. A run builds its own clock, breaker, retry budget and limiter and seeds its own thread's jitter stream, so a band reports exactly what the same seeds report run one at a time - the threads make it finish sooner and change nothing else. The gain is bounded by allocation rather than by cores, so a host that runs large bands should enable [server GC](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector). A simulation configured with `WithLimiter` runs its seeds one at a time instead, because the factory it takes is your code and has always been called one seed at a time.

This is how a configuration argument becomes a number:

<!-- snippet: simulation-compare -->
```csharp
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

var slowing = Dependency
    .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
    .Brownout(after: TimeSpan.FromSeconds(value: 30), slower: 8, lasting: TimeSpan.FromMinutes(value: 1));

// The same seeds and the same dependency, one setting different - so the difference between
// the two bands is the setting rather than the run.
var unbudgeted = Simulate.Policy(policy: api with { Budget = RetryBudget.None })
    .Against(dependency: slowing)
    .Under(load: Load.Constant(perSecond: 500))
    .For(duration: TimeSpan.FromMinutes(value: 5))
    .RunAll(1, 2, 3, 4, 5);

var budgeted = Simulate.Policy(policy: api)
    .Against(dependency: slowing)
    .Under(load: Load.Constant(perSecond: 500))
    .For(duration: TimeSpan.FromMinutes(value: 5))
    .RunAll(1, 2, 3, 4, 5);

// Not "it won on seed 42" - the two ranges do not overlap, so it wins on every seed.
Assert.True(condition: budgeted.LoadMultiplier.Separates(other: unbudgeted.LoadMultiplier));
Assert.True(condition: budgeted.LoadMultiplier.Maximum < unbudgeted.LoadMultiplier.Minimum);
```
<!-- endsnippet -->

## Record a timeline

The counts a report carries say what a run cost. The timeline says how it got there: which attempt the breaker opened between, what the backoff actually delayed by, how far into the brownout the attempt ceiling adapted.

`Recording()` asks for one. It is off by default because a five-minute run at 500 rps raises a few hundred thousand events, and a report read only for its ratios should not pay to keep them.

<!-- snippet: simulation-timeline -->
```csharp
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

var report = Simulate.Policy(policy: api)
    .Against(dependency: Dependency
        .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
        .Failing(rate: 0.2))
    .Under(load: Load.Constant(perSecond: 50))
    .For(duration: TimeSpan.FromSeconds(value: 10))
    .Recording()
    .Run(seed: 42);

// Every event the run raised, in order, each with the virtual time it was raised at. Null
// unless the run was asked to record one, because a five-minute run raises a few hundred
// thousand of them.
var timeline = report.Timeline!;

foreach (var entry in timeline.Take(count: 20))
{
    Console.WriteLine(value: entry);
}

// Which makes a claim about ordering an assertion rather than an argument: the backoff a
// retry served is on the event that scheduled it.
var retry = timeline.First(entry => entry.Event.Kind == CallEventKind.Retrying);

Assert.NotNull(@object: retry.Event.Delay);
Assert.True(condition: retry.At > TimeSpan.Zero);
```
<!-- endsnippet -->

Recording changes nothing about what the run does. The events are the ones the policy already raises to its listener, and a run is single-threaded on a virtual clock, so the time beside each one is read rather than measured - a recorded run and an unrecorded one from the same seed print the same report.

## Simulate a call graph

`Simulate.Policy` answers what one policy costs one dependency, which is the question a policy's author has. The question a team has is the other one: **when the bank browns out, does the checkout fall over?**

That is a property of a call graph rather than of a policy. A retry storm is B retrying C while A retries B, and B's breaker opening changes the load A offers - so no arrangement of a single-dependency run can show one. `Simulate.Topology` runs the graph:

<!-- snippet: simulation-topology -->
```csharp
var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10) };

var report = Simulate.Topology()
    .Calls(caller: "checkout", callee: "payments", policy: api with { Name = "payments" })
    .Calls(caller: "payments", callee: "bank", policy: api with { Name = "bank" })
    .Leaf(name: "bank", dependency: Dependency
        .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 100))
        .Brownout(after: TimeSpan.FromSeconds(value: 10), slower: 10, lasting: TimeSpan.FromSeconds(value: 15)))
    .Under(load: Load.Constant(perSecond: 200), at: "checkout")
    .For(duration: TimeSpan.FromSeconds(value: 40))
    .Run(seed: 42);

// Each policy retries about as much as it was told to.
var upper = report.On(caller: "checkout", callee: "payments");
var lower = report.On(caller: "payments", callee: "bank");

// And the thing at the bottom feels the product of them - the number nobody configured, and
// the one no single-dependency run can show you.
Assert.True(condition: lower.Amplification > upper.Amplification);

// The calls payments makes are the attempts checkout sent it.
Assert.Equal(expected: upper.Reached, actual: lower.Calls);
```
<!-- endsnippet -->

**A policy belongs to a call, not to a service.** A checkout retries its payment provider on different terms than its catalog, so `Calls(caller, callee, policy)` is the unit - and the unit the numbers come back on, because the load multiplier on `payments -> bank` is the number the bank's owner asks for.

Everything else is what it already was. The policies are yours and the executors are the shipping ones; one virtual clock and one random stream drive the whole graph, so a run reproduces to the last byte from its seed exactly as a single-dependency run does. A `Leaf` is the same modeled `Dependency`. A downstream failure is raised to the caller as the downstream's own exception, so the caller's classifier judges what actually happened.

### Read the report

| Member | What it answers |
| :--- | :--- |
| `On(caller, callee)` | What one call measured, as an ordinary [`SimulationReport`](#read-the-report). `Calls` is how often the caller invoked it, `Reached` how many attempts got to the callee, and `LoadMultiplier` between them is what the callee feels. |
| `At(service)` | What one service measured. `Calls` is the requests it served, `Availability` the fraction it answered, and `Reached` the attempts it sent downstream across every call it makes - so its `LoadMultiplier` is fan-out and retries together. A leaf sends nothing on, so its multiplier is one. |
| `Edges`, `Entries`, `Services` | What the graph contains. |
| `RunAll(seeds)` | A `TopologyBand`, with `On` and `At` returning a [`SimulationBand`](#compare-two-configurations) each. |

The finding is usually the gap between two of them: every policy behaving exactly as configured while the thing at the bottom feels several times the load anybody asked for.

> [!IMPORTANT]
> **A service makes its calls one after another, in the order they were declared.** This does not model parallel fan-out. Modeling parallel calls as sequential would incorrectly sum wait times and fail to reflect that a failed first call may prevent subsequent ones. Two calls a caller really does make in sequence are exactly this.

### Guards that belong to a process

A [thread pool](#model-the-thread-pool) and a [limiter](#bound-what-leaves-the-process) are properties of one process, and in a graph each service is one.

`WithPool(service, pool)` gives a service its own pool. Every attempt that service makes waits in it, on every call it makes, whether or not the policy on that call is configured to notice. A policy that *does* configure `Saturation` reads **its caller's** pool: the work item waiting for a thread is the outbound call, and the process it waits in is the one making it. A call from a service with no pool reads a pool that never queues, exactly as a single-dependency run with no `WithPool` does.

`WithLimiter` comes two ways. `WithLimiter(caller, callee, limiter)` guards one outbound call - a checkout's bulkhead for its payment provider is not its bulkhead for its catalog. `WithLimiter(service, limiter)` is the process-wide bulkhead: one pool of permits shared by every call the service makes, so it bounds the **total** it has in flight rather than each call separately, and is a much tighter bound than the same number written per call. Twenty permits shared across two calls refuses several times what twenty permits each does.

A service can have both, which is the two-level bulkhead: the service's permit is acquired first, then the call's. Either refusal is counted against the call that was attempting, because a shared limiter cannot say which of its calls was turned away. The same two kinds of limiter are refused as in a single-dependency run, and the message names the service or the call.

> [!IMPORTANT]
> **A shared limiter here will not show a slow callee starving the calls to a healthy one.** That failure needs a service whose calls run at the same time, and a service here [makes its calls one after another](#simulate-a-call-graph): a request holds one permit at a time, and a request whose first call is refused never makes its second. The bound is real and the starvation is not modeled, so the graph says so rather than producing a number that looks like it.

> [!TIP]
> **This is where a pool stall stops being local.** `Saturation` stops a stalled process mistaking itself for a slow dependency. But to everyone *calling* that process, a stall and a slow dependency are still the same thing: in a chain where payments' pool stalls for twenty seconds, the bank answers in its usual time throughout and the checkout's p99 goes from 98 ms to 1.1 s. Payments detects its own episode; the checkout detects nothing, because it has nothing to detect. A graph is the only place that shows.
>
> It compounds with the [attempt ceiling](../features/deadlines.md). A ceiling learned while the pool was healthy describes a dependency that never changed, so once the pool stalls every attempt overruns it - and because a service makes its calls one after another, the call that follows the failed one is never made at all. The cost of the misattribution is a whole downstream call rather than a retry.

### What a graph will not do

- **Cycles are refused.** A run would never finish, and a service that calls itself back is a different simulator.
- **`peers` is refused.** It exists so a single-dependency run can ask "does my retry budget hold when I am one of fifty" without claiming to have simulated fifty policies - and a topology is the place where the peers *do* have policies, so approximating them away is the one thing it should not offer. Offer load at another entry instead.
- **No parallel fan-out, and so no bulkhead starvation.** The shape where one slow callee exhausts a shared bulkhead and starves a service's other calls needs its calls to overlap. Sequential calls cannot produce it.

## What is fake, and what is not

Four things are fake: the clock, the random source, the dependency, and - when you ask for one - this process's own thread pool. The policy is yours, and the executor, the breaker, the retry budget, the classifier, and every estimator are the shipping ones - nothing about the decision logic is re-implemented for the simulator. A simulator that modeled the library would be worse than no simulator.

The report is reproducible to the last byte: `Run(seed)` twice with the same seed and `ToString()` returns the same string, on any operating system and any processor, because the simulator's own arithmetic uses only operations IEEE-754 specifies exactly. Assert on that in your own suite if you like - a run that stops reproducing means something on the path is reading a clock or a random source it does not own.

## Limits

Three, and each is a property of the design rather than a gap to fill:

- **One process.** The simulator cannot model fifty pods making their own decisions. `peers` scales the load without simulating the peers, which is an approximation and says so.
- **The pool is a model, and a coarser one than the dependency.** [`Pool`](#model-the-thread-pool) supplies the queue delay rather than measuring it, and spends it once per attempt where a real deep queue is paid again at every resumption - so the direction of the error is to understate a stall. A process that is *always* starved has no episode to find, in the simulator and in production alike.
- **The dependency is a model.** Latency, capacity, and impairment are the three dimensions it has. A dependency whose failure mode is none of those is one the simulator cannot show you.

A fourth is not a limit so much as a habit: **one seed is one sample.** Reach for [`RunAll`](#compare-two-configurations) rather than `Run` whenever the answer is going to decide a setting.

## Go deeper

- [Fault injection](fault-injection.md) - injecting faults at a rate, in a real process, on real time.
- [Retry budget](../features/retry-budget.md) - the guard the load multiplier is mostly a measurement of.
- [Testing reference](../reference/testing.md) - every member, in a table.
