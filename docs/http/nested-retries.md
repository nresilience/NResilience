---
title: Nested retries
description: How the handler notices that it is already inside a retrying client, and why it reports rather than intervenes.
order: 3
---

# Nested retries

Retries compose multiplicatively, and the amplification is invisible from any single layer: three
layers each retrying three times is 27 attempts at the bottom, and every layer believes it is being
reasonable.

`DetectNestedRetries` is **on by default** and costs one header on a request that is already being
retried.

## What it does

Within one process, a flag on the current execution context says whether this send is running inside
a retrying handler's attempt. Across a process boundary, the header does the same job:

```
X-NResilience-Retrying: 1
```

A retrying handler stamps it on every request it sends. A service that reads it off an inbound request
knows its caller will retry, which is the information it needs to stop retrying again underneath.
`ResilienceHttp.NestedRetryHeader` is the constant.

When nesting is detected, a `NestedRetry` [event](../features/telemetry.md) fires and the call
proceeds. The library reports it and does nothing else: silently dropping the retries you configured
would be a bigger surprise than the amplification.

## Reading it on the inbound side

```csharp
bool callerWillRetry = request.Headers.ContainsKey(ResilienceHttp.NestedRetryHeader);
```

The useful reaction is usually to reduce the inner attempt count to one, or to shorten the inner
deadline so the caller's retry has budget left to be useful. That decision belongs to your service,
so the library states the fact rather than acting on it.

