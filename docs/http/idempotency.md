---
title: Idempotency
description: Learn which HTTP methods are retried by default and how to mark specific requests as safe for repetition.
order: 1
---

# Idempotency

An HTTP method is **idempotent** if sending the same request multiple times has the same effect as sending it once. For example, a `GET` request re-reads the same resource, and a `DELETE` request removes a resource; neither action duplicates data. In contrast, a `POST` request that creates an order can result in multiple orders if retried.

To prevent data duplication, the NResilience handler only retries methods that are safe to repeat.

## Default retry behavior

The handler retries the following methods:
- `GET`
- `HEAD`
- `PUT`
- `DELETE`
- `OPTIONS`
- `TRACE`

`POST` and `PATCH` are not retried by default. Additionally, any unrecognized HTTP methods are treated as unsafe and are not retried to avoid unintended side effects.

A retried `POST` is a duplicate order, a duplicate message, or a duplicate charge. Microsoft's standard handler retries `POST` by default, which creates duplicates; it offers an opt-out. NResilience inverts this default.

## Mark a request as repeatable

If you know a specific `POST` or `PATCH` request is safe to repeat - for example, because it includes an idempotency key - you can mark it as repeatable on a per-request basis. This is the most precise way to control retry behavior.

<!-- snippet: http-repeatable -->
```csharp
// POST is not retried by default, because a retried POST is a duplicate order. Per request,
// this is the finer instrument, and it beats the per-client switch in both directions.
using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders") { Content = body };
request.Headers.Add(name: "Idempotency-Key", value: key);
request.Options.Set(key: ResilienceHttp.Repeatable, value: true);

using var response = await client.SendAsync(request: request, cancellationToken: cancellationToken);
```
<!-- endsnippet -->

The `ResilienceHttp.Repeatable` option overrides the client-level `RetryUnsafeMethods` setting in both directions:
- Setting it to `true` allows a `POST` or `PATCH` request to be retried.
- Setting it to `false` prevents a `GET` or `PUT` request from being retried.

## Enable retries for a client

You can enable retries for `POST` and `PATCH` across an entire client by setting `HttpResilienceOptions.RetryUnsafeMethods = true`. 

Use this setting only if the entire API served by that client is genuinely idempotent. For most scenarios, the per-request `ResilienceHttp.Repeatable` option is safer and more precise.

## Request and response handling

To ensure retries are successful and resource-efficient, the handler manages requests and responses as follows:

- **Request cloning**: Each retry attempt uses a fresh `HttpRequestMessage`. The handler copies the original method, URI, version, headers, and options.
- **Body buffering**: The request body is buffered once before the first attempt. Every subsequent attempt receives a fresh copy of the buffered content, ensuring compatibility with both `StringContent` and `StreamContent`.
- **Response disposal**: The handler automatically disposes of any `HttpResponseMessage` that is superseded by a retry. The final response - whether it is a success or the final failure - is returned to the caller for manual disposal.
