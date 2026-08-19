---
title: Idempotency
description: Which methods are retried, and how to say that one particular POST is safe to repeat.
order: 1
---

# Idempotency

GET, HEAD, PUT, DELETE, OPTIONS and TRACE are retried. POST and PATCH are not, and neither is any
method the library has not heard of - retrying something unrecognized is a guess, and the direction
to guess in is the one that does not duplicate it.

A retried POST is a duplicate order, a duplicate message or a duplicate charge. Microsoft's standard
handler retries POST by default; the report that it creates duplicates was declined after 33
comments, and an opt-out shipped instead.

## Per request, which is the finer instrument

<!-- snippet: http-repeatable -->
```csharp
// POST is not retried by default, because a retried POST is a duplicate order. Per request,
// this is the finer instrument, and it beats the per-client switch in both directions.
using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/orders") { Content = body };
request.Headers.Add("Idempotency-Key", key);
request.Options.Set(ResilienceHttp.Repeatable, true);

using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
```
<!-- endsnippet -->

`ResilienceHttp.Repeatable` decides when it is set, and it beats `RetryUnsafeMethods` in **both**
directions: `true` retries a POST, and `false` keeps a GET from being repeated whatever its method
says. Whoever writes the request knows something the client registration cannot - such as whether it
carries an idempotency key.

## Per client

`HttpResilienceOptions.RetryUnsafeMethods = true` retries POST and PATCH for a whole client. That is
a statement about every request that client will ever send, so prefer the per-request switch unless
the whole API is genuinely idempotent.

## What a retried request looks like

Each attempt gets a fresh `HttpRequestMessage` carrying the original's method, URI, version, headers
and options. The body is buffered once, before the first attempt, and every attempt gets its own copy
of it - unconditionally, because a retry that works for `StringContent` and throws for
`StreamContent` is exactly the bug that only shows up in production.

The response a retry supersedes is disposed for you. The final response - whether it succeeded or is
the 503 the policy ran out of attempts on - is handed back alive, because you need it, not least to
dispose it yourself.

