---
title: Streaming
description: How server-streaming calls are retried until their first message, and why client-streaming and duplex calls pass through.
order: 5
---

# Streaming

The interceptor wraps **server-streaming** calls on the [streaming](../features/streaming.md) semantic the core library already has: an attempt is *start the call and pull one message*, and the first message is the success point.

<!-- snippet: grpc-streaming-consume -->
```csharp
// Retried until the first message arrives. Everything after it is the enumeration
// the server is writing, handed over untouched.
await foreach (var update in call.ResponseStream.ReadAllAsync())
    Console.WriteLine(update);
```
<!-- endsnippet -->

Nothing about the registration changes: a stream uses the same policy, the same classifier, the same breaker, and the same per-service scope as the unary calls beside it.

## What is retried, and what is not

| When the failure happens | What the interceptor does |
| :--- | :--- |
| Before the first message | Classifies it, and retries it like any call. A fresh attempt is a fresh gRPC call. |
| After the first message | Nothing. The `RpcException` reaches your `await foreach` unchanged. |

The line is not a limitation to work around - it is the only honest semantic a stream has. Once your consumer holds a message, it has acted on it: a retry would either duplicate the messages it has already seen, or drop the ones it has not, and there is no third option that does not buffer the whole stream.

Before the first message, a stream is indistinguishable from a call. A connection reset, a `Unavailable`, a throttling reply and a deadline all arrive in that window, which is exactly what the classifier already judges. See [Classification](classification.md).

## The one place the wire deadline differs

For a unary call, the deadline on the wire is the **attempt's** ceiling: `min(AttemptTimeout, time left on the Deadline)`. For a stream it is the **whole call's remaining budget**.

`CallOptions.Deadline` is fixed when the call starts and cannot be moved afterwards, and `AttemptTimeout` bounds only the time to the first message. Writing the attempt ceiling onto a stream would tell the server to hang up on a perfectly healthy stream the moment the ceiling passed - which is the opposite of what an attempt ceiling means here.

| | Unary | Server-streaming |
| :--- | :--- | :--- |
| `grpc-timeout` on the wire | The attempt ceiling, plus `DeadlineSlack` | The remaining `Deadline`, plus `DeadlineSlack` |
| What `AttemptTimeout` bounds | The whole attempt | The time to the first message only |
| A `DeadlineExceeded` after the stream starts | Cannot happen - the ceiling was the attempt's | Yours: the call ran out of budget, and it reaches your enumeration |

Everything else on the [Deadlines](deadlines.md) page applies unchanged, including `DeadlineSlack` and a deadline you set yourself, which is still never overwritten when it is the tighter of the two.

## Ending a stream early

Dispose the call. `IAsyncStreamReader<T>` is not disposable, so disposing the call object is how a consumer says it has read enough - and it is what cancels the enumeration and releases the underlying gRPC call. A `using` on the call - `using var call = client.Watch(request);`, which is what a generated client's own samples show - is enough. Reading the stream after that throws `ObjectDisposedException`.

## Repeatability applies here too

[`IsRepeatable`](idempotency.md) and [`GrpcResilience.SingleShot()`](idempotency.md#per-call-singleshot) gate a stream exactly as they gate a unary call, with the same default. A server-streaming method that is not repeatable gets one attempt, and a failure before its first message reaches you directly.

## Client-streaming and duplex calls pass through

They are not wrapped, and they will not be. The request stream is a source you drive interactively, so repeating the call means re-enumerating something the failed attempt has already partially consumed - the duplicates-or-buffering problem again, with no first-message line to draw. Wrap the *setup* call instead, the way any other callback is wrapped, or retry at the level that knows what the half-sent request meant.

## See also

- [Streaming](../features/streaming.md) - the core primitive, and what it does with a breaker, a budget, and a deadline.
- [Deadlines](deadlines.md) - the per-attempt deadline, the slack, and who bounds what.
- [Idempotency](idempotency.md) - the repeatable-by-default rule and the two ways to change it.
