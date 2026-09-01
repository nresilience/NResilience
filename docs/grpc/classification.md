---
title: Classification
description: What each gRPC status code means for retrying, and how to change any of it.
order: 1
---

# Classification

A gRPC failure does not live where the HTTP classifier looks. Application errors travel in the `grpc-status` trailer on an HTTP `200`, and transport failures surface as an `RpcException` after the HTTP classifier has judged that `200` a success.

`GrpcResilience.Classifier` is [`Classifier.Default`](../features/classification.md) plus one rule: read the `StatusCode` on an `RpcException`.

<!-- snippet: grpc-classifier -->
```csharp
// GrpcResilience.Default is Resilience.Default with this classifier already on it.
var classifier = GrpcResilience.Classifier;

var unavailable = classifier.ClassifyException(new RpcException(new Status(StatusCode.Unavailable, "moving")));
var notFound = classifier.ClassifyException(new RpcException(new Status(StatusCode.NotFound, "no such order")));
var exhausted = classifier.ClassifyException(new RpcException(new Status(StatusCode.ResourceExhausted, "quota")));

Console.WriteLine(unavailable.Kind); // Transient
Console.WriteLine(notFound.Kind); // Permanent - an answer, not a failure
Console.WriteLine(exhausted.Kind); // Throttled - the dependency is defending itself
```
<!-- endsnippet -->

`GrpcResilience.Default` is `Resilience.Default` with that classifier already on it, so `AddGrpcResilience()` needs no classifier argument. See [Classification](../features/classification.md) for verdicts.

## The shipped table

| `StatusCode` | Verdict | Why |
| :--- | :--- | :--- |
| `Unavailable` | Transient | The transport could not reach the method. The canonical retryable status. |
| `DeadlineExceeded` | Transient | A ceiling this side or the peer set expired. A fresh attempt gets a fresh ceiling. |
| `ResourceExhausted` | Throttled | The dependency is defending itself - out of quota, out of memory, out of concurrency. One verdict buys the long backoff curve, no evidence against the breaker, and no charge to the [retry budget](../features/retry-budget.md). |
| `Internal` | Permanent | The server's own bug. Retrying multiplies load against something that is already broken. |
| `Unauthenticated`, `PermissionDenied` | Permanent | Credentials do not fix themselves on a retry. Refresh them in `BeforeAttempt`, which runs before each attempt. |
| `InvalidArgument`, `NotFound`, `AlreadyExists`, `FailedPrecondition`, `OutOfRange`, `Unimplemented`, `DataLoss` | Permanent | Answers, not failures - the same line the HTTP classifier takes with a `404`. |
| `Aborted` | Permanent | A transaction conflict. Whether repeating one is safe depends on the store, so the conservative verdict ships. For an example of changing it, see the following section. |
| `Cancelled` | Permanent | The interceptor translates *your* cancellations before the classifier sees them, so an `RpcException(Cancelled)` that reaches it is a peer that hung up. Repeating a call the other end abandoned is a guess. |
| Anything else | Permanent | `Classifier.Default` does not retry what it does not recognize. Retrying a programming error converts a fast, clear failure into a slow, confusing one. |

## Change any of it

Every row is one line to override. The last rule registered for an exception type is the one that runs, so a rule of yours replaces the shipped one and can fall back to it:

<!-- snippet: grpc-classifier-override -->
```csharp
// Aborted is a transaction conflict, and whether repeating one is safe depends on the store -
// so the shipped verdict is Permanent and this is how a store that wants it says so.
var policy = GrpcResilience.Default with
{
    Classify = GrpcResilience.Classifier.On<RpcException>(
        static e => e.StatusCode == StatusCode.Aborted
            ? Verdict.Transient
            : GrpcResilience.Classifier.ClassifyException(e)),
};
```
<!-- endsnippet -->

`Aborted` is the row most often worth changing. A transactional store that reports write conflicts as `Aborted` and expects the client to retry them is a real shape - just not the only one, and repeating a conflicting write against a store that does not expect it is worse than failing.

## What the classifier does not see

Two things reach the caller without passing the classifier, both deliberate:

- **Your own cancellation.** A cancelled caller token is never a failure, is never retried, and no classifier can override it.
- **Your own attempt timeout.** An attempt that exceeds its ceiling produces an `AttemptTimeoutException`, judged by the executor rather than by a predicate. See [Deadlines](deadlines.md) for how the interceptor keeps a gRPC `DeadlineExceeded` from being confused with one.

## Retry pushback

gRPC carries retry pushback as a `google.rpc.RetryInfo` message inside the `grpc-status-details-bin` trailer. NResilience does not read it: doing so means taking a dependency on `Google.Rpc` and base64-decoding a details field, and the `Throttled` verdict already produces the long backoff curve without it.

If your dependency sends one and you want to honor it, write a classifier rule that returns `Verdict.Throttled(retryAfter)`.
