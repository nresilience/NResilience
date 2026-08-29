# NResilience.AspNetCore

The ASP.NET Core integration for [NResilience](https://github.com/nresilience/NResilience).

## Install

Install the package using the .NET CLI:

```bash
dotnet add package NResilience.AspNetCore
```

## What it adds

### Deadline propagation (inbound half)

Middleware that reads the deadline a caller sent with the request and publishes it for the rest of that request, so any policy configured with
`UseAmbientDeadline = true` is bounded by
`min(its own deadline, the time the caller is still waiting)`.

```csharp
var app = builder.Build();

app.UseResilienceDeadline();
```

The outbound half - writing the header on the way out - is in the core package, on `HttpResilienceOptions.PropagateDeadline`.

### Nested-retry propagation

Middleware that reads the marker a retrying caller sent (`X-NResilience-Retrying: 1`), so the outbound handler knows its call is itself a retry and stops retrying it. Without it, a retry that meets a retry multiplies the attempt count - one caller failure becomes many downstream calls.

```csharp
app.UseResilienceNestedRetry();
```

The outbound half - writing the marker on the way out - is in the core package, on `HttpResilienceOptions.DetectNestedRetries`. See [nested retries](https://github.com/nresilience/NResilience/blob/main/docs/features/nested-retries.md) for both halves.

### Exception-to-response mapping

An `IExceptionHandler`, registered in DI rather than positioned in a pipeline, that maps the exceptions NResilience throws to the HTTP responses they mean: `DeadlineExceededException` to 504, `CallRejectedException` and `RateLimitedException` to 503, the latter two with `Retry-After` when the policy had a hint. Unhandled exceptions fall through to the application's own handlers.

```csharp
builder.Services.AddResilienceExceptionHandler();
builder.Services.AddProblemDetails();
// ...

app.UseExceptionHandler();
```

`AddProblemDetails()` is required by the parameterless `UseExceptionHandler()` overload; call it after `AddResilienceExceptionHandler()`. See [error responses](https://github.com/nresilience/NResilience/blob/main/docs/http/error-responses.md).

## Why it is a separate package

It is the only part of NResilience that requires ASP.NET Core. A worker or a console app must be able to use `NResilience.Extensions` without it.

## Documentation

See [deadline propagation](https://github.com/nresilience/NResilience/blob/main/docs/features/deadlines.md)
for both halves, [nested retries](https://github.com/nresilience/NResilience/blob/main/docs/features/nested-retries.md)
for the retry-rejection marker, and [the cancellation deep dive](https://github.com/nresilience/NResilience/blob/main/docs/deep-dives/cancellation.md)
for what an inherited deadline costs and why it is opt-in.