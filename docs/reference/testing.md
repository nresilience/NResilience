---
title: Testing reference
description: Reference for the Sequence, EventRecorder, TestPolicy, ScriptedHttpHandler, Chaos, and Simulate tools used for testing resilience policies.
order: 11
---

# Testing reference

The testing utilities live in the `NResilience.Testing` namespace in the `NResilience.Testing` package.

## `Sequence`

The `Sequence` class is a factory for scripted call sequences that return pre-defined results or throw exceptions.

| Member | Description |
| :--- | :--- |
| `Sequence.For<T>(TimeProvider? time = null)` | Creates a scripted sequence that returns values of type `T`. |
| `Sequence.ForVoid(TimeProvider? time = null)` | Creates a scripted sequence for void execution overloads, returning a `VoidSequence`. |

Pass the same `TimeProvider` to the `Sequence` that you gave the resilience policy, so scripted delays stay deterministic and never become real sleeps.

## `Sequence<T>`

`Sequence<T>` defines a series of outcomes served to the policy during a test.

| Member | Description |
| :--- | :--- |
| `Returns(T result)` / `Returns(T result, int count)` | Appends one or more steps that return the specified result. |
| `Throws(Exception)` / `Throws(Exception, int count)` | Appends one or more steps that throw the specified exception. The same exception instance is used for all counts, allowing for reference equality assertions in tests. |
| `Delays(TimeSpan)` | Configures the next step to take the specified amount of time to complete. Multiple calls to `Delays` accumulate. |
| `NextAsync(CancellationToken)` | Serves the next step in the sequence. This is the method typically used as the resilience callback. |
| `CallCount` | The total number of calls served, including any call that exceeded the script length. |
| `Remaining` | The number of steps remaining in the script. |

### Execution behavior

- **Timing**: A step with no delay completes synchronously. A step with a delay suspends execution and observes the provided `CancellationToken`.
- **Bounds**: If a call is made after the script has been exhausted, the sequence throws an `InvalidOperationException` specifying the script length and the call number.
- **Thread Safety**: Building the script is not thread-safe, but serving the script via `NextAsync` is thread-safe.

## `VoidSequence`

What `Sequence.ForVoid()` returns: the same script for callbacks that return nothing. Its `NextAsync` returns a bare `Task`, which is what binds it to the void execution overloads - a `Task<T>` is a `Task`, so a result-returning script would always bind to the generic overload instead.

| Member | Description |
| :--- | :--- |
| `Returns()` / `Returns(int count)` | Appends one or more steps that complete. |
| `Throws(Exception)` / `Throws(Exception, int count)` | Appends one or more steps that throw the specified exception. |
| `Delays(TimeSpan)` | Configures the next step to take the specified amount of time. Accumulates. |
| `NextAsync(CancellationToken)` | Serves the next step. Use it as the resilience callback. |
| `CallCount` | The total number of calls served. |
| `Remaining` | The number of steps remaining in the script. |

## `ScriptedStream`

`ScriptedStream` is a factory for creating scripted cold streams, used with the `RunAsync` and `TryRunAsync` overloads that take an `IAsyncEnumerable<T>` source.

| Member | Description |
| :--- | :--- |
| `ScriptedStream.For<T>(TimeProvider? time = null)` | Creates a scripted stream of `T` elements. Pass the same `TimeProvider` the policy was given, so scripted delays are served on the test clock. |

## `ScriptedStream<T>`

`ScriptedStream<T>` defines a series of stream-shaped outcomes, served one per attempt, in order.

| Member | Description |
| :--- | :--- |
| `Yields(params ReadOnlySpan<T> elements)` | Appends a step that yields the specified elements. |
| `YieldsAfter(TimeSpan delay, params ReadOnlySpan<T> elements)` | Appends a step that yields the elements after waiting the delay before the first one. |
| `YieldsNothing()` | Appends a step that yields nothing, which the streaming path treats as a success. |
| `Throws(Exception)` | Appends a step that throws the exception from its first pull, after any pending delay. |
| `ThrowsAfter(Exception, params ReadOnlySpan<T> elements)` | Appends a step that yields the elements and then throws the exception mid-stream, from the pull after the last element - the fault a source produces after the streaming path has stopped watching. |
| `Delays(TimeSpan)` | Makes the next step wait the delay before its outcome. Multiple calls accumulate. |
| `Next(CancellationToken)` | Serves the next step as a cold source. This is the method typically bound to the streaming `RunAsync` and `TryRunAsync` overloads, as a method group or a static lambda. |
| `CallCount` | How many attempts have started, whether or not their source was ever pulled from. |
| `LiveEnumerators` | How many served enumerators are still undisposed - one while the caller is still enumerating, zero once done. |
| `DisposedEnumerators` | How many served enumerators have been disposed - abandoned by the policy, or finished by the consumer. A retried stream that leaks its losing attempts reads here. |

