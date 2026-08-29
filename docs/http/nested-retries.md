---
title: Nested retries
description: Learn how NResilience detects nested retry loops across process boundaries and how to use this information to prevent request amplification.
order: 3
---

# Nested retries

Retries compose multiplicatively, which can lead to a phenomenon called "request amplification." If a frontend retries a call three times, calls a backend that retries three times, which calls a database that retries three times, a single user action can result in 27 attempts at the bottom of the stack (3 x 3 x 3). This amplification is often invisible to each individual layer because every service believes it is behaving reasonably by only attempting three retries.

The best way to resolve request amplification is to reduce the attempt count in the inner layers. The layer closest to the failing dependency should handle the majority of the retries, while the layers above it should pass the call through with minimal or no retries of their own.

The NResilience handler helps you identify these nested loops by detecting and reporting them.

## How nested retries are detected

`DetectNestedRetries` is enabled by default. The handler uses two mechanisms to detect nesting:

- **Within a single process**: A flag in the current execution context tracks whether the send operation is already running inside a retrying handler's attempt.
- **Across process boundaries**: The handler adds a specific HTTP header to every request it can retry:

```
X-NResilience-Retrying: 1
```

You can use the `ResilienceHttp.NestedRetryHeader` constant to refer to this header in your code.

The header means the sender has retries enabled for this request - not that this particular request is a retry. It is present on the first attempt, which is the one that matters: by the time a retry goes out, the amplification has already happened.

When the handler detects nesting, it fires a `NestedRetry` [event](../features/telemetry.md) and then proceeds with the call. The library reports the nesting but does not intervene; silently dropping configured retries would be an unexpected behavior that could lead to difficult-to-debug failures.

## Handle nested retries on the inbound side

If you are building a service that receives requests, you can check for the nested retry header to determine if the caller will retry the operation. Check the value, not just the header's presence: an intermediary that forwards unknown headers can add an empty one, and `1` is the only value a retrying handler writes.

```csharp
bool callerWillRetry = request.Headers[ResilienceHttp.NestedRetryHeader]
    .Any(ResilienceNestedRetry.IsMarker);
```

In an ASP.NET Core app, install `NResilience.AspNetCore` and read the marker with one line:

```csharp
app.UseResilienceNestedRetry();
```

Register it before anything that makes an outbound call. The middleware publishes the flag in the execution context, so with it registered, the outbound calls this request makes report `NestedRetry` themselves, in any [telemetry](../features/telemetry.md) listener - which is what makes the middle hop of a chain able to see an amplification it is part of, not just the hop that started it.

Anywhere else, publish the marker yourself:

<!-- snippet: nested-retry-publish -->
```csharp
// In an ASP.NET Core app, UseResilienceNestedRetry() publishes what the caller sent. Anywhere
// else - a queue consumer reading the retrying marker off a message - publish it yourself:
// read the header the message carries and begin the scope with what it means.
string? marker = "1";
using var inbound = ResilienceNestedRetry.Begin(callerRetrying: ResilienceNestedRetry.IsMarker(marker));
```
<!-- endsnippet -->

When you detect that a caller will retry, you can implement one of the following strategies to reduce amplification:
- **Reduce attempts**: Set the inner attempt count to one.
- **Shorten deadlines**: Reduce the inner deadline so that the caller's retry budget remains useful.

Because the optimal response depends on your specific service architecture, NResilience provides the detection logic and leaves the decision on how to react to your application. The middleware is the same shape as [`UseResilienceDeadline`](../features/deadlines.md#propagate-the-deadline-across-a-hop), and the two are usually registered together.

For the full options, see [`UseResilienceNestedRetry`](../reference/options.md#useresiliencenestedretry-on-iapplicationbuilder).
