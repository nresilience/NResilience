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

## `Simulation`

`Simulation` is a `record` describing one simulation. Every method returns a new value, so a half-built simulation can be shared between tests that vary the rest.

| Member | Description |
| :--- | :--- |
| `Against(Dependency dependency)` | The dependency the policy calls. Required. |
| `Under(Load load)` | The traffic to offer. Required. |
| `For(TimeSpan duration)` | How long to offer load for. Required, and positive. Calls in flight when it elapses are allowed to finish. |
| `Run(int seed)` | Runs the simulation and returns a `SimulationReport`. The same seed produces the same report. |
| `Policy`, `Dependency`, `Load`, `Duration` | What has been set so far. `Dependency` and `Load` are null until set. |

`Run` validates the policy, the dependency, and the load, throwing `ResilienceConfigurationException`. A simulation missing its dependency, its load, or its duration throws `InvalidOperationException` naming the method that was not called.

## `Dependency`

`Dependency` is a `record` describing the thing a simulated policy calls. It is the only modeled part of a simulation: the executor, breaker, retry budget, classifier, and estimators are the real ones.

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
| `Validate()` / `Validated()` | Throws `ResilienceConfigurationException` listing every problem at once, or returns the load. |

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
| `Calls`, `Succeeded`, `Failed`, `Reached` | The raw counts the ratios come from. |
| `Seed`, `Duration` | The seed the run was drawn from, and how long it offered load for. |
| `ToString()` | The report as a fixed block of invariant-formatted text, one measurement per line. |