### Execution behavior

- **Timing**: The delay is served against the `TimeProvider` the stream was given, but observes the token the enumerator was handed - the attempt's token, so attempt ceilings are testable against a fake clock.
- **Bounds**: If the policy starts an attempt after the script has been exhausted, `Next` throws an `InvalidOperationException` specifying the script length and the attempt number.
- **Thread Safety**: Building the script is not thread-safe, but serving is.

## `EventRecorder`

`EventRecorder` captures and asserts on the events a resilience policy emits.

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

## `TestPolicy`

`TestPolicy` provides ready-made `Resilience` values shaped for tests, where sleeping and wall-clock bounds are noise. The policies are not safe to ship: they turn off storm protection so a test pays for neither a sleep nor a wall-clock bound it does not care about.

| Member | Description |
| :--- | :--- |
| `TestPolicy.Instant` | Three attempts, no backoff, and both the deadline and the attempt timeout set to `Timeout.InfiniteTimeSpan`. Storm protection is off. |
| `TestPolicy.InstantHttp` | `Instant` with `Classifier = Classifier.Http` and `Name = "http"`. |
| `TestPolicy.WithClock(TimeProvider time)` | `Instant` on the given test clock, with any breaker the policy carries rebuilt on the same clock. |
| `WithClock(this Resilience policy, TimeProvider time)` | Extension method. Rebases a policy on the given clock, rebuilding the breaker it carries on that clock too. The returned policy carries a new breaker with the same settings and no accumulated state. |

## `ScriptedHttpHandler`

`ScriptedHttpHandler` is an `HttpMessageHandler` serving a scripted sequence of responses, so the HTTP layer can be tested without a transport. The last step repeats, so a script does not have to predict how many attempts the policy will make.

| Member | Description |
| :--- | :--- |
| `Responds(HttpStatusCode status)` | Serves one response with the given status. Returns this handler. |
| `Responds(HttpStatusCode status, int times)` | Serves the status for `times` attempts before the script advances. Returns this handler. |
| `Responds(Func<HttpResponseMessage> response)` | Serves one response built afresh per attempt, so its content can be read each time. Returns this handler. |
| `Responds(Func<HttpResponseMessage> response, int times)` | Builds a fresh response for `times` attempts before the script advances. Returns this handler. |
| `Throws(Func<Exception> exception)` | Throws, for the transport failures a classifier has to see. The factory is called once per attempt, so a reused instance never accumulates a shared stack trace. Returns this handler. |
| `Throws(Func<Exception> exception, int times)` | Throws for `times` attempts before the script advances. Returns this handler. |
| `Requests` | A snapshot of what each attempt sent, in order. The live message is disposed by `HttpClient`, so the snapshot captures the method, URI, and headers before disposal. |
| `CallCount` | How many attempts reached the handler. |
| `CaptureBodies` | Whether `SentRequest.Body` is populated. Off by default; reading a body buffers it. |

## `Chaos`

`Chaos` is a `record` describing a fault-injection profile. It wraps the callback, not the policy, so an injected outcome is classified, retried, counted against the breaker, and logged exactly like a real one. See [Fault injection](../testing/fault-injection.md).

| Member | Default | Description |
| :--- | :--- | :--- |
| `Chaos.None` | - | Injects nothing. `Inject` hands the callback back unwrapped. |
| `Enabled` | `false` | The master switch. While `false`, `Inject` returns the callback it was given. |
| `FaultRate` | `0` | The fraction of calls that fail, from 0 to 1. |
| `Fault` | `null` | `Func<Exception>`. What a failing call throws. `null` throws an `IOException`, which both shipped classifiers call `Transient`. |
| `LatencyRate` | `0` | The fraction of calls that are slowed, from 0 to 1. |
| `Latency` | `TimeSpan.Zero` | How much slower a slowed call is. Served on the attempt's token, so `AttemptTimeout` cuts it short. |
| `Gate` | `null` | `Func<bool>`. Asked before every roll; `false` leaves the call alone and does not consume the random stream. |
| `Seed` | `null` | Fixes the random stream, so an injected count is repeatable. |
| `Time` | `TimeProvider.System` | The clock the injected latency is served against. |
| `Validate()` | - | Throws `ResilienceConfigurationException` listing every problem at once. |
| `Validated()` | - | Runs `Validate()` and returns this profile. |

