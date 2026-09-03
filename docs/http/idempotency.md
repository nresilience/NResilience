---
title: Idempotency
description: Learn which HTTP methods are retried by default and how to mark specific requests as safe for repetition.
order: 1
---

# Idempotency

An HTTP method is **idempotent** when sending the same request multiple times has the same effect as sending it once. A `GET` re-reads the same resource and a `DELETE` removes a resource; neither duplicates anything. A `POST` that creates an order can create several orders if retried.

To prevent duplicates, the NResilience handler only retries methods that are safe to repeat.

## Default retry behavior

The handler retries the following methods:
- `GET`
- `HEAD`
- `PUT`
- `DELETE`
- `OPTIONS`
- `TRACE`

`POST` and `PATCH` are not retried by default. Unrecognized HTTP methods are also treated as unsafe and are not retried, to avoid unintended side effects.

A retried `POST` is a duplicate order, a duplicate message, or a duplicate charge. Microsoft's standard handler retries `POST` by default and offers an opt-out; NResilience inverts that default.

## Mark a request as repeatable

If you know a specific `POST` or `PATCH` request is safe to repeat - it carries an idempotency key, say - mark it repeatable per request. That is the most precise way to control retry behavior.

`MarkRepeatable` writes both halves of that decision in one call, and both matter because they serve different consumers: the `HttpResilience.Repeatable` option tells *this client* the request may be sent again, and the idempotency key header tells *the service* to discard the second copy. A retryable `POST` needs both - the option without a key duplicates the order, and the key without the option is never used.

<!-- snippet: http-repeatable -->
```csharp
// POST is not retried by default, because a retried POST is a duplicate order. Per request,
// this is the finer instrument, and it beats the per-client switch in both directions.
// MarkRepeatable writes both halves: the option this client retries on, and the key the
// service deduplicates on.
using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders") { Content = body };
request.MarkRepeatable(idempotencyKey: key);

using var response = await client.SendAsync(request: request, cancellationToken: cancellationToken);
```
<!-- endsnippet -->

`Idempotency-Key` is an IETF draft rather than a standard, so the header name is a parameter: `request.MarkRepeatable(key, headerName: "X-Request-Id")`. Passing no key at all leaves the headers alone, for a service that does not deduplicate. An existing key on the request is never replaced - two idempotency keys on one request is a request most services reject outright.

The `HttpResilience.Repeatable` option overrides the client-level `RetryUnsafeMethods` setting in both directions:
- Setting it to `true` - what `MarkRepeatable` does - allows a `POST` or `PATCH` request to be retried.
- Setting it to `false` - what `MarkSingleShot()` does - prevents a `GET` or `PUT` request from being retried, whatever its method says.

Both helpers return the same request, so they compose in an initializer.

## Enable retries for a client

Turn on retries for `POST` and `PATCH` across a whole client with `HttpResilienceOptions.RetryUnsafeMethods = true`.

Use that only if the entire API served by the client is genuinely idempotent. For most cases, the per-request `MarkRepeatable` / `MarkSingleShot` pair is safer and more precise.

## Request and response handling

The handler manages requests and responses as follows:

- **Request cloning**: Each retry attempt uses a fresh `HttpRequestMessage`. The handler copies the original method, URI, version, headers, and options.
- **Body buffering**: The request body is buffered once before the first attempt. Every later attempt gets a fresh copy of the buffered content, so both `StringContent` and `StreamContent` work.
- **Response disposal**: The handler disposes any `HttpResponseMessage` superseded by a retry. The final response - success or final failure - is returned to the caller for disposal.
