---
title: Streaming
description: Retry an IAsyncEnumerable source until its first element, then hand the rest of the enumeration to the caller untouched.
order: 11
---

# Streaming

Streaming calls are **opt-in** - they use the same policy as everything else, through the `RunAsync` overloads that take an `IAsyncEnumerable<T>` source. Retry, deadlines, attempt ceilings, the classifier, the breaker, and the retry budget all compose; the only thing that changes is what an attempt is.

An attempt over a stream ends at the **first element**. Before the first element, a stream is indistinguishable from a call: a connection reset, a throttling reply or a deadline all arrive before anything is yielded, and that window is exactly what the existing machinery classifies. After the first element, the call has succeeded - a retry would duplicate or drop work the consumer has already acted on - so the rest of the enumeration passes to the caller untouched.

<!-- snippet: stream-basic -->
```csharp
// The source is cold: each attempt re-invokes it, exactly as the HTTP handler
// builds a fresh request per attempt. Pass the token into whatever you call.
var api = Resilience.Default;

var received = new List<int>();

await foreach (var item in api.RunAsync(ct => streams.Next(ct)))
    received.Add(item);
```
<!-- endsnippet -->

The overloads are the same two shapes every other execution surface uses, so a static lambda can avoid a closure:

<!-- snippet: stream-state -->
```csharp
// The static lambda takes the stream as caller state, so it allocates no closure.
await foreach (var item in Resilience.Default.RunAsync(
                   static (source, ct) => source.Next(ct),
                   streams))
{
    Consume(item);
}
```
<!-- endsnippet -->

## What the policy judges

The **first element is the one verdict point**. It is classified like any result - `OnResult<T>` works, where `T` is the element type - and a non-`Ok` verdict on it is retryable; the consumer never sees that element.

<!-- snippet: stream-classifier -->
```csharp
// `OnResult<T>` judges the first element like any result. A verdict the policy will not
// accept is retried, and on the final attempt it throws CallRejectedException from the
// first MoveNextAsync - the consumer never receives an element the classifier refused.
await foreach (var item in api.RunAsync(ct => streams.Next(ct)))
    received.Add(item);
```
<!-- endsnippet -->

Elements after the first pass through unclassified, because the call already succeeded and re-judging mid-stream data would be a second policy nobody configured.

A stream the policy could not start successfully **throws from the first `MoveNextAsync`**. If the attempts run out on an element the classifier kept refusing, or the classifier calls a verdict `Permanent`, or a guard refuses the retry, the consumer receives nothing: an element does not self-describe its failure the way a response with a status code does, so a one-element stream completing normally would be indistinguishable from success. The verdict, the stop reason, and the attempt log travel on the exception instead - `CallRejectedException`, `DeadlineExceededException`, `AttemptTimeoutException`, or whatever the source threw - exactly the exceptions a failed call throws.

Two outcomes are successes without a verdict point:

- **An empty source that completes** is a success - no element, nothing to judge. The consumer's enumeration yields nothing.
- **A caller who stops pulling** is the consumer's business, as with any enumerable.

## What belongs to the consumer

A fault after the first element propagates out of `MoveNextAsync` verbatim: unclassified, no event raised, nothing recorded against the breaker or the budget. The call succeeded; what the source does afterwards is the consumer's exception, same as any other enumerable.

<!-- snippet: stream-post-start -->
```csharp
// A fault after the first element propagates out of MoveNextAsync verbatim:
// unclassified, no event raised, nothing recorded against the breaker. The call
// succeeded; what the source does afterwards is the consumer's exception.
try
{
    await foreach (var item in Resilience.Default.RunAsync(ct => streams.Next(ct)))
        received.Add(item);
}
catch (InvalidOperationException e)
{
    fault = e;
}
```
<!-- endsnippet -->

> [!NOTE]
> What the breaker samples is **time to the first element**. A stream that opens in 2 ms and dies at minute nine is a fast success to the breaker, because the attempt genuinely ended at the first element. If a dependency always fails at element two, the breaker will not notice - see the [deep dive](../deep-dives/one-executor.md#the-streaming-path) for why that is the honest choice rather than an accident.

## Attempt ceilings and deadlines

`AttemptTimeout` bounds **time to the first element only**. Once the element is in hand the ceiling is disarmed, so a slow middle of a stream never loses the enumeration. `Deadline` and `Backoff` work between attempts exactly as for calls, and the first `MoveNextAsync` throws the exception a failed call would have thrown: `DeadlineExceededException`, `CallRejectedException`, `AttemptTimeoutException`, or the original exception, with the attempt log attached.

## What composes, what is refused

Everything composes except hedging. A hedge is a concurrent second copy of a value-returning attempt; two interleaved enumerables is a buffering problem, not a hedge, so the streaming overloads refuse a hedged policy **at the `RunAsync` call** rather than silently doing nothing. The same policy still runs calls.

<!-- snippet: stream-hedge-refusal -->
```csharp
// A hedge is a concurrent second copy of a value-returning attempt; two interleaved
// enumerables is a buffering problem, not a hedge. The streaming overloads refuse a
// hedged policy at the RunAsync call, and the same policy still runs calls.
Assert.Throws<ResilienceConfigurationException>(() => hedged.RunAsync<int>(static ct => Empty(ct)));
```
<!-- endsnippet -->

`Admit` runs before the first pull and is classified exactly as for calls; `OnEvent` hears stream attempts like any other; `UseAmbientDeadline` composes unchanged.

## Test a streaming policy

`ScriptedStream` is to a streaming source what `Sequence` is to a callback: a script of stream-shaped outcomes served one per attempt, with counters for which attempts started, which were abandoned, and which survived.

<!-- snippet: stream-scripted -->
```csharp
// ScriptedStream is to a streaming source what Sequence is to a callback: a
// script of stream-shaped outcomes, served one per attempt. The counters prove
// which attempts started, which were abandoned, and which survived.
var policy = Resilience.Default with
{
    AttemptTimeout = TimeSpan.FromSeconds(1),
    Backoff = Backoff.None,
};

await foreach (var item in policy.RunAsync(ct => streams.Next(ct)))
    received.Add(item);
```
<!-- endsnippet -->

See [the testing reference](../reference/testing.md) for the full surface.

## gRPC server streaming

A gRPC server-streaming call is this feature with the plumbing already done: `AddGrpcResilience()` wraps it for you on the same first-message semantic. See [gRPC streaming](../grpc/streaming.md).

## Go deeper

[The streaming path](../deep-dives/one-executor.md#the-streaming-path) in the executor deep dive covers the one design point that makes streaming different: the surviving attempt's enumerator and token outlive the loop that produced them, and the timer that armed its ceiling is never returned to the pool.