The two rates are rolled independently, so a call can be both slowed and failed.

## `ChaosExtensions`

| Member | Description |
| :--- | :--- |
| `Inject<T>(Func<CancellationToken, Task<T>> work)` | Wraps the callback. A failing call throws. |
| `Inject<T>(Func<CancellationToken, Task<T>> work, Func<T> outcome)` | The same, but a failing call returns `outcome()` rather than throwing - so the classifier's *result* rules are what judge it. |
| `Inject(Func<CancellationToken, Task> work)` | The void form. |
| `Inject<T>(Func<CancellationToken, ValueTask<T>> work)` | The `ValueTask` form. An inert roll awaits the callback's own `ValueTask` and nothing else. |
| `Inject<T>(Func<CancellationToken, ValueTask<T>> work, Func<T> outcome)` | The `ValueTask` form with result substitution. |
| `Inject(Func<CancellationToken, ValueTask> work)` | The void `ValueTask` form. |

Every overload validates the profile eagerly and returns a callback of the shape it was given. A disabled profile returns the callback itself, so `Inject` costs one branch at composition time and nothing per call.

## `ChaosHandler`

`ChaosHandler` is a `DelegatingHandler` that injects into an `HttpClient` pipeline. Add it **after** `AddResilience()`, which makes it inner to the resilience handler so the policy sees the injected faults.

| Member | Description |
| :--- | :--- |
| `ChaosHandler(Chaos chaos, Func<HttpResponseMessage>? response = null)` | Creates the handler. `response`, when supplied, is returned by a failing request instead of throwing; it is called once per injected failure and must produce a fresh response each time. |
| `Chaos` | The profile this handler was built with. |
| `Injected` | How many requests have been failed. |
| `Slowed` | How many requests have been slowed. |

Chaos applies only to the asynchronous path. This is not a limitation in practice: `HttpResilienceHandler.Send` throws `NotSupportedException`, so pipelines with policies have no synchronous path.

## `SentRequest`

`SentRequest` is a `record` that captures what one attempt sent, before `HttpClient` disposed the message.

| Member | Description |
| :--- | :--- |
| `Method` | The `HttpMethod` of the request. |
| `RequestUri` | The `Uri` of the request. |
| `Headers` | The request headers, copied before disposal. |
| `Body` | The request body, or null when `CaptureBodies` is off. |

## `Simulate`

`Simulate` runs a policy against a modeled dependency on a virtual clock and reports what it cost. See [Simulation](../testing/simulation.md).

| Member | Description |
| :--- | :--- |
| `Simulate.Policy(Resilience policy)` | Starts a simulation of a policy, returning a `Simulation`. The policy's `Time` is replaced by the virtual clock and any breaker it carries is rebuilt on that clock. |
| `Simulate.Topology()` | Starts a simulation of a graph of services calling each other, returning a `Topology`. |

## `Simulation`

`Simulation` is a `record` describing one simulation. Every method returns a new value, so a half-built simulation can be shared between tests that vary the rest.

| Member | Description |
| :--- | :--- |
| `Against(Dependency dependency)` | The dependency the policy calls. Required. |
| `Under(Load load)` | The traffic to offer. Required. |
| `For(TimeSpan duration)` | How long to offer load for. Required, and positive. Calls in flight when it elapses are allowed to finish. |
| `WithPool(Pool pool)` | This process's own thread pool, which is what `Saturation` measures. Optional. Accepted on a policy with no `Saturation`, because the delay is spent either way. |
| `Recording()` | Records every event the run raises into `SimulationReport.Timeline`. Optional, and off by default. |
| `WithLimiter(Func<TimeProvider, RateLimiter> limiter)` | Builds the limiter each run acquires a permit from, against the run's virtual clock. A factory that provides the clock to adaptive limiters and ensures `RunAll` builds one per seed. The limiter is the caller's and is not disposed by the run. |
| `Run(int seed)` | Runs the simulation and returns a `SimulationReport`. The same seed produces the same report. |
| `RunAll(params int[] seeds)` | Runs the simulation once per seed and returns a `SimulationBand`. At least one seed, and no repeats - a repeated seed throws `ArgumentException`, because the second copy would narrow the band rather than widen it. |
| `Policy`, `Dependency`, `Load`, `Duration`, `Pool`, `Records`, `Limiter` | What has been set so far. `Dependency`, `Load`, `Pool` and `Limiter` are null until set. |

