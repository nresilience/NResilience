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

If you are building a service that receives requests, you can check for the nested retry header to determine if the caller will retry the operation.

```csharp
bool callerWillRetry = request.Headers.ContainsKey(ResilienceHttp.NestedRetryHeader);
```

When you detect that a caller will retry, you can implement one of the following strategies to reduce amplification:
- **Reduce attempts**: Set the inner attempt count to one.
- **Shorten deadlines**: Reduce the inner deadline so that the caller's retry budget remains useful.

Because the optimal response depends on your specific service architecture, NResilience provides the detection logic and leaves the decision on how to react to your application.
