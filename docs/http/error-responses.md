---
title: Error responses
description: Map the exceptions NResilience throws to the HTTP responses they mean with one registration, no try/catch per endpoint.
order: 4
---

# Error responses

When a deadline expires or a guard refuses a call, NResilience throws one of four exceptions. Unhandled, each becomes a 500 - the response that means "this service is broken" - even though these failures mean "this service's dependency is broken" or "come back later". Every service that cares ends up writing the same try/catch in endpoint after endpoint.

`NResilience.AspNetCore` provides the mapping as an [`IExceptionHandler`](../reference/exceptions.md), so one registration covers every endpoint. It is **opt-in**.

## Turn it on

```csharp
builder.Services.AddResilienceExceptionHandler();
builder.Services.AddProblemDetails();
// ...

app.UseExceptionHandler();
```

`AddProblemDetails()` is required by the parameterless `UseExceptionHandler()` overload. An exception this handler does not recognize is reported unhandled, so it composes with your own handlers in any order, and with MVC's exception filters - that chain-of-responsibility design is why it is a handler and not a middleware.

## What it maps

| Exception | Response | Type |
| :--- | :--- | :--- |
| `DeadlineExceededException` | `504` | `urn:nresilience:deadline-exceeded` |
| `AttemptTimeoutException` | `504` | `urn:nresilience:attempt-timeout` |
| `CallRejectedException`, reason `BudgetExhausted` | `503` | `urn:nresilience:retry-budget-exhausted` |
| `CallRejectedException`, any other reason | `503` | `urn:nresilience:dependency-unavailable` |
| `RateLimitedException` | `503` | `urn:nresilience:rate-limited` |

`Retry-After` is set when the exception carried a hint, rounded up to whole seconds. The status codes are the exception's, not the caller's: `RateLimitedException` defaults to 503, not 429, because a limiter in this process refusing to start a call is not the caller's fault. All four are configurable on `ResilienceExceptionHandlerOptions` - see [`AddResilienceExceptionHandler`](../reference/options.md#addresilienceexceptionhandler-on-iservicecollection).

## Read the response

The body is a [problem document](https://www.rfc-editor.org/rfc/rfc9457.html) - `type`, `title`, `status`, `detail` from the exception's own message, `instance` from the request path:

```json
{
  "type": "urn:nresilience:dependency-unavailable",
  "title": "Dependency Unavailable",
  "status": 503,
  "detail": "The call was rejected: DependencyUnavailable.",
  "instance": "/orders"
}
```

`detail` is the exception's own message, which names no dependency, host, or credential. It never includes how many times this service tried: `IncludeAttemptDetails` is off, because a public caller has no business seeing internal retry structure. Turn it on behind a gateway, or where the caller is your own dashboard.

> [!CAUTION]
> `IncludeAttemptDetails` discloses retry structure - attempt count and elapsed time - to whoever receives the body. It is off by default; leave it off for any response a public caller can see.

Once the response has started, the status cannot be changed. The handler declines to handle the exception and the framework aborts the connection - appending a problem document to a half-written body would produce garbage.

## Go deeper

- The four exceptions, their properties, and who sets `RetryAfter`: [Exceptions](../reference/exceptions.md).
- Why a rejected call waits out the rejection pause before throwing: [the guarded rejection deep dive](../deep-dives/guarded-rejection.md).