`Run` validates the policy, the dependency, the load, and the pool if there is one, throwing `ResilienceConfigurationException`. A simulation missing its dependency, its load, or its duration throws `InvalidOperationException` naming the method that was not called.

A limiter that cannot answer on a virtual clock throws `InvalidOperationException` too: a `ReplenishingRateLimiter` - `Limit.PerSecond`, `Limit.PerWindow` - refills against the wall clock, and a limiter with a `queueLimit` above zero resumes a queued wait on the thread pool. `Limit.Concurrency` and `Limit.Adaptive`, built with `queueLimit: 0`, are what a run can measure.

## `Topology`

`Topology` is a graph of services calling each other. A class rather than a `record` - it holds arrays, so generated equality would compare references - but nothing mutates, so every method returns a new value, similarly to `Simulation`. See [Simulate a call graph](../testing/simulation.md#simulate-a-call-graph).

| Member | Description |
| :--- | :--- |
| `Calls(string caller, string callee, Resilience policy)` | Declares that one service calls another under a policy. Policies are associated with calls. A service is any name that makes a call. |
| `Leaf(string name, Dependency dependency)` | Declares a leaf: a node that makes no further calls, modeled as a `Simulate.Policy` dependency. |
| `Under(Load load, string at)` | Offers traffic at one service. Call it more than once for several entry points. `Load.Peers` must be 1. |
| `For(TimeSpan duration)` | How long to offer load for. Required, and positive. |
| `WithPool(string service, Pool pool)` | One service's own modeled thread pool. Every attempt it makes waits in it; a policy configuring `Saturation` reads its caller's pool. Refused on a leaf, on a name that makes no calls, or twice for one service. |
| `WithLimiter(string caller, string callee, Func<TimeProvider, RateLimiter> limiter)` | The limiter one call acquires from, built per run against the virtual clock. Refused for a call the topology does not make, or twice for one call. |
| `WithLimiter(string service, Func<TimeProvider, RateLimiter> limiter)` | The process-wide bulkhead: one limiter shared by every call the service makes, so it bounds the total in flight rather than each call. Refused on a leaf, on a name that makes no calls, or twice for one service. |

Both kinds of limiter may apply to one call. The service's permit is acquired first, then the call's, and either refusal is counted against the call that was attempting - a shared limiter cannot say which of its calls was turned away. The same replenishing and queueing limiters `Simulation.WithLimiter` refuses are refused here, and the message names the service or the call.
| `Recording()` | Records a timeline per edge, into each `On(caller, callee).Timeline`. |
| `Run(int seed)` | Runs the graph and returns a `TopologyReport`. |
| `RunAll(params int[] seeds)` | Runs it once per seed and returns a `TopologyBand`. No repeats. |
| `Validate()` | Throws `InvalidOperationException` listing every problem at once, then validates each policy, dependency and load. |
| `Edges`, `Duration`, `Records` | What has been declared so far. |

A service makes its calls **in the order they were declared, one after another**. Cycles, self-calls, duplicate calls, a leaf that also makes calls, a callee that was never declared, load offered at a service that makes no calls, and `Load.Peers` above one are all refused.

A call's report carries its own `RefusedByLimiter`. A service's pool is paid before that call's permit is acquired and before the attempt counts as having reached the callee, which is the order a single-dependency run uses.

## `Edge`

`Edge` is one call in a topology: a `Caller` and a `Callee`, and a `ToString()` of `caller -> callee`.

## `TopologyReport`

What a graph run measured. The same seed produces the same report.

| Member | Description |
| :--- | :--- |
| `On(string caller, string callee)` | What one call measured, as a `SimulationReport`. `Calls` is the caller's invocations, `Reached` the attempts that got to the callee, and `TimeToRecover` is measured against the callee's own impairment - null for a call into a service, because only a leaf is impaired on a schedule. |
| `At(string service)` | What one service measured, as a `SimulationReport`. `Calls` is requests served, `Reached` is attempts sent downstream across every call it makes. A leaf's `Reached` equals its `Calls`, so its multiplier is one. |
| `Edges`, `Entries`, `Services` | The calls declared, the services load was offered at, and every name in the graph. |
| `Seed`, `Duration`, `EngineVersion` | What the run was. |
| `ToString()` | Every entry then every call, as a fixed block of invariant-formatted text. |

## `TopologyBand`

What a set of seeds measured over a graph. `On(caller, callee)` and `At(service)` each return a `SimulationBand`; `Reports`, `Seeds`, `Edges`, `Entries`, `Services`, `Duration` and `EngineVersion` are as `TopologyReport` has them.

## `Dependency`

`Dependency` is a `record` describing the thing a simulated policy calls. With [`Pool`](#pool) it is one of two modeled parts of a simulation: the executor, breaker, retry budget, classifier, and estimators are the real ones.

| Member | Description |
| :--- | :--- |
| `Dependency.Healthy(TimeSpan p50, TimeSpan p99)` | A dependency that answers every call, with the latency spread these two quantiles describe. `p50` must be positive and `p99` at least `p50`. |
| `Brownout(TimeSpan after, double slower, TimeSpan lasting)` | A stretch during which the dependency is slower but still answering. `slower` must be at least 1. |
| `Outage(TimeSpan after, TimeSpan lasting)` | A stretch during which every call fails immediately. |
| `Failing(double rate)` | The fraction of calls that fail while nothing else is wrong, from 0 to 1. A failure is an `IOException`. |
| `Capacity(int concurrent)` | Calls served at once before queueing begins. Past the bound the dependency slows in proportion; past three times it fails immediately. Zero, the default, is unbounded. |
| `P50`, `P99`, `FailureRate`, `Concurrency` | What has been set. |
| `Validate()` / `Validated()` | Throws `ResilienceConfigurationException` listing every problem at once, or returns the dependency. |

Impairments compose: two overlapping brownouts multiply, and an outage inside a brownout fails.

## `Load`

`Load` is a `record` describing the traffic a simulation offers.

| Member | Description |
| :--- | :--- |
| `Load.Constant(int perSecond, int peers = 1)` | A steady rate for the whole run. Gaps between arrivals are spread rather than even, so the run contains bursts. |
| `PerSecond` | Calls this process starts per second, on average. |
| `Peers` | How many other processes offer the same rate to the same dependency. Their load counts against `Capacity`; their own retries, breakers, and budgets are not simulated. |
| `Mix(Criticality criticality, double fraction)` | Offers a share of the load at one criticality, published through `AmbientCriticality`. `Criticality.Critical` is the remainder and cannot be set - passing it throws `ArgumentOutOfRangeException`. |
| `ShareOf(Criticality criticality)` | The share offered at one level. `Critical` is whatever the other three leave, so a load with no mix is entirely critical. |
| `Validate()` / `Validated()` | Throws `ResilienceConfigurationException` listing every problem at once, or returns the load. |

A load with no mix draws nothing from the seed to decide a level, so an unmixed run reproduces exactly as it did before there was a level to name.

## `Pool`

`Pool` is a `record` describing this process's own thread pool - what [`Resilience.Saturation`](resilience.md) measures, and the one term in the library that is about the caller rather than the dependency. Set it with `Simulation.WithPool`.

| Member | Description |
| :--- | :--- |
| `Pool.Healthy(TimeSpan delay)` | A pool whose work items wait `delay` for a thread, always. Must be positive. There is no default, because `Saturation.Multiple` is relative to it. |
| `Stall(TimeSpan after, TimeSpan delay, TimeSpan lasting)` | A stretch during which work items wait `delay` for a thread. Both must be positive. |
| `Delay` | The normal queue delay. |
| `Validate()` / `Validated()` | Throws `ResilienceConfigurationException` listing every problem at once, or returns the pool. |

Overlapping stalls take the deepest rather than compounding, which is where this parts company with `Dependency`: a brownout is a multiplier on a dependency's own work and two of them genuinely stack, while a queue delay is one number about one queue.

The modeled delay is both what the probe reports and what each attempt waits before it reaches the dependency - so a stall reaches availability, latency and the load multiplier, not only `CountOf(CallEventKind.SaturationDetected)`. It is spent once per attempt, where a real deep queue is paid again at every resumption, so the model understates a stall rather than overstating it.

The baseline is modeled as the rolling median the shipping probe computes, so both of its blind spots are reproduced: a stall inside the first `Saturation.MinimumSamples` quarter-seconds of a run is never detected, and a stall that comes to cover more than half the last minute becomes the median and stops being detected while the queue is still deep.

## `SimulationReport`

`SimulationReport` is what a run measured. The same seed produces the same report on any operating system and any processor.

| Member | Description |
| :--- | :--- |
| `LoadMultiplier` | Attempts that reached the dependency divided by calls the caller made. |
| `Amplification` | The worst one-second window's load multiplier, counting only seconds the caller made a call in. |
| `Availability` | The fraction of calls that ended in success, from 0 to 1. |
| `Latency(double quantile)` | Caller-observed latency at a quantile, over every call in the run by nearest rank. Includes retries and backoff. Throws `ArgumentOutOfRangeException` outside 0 to 1. |
| `BreakerOpens` | How many times a breaker tripped. |
| `TimeToRecover` | How long after the last impairment ended before a full second of calls all succeeded. Null when the dependency was never impaired, or when the run ended first. |
| `CountOf(CallEventKind kind)` | How many events of one kind the run raised. |
| `AvailabilityAt(Criticality criticality)` | The fraction of calls at one criticality that ended in success, and zero when none were offered at it. The measurement `Availability` averages away. |
| `CallsAt(Criticality criticality)`, `SucceededAt(Criticality criticality)` | The counts `AvailabilityAt` comes from. Every call is `Critical` unless the load was mixed. |
| `Calls`, `Succeeded`, `Failed`, `Reached` | The raw counts the ratios come from. |
| `RefusedByLimiter` | Attempts a limiter refused before they could leave the process. Not counted in `Reached`, and charged to neither the breaker nor the retry budget. |
| `Seed`, `Duration` | The seed the run was drawn from, and how long it offered load for. |
| `Timeline` | Every event the run raised as `TimelineEntry` values, in order. Null unless `Simulation.Recording()` asked for one. |
| `EngineVersion` | Which build of the library produced the report. Deliberately absent from `ToString()`, so the block of text a determinism test pins does not change every release. |
| `ToString()` | The report as a fixed block of invariant-formatted text, one measurement per line. |

## `TimelineEntry`

`TimelineEntry` is one event a run raised and the virtual time it was raised at. A `readonly struct` rather than a record: a `CallEvent` carries an exception and a result, so value equality over one would compare two references and call the answer a measurement.

| Member | Description |
| :--- | :--- |
| `At` | How far into the run the event was raised, on the virtual clock. |
| `Event` | The [`CallEvent`](events.md). |
| `ToString()` | The entry as one invariant-formatted line: the virtual time, then the event's own layout. |

## `SimulationBand`

`SimulationBand` is what a set of seeds measured - the same scenario run once per seed, reported as a range per measurement rather than a number per measurement. Returned by `Simulation.RunAll`.

| Member | Description |
| :--- | :--- |
| `Availability`, `LoadMultiplier`, `Amplification`, `BreakerOpens`, `Calls`, `RefusedByLimiter` | The report's ratios and counts as a `Band`. |
| `Latency(double quantile)`, `TimeToRecover` | The report's durations as a `TimeBand`. `TimeToRecover` is null when no run recovered, and covers only the runs that did. |
| `CountOf(CallEventKind kind)` | How many events of one kind the runs raised, as a `Band`. |
| `AvailabilityAt(Criticality criticality)`, `CallsAt(Criticality criticality)` | The per-criticality measurements as a `Band`. |
| `Recovered` | How many of the runs recovered at all. A count below `Seeds.Count` is the finding. |
| `Reports`, `Seeds` | The individual reports and the seeds they came from, in the order the seeds were given. |
| `Duration`, `EngineVersion` | The same for every seed - they are inputs. |
| `ToString()` | The band as a fixed block of invariant-formatted text, in the layout `SimulationReport.ToString()` uses. |

## `Band` and `TimeBand`

One measurement across a set of seeds. `Band` carries doubles and `TimeBand` carries `TimeSpan`s; both are `readonly struct`s with the same four members.

| Member | Description |
| :--- | :--- |
| `Median` | The middle value by nearest rank - a value one of the runs actually produced, not an average of two that neither did. |
| `Minimum`, `Maximum` | The lowest and highest values across the seeds. |
| `Spread` | `Maximum - Minimum`. Zero is a measurement the seed does not touch. |
| `Separates(Band other)` | Whether the two bands are disjoint. False when they overlap, which is the answer "these two policies are indistinguishable over these seeds". |
| `ToString()` | `median (minimum-maximum)`, invariant-formatted. |
