---
title: Nested retries
description: Learn how NResilience detects nested retry loops across process boundaries and how to use this information to prevent request amplification.
order: 3
---

# Nested retries

Retries compose multiplicatively, a problem known as "request amplification". If a frontend retries three times, calls a backend that retries three times, which calls a database that retries three times, a single user action produces 27 attempts at the bottom (3 x 3 x 3). Each layer is blind to the amplification because every service sees only its own three retries.

The best fix is to reduce attempt counts in the inner layers: the layer closest to the failing dependency should do most of the retrying, and the layers above it should pass the call through with few or no retries of their own.

The NResilience handler detects and reports nested loops so you can find them.

## How nested retries are detected

`DetectNestedRetries` is on by default. The handler detects nesting two ways:

- **Within a process**: A flag in the current execution context tracks whether the send is already inside a retrying handler's attempt.
- **Across process boundaries**: The handler adds an HTTP header to every request it can retry:

```
X-NResilience-Retrying: 1
```

You can use the `NestedRetry.Header` constant to refer to this header in your code.

The header indicates that the sender has retries enabled for this request, not that this particular request is a retry. It is present on the first attempt - the one that matters - because by the time a retry goes out, the amplification has already happened.

On detecting nesting, the handler fires a `NestedRetry` [event](../features/telemetry.md) and proceeds with the call. The library reports but does not intervene: silently dropping configured retries would be surprising and hard to debug.

## gRPC carries the same marker

The [gRPC interceptor](../grpc/index.md) reports nesting the same way, under the same name. gRPC metadata keys are lowercase ASCII, so the marker travels as:

```
x-nresilience-retrying: 1
```

The marker means the same thing on both transports, which matters because the chain that amplifies is rarely all one protocol. An HTTP frontend calling a gRPC backend that calls an HTTP dependency is exactly the shape where the middle hop cannot see what it is part of, and the marker crosses both hops unchanged.

The in-process half crosses as well: a gRPC call made inside a retrying HTTP handler's attempt is detected without any header, and so is the reverse. To turn this off per client, set `DetectNestedRetries` on `GrpcResilienceOptions`.

## Handle nested retries on the inbound side

If you are building a service that receives requests, you can check for the nested retry header to determine if the caller will retry the operation. Check the value, not just the header's presence: an intermediary that forwards unknown headers can add an empty one, and `1` is the only value a retrying handler writes.

```csharp
bool callerWillRetry = request.Headers[NestedRetry.Header]
    .Any(NestedRetry.IsMarker);
```

In an ASP.NET Core app, install `NResilience.AspNetCore` and read the marker with one line:

```csharp
app.UseResilienceNestedRetry();
```

Register it before anything that makes an outbound call. The middleware publishes the flag in the execution context, so with it registered, the outbound calls this request makes report `NestedRetry` themselves in any [telemetry](../features/telemetry.md) listener - the middle hop of a chain can then see an amplification it is part of, not just the hop that started it.

Anywhere else, publish the marker yourself:

<!-- snippet: nested-retry-publish -->
```csharp
// In an ASP.NET Core app, UseResilienceNestedRetry() publishes what the caller sent. Anywhere
// else - a queue consumer reading the retrying marker off a message - publish it yourself:
// read the header the message carries and begin the scope with what it means.
string? marker = "1";
using var inbound = NestedRetry.Begin(callerRetrying: NestedRetry.IsMarker(marker));
```
<!-- endsnippet -->

When you know a caller will retry, reduce amplification by:
- **Reducing attempts**: Set the inner attempt count to one.
- **Shortening deadlines**: Reduce the inner deadline so the caller's retry budget stays useful.

The right response depends on your architecture, so NResilience provides the detection and leaves the reaction to you. The middleware is the same shape as [`UseResilienceDeadline`](../features/deadlines.md#propagate-the-deadline-across-a-hop), and the two are usually registered together.

For the full options, see [`UseResilienceNestedRetry`](../reference/options.md#useresiliencenestedretry-on-iapplicationbuilder).
