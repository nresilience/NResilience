# NResilience.AspNetCore

The ASP.NET Core integration for [NResilience](https://github.com/nresilience/NResilience).

## Install

Install the package using the .NET CLI:

```bash
dotnet add package NResilience.AspNetCore
```

## What it adds

### Deadline and criticality propagation (inbound half)

One middleware pass that reads what the caller propagated and publishes it for the rest of the request: the deadline, so any policy configured with
`UseAmbientDeadline = true` is bounded by
`min(its own deadline, the time the caller is still waiting)`, and how much the work matters, so any policy configured with
`UseAmbientCriticality = true` stops hedging a backfill and stops spending a depleted retry budget on one.

```csharp
var app = builder.Build();

app.UseResilienceDeadline();
```

The outbound halves - writing the headers on the way out - are in the core package, on `HttpResilienceOptions.PropagateDeadline` and
`HttpResilienceOptions.PropagateCriticality`. `ReadCriticality` turns the second half of the pass off, and `RejectExpired` refuses a request that arrives
with less time left than the `Reserve` says answering costs.

### Nested-retry propagation

Middleware that reads the marker a retrying caller sent (`X-NResilience-Retrying: 1`), so the outbound handler knows its call is itself a retry and stops
retrying it. Without it, a retry that meets a retry multiplies the attempt count - one caller failure becomes many downstream calls.

```csharp
app.UseResilienceNestedRetry();
```

The outbound half - writing the marker on the way out - is in the core package, on `HttpResilienceOptions.DetectNestedRetries`.
See [nested retries](https://github.com/nresilience/NResilience/blob/main/docs/features/nested-retries.md) for both halves.

### Exception-to-response mapping

An `IExceptionHandler`, registered in DI rather than positioned in a pipeline, that maps the exceptions NResilience throws to the HTTP responses they mean:
`DeadlineExceededException` to 504, `CallRejectedException` and `RateLimitedException` to 503, the latter two with `Retry-After` when the policy had a hint.
Unhandled exceptions fall through to the application's own handlers.

```csharp
builder.Services.AddResilienceExceptionHandler();
builder.Services.AddProblemDetails();
// ...

app.UseExceptionHandler();
```

`AddProblemDetails()` is required by the parameterless `UseExceptionHandler()` overload; call it after `AddResilienceExceptionHandler()`.
See [error responses](https://github.com/nresilience/NResilience/blob/main/docs/http/error-responses.md).

## Why it is a separate package

It is the only part of NResilience that requires ASP.NET Core. A worker or a console app must be able to use `NResilience.Extensions` without it.

## Documentation

See [deadline propagation](https://github.com/nresilience/NResilience/blob/main/docs/features/deadlines.md)
and [criticality](https://github.com/nresilience/NResilience/blob/main/docs/features/criticality.md)
for both halves of each, [nested retries](https://github.com/nresilience/NResilience/blob/main/docs/features/nested-retries.md)
for the retry-rejection marker, and [the cancellation deep dive](https://github.com/nresilience/NResilience/blob/main/docs/deep-dives/cancellation.md)
for what an inherited deadline costs and why it is opt-in.